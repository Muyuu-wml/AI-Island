using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIIsland.Services;
using AIIsland.Modules;

internal static class BalanceChecks
{
    public static async Task Run(string root)
    {
        var directory = Path.Combine(root, "artifacts", "balance-checks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "token.dat");
        var password = LocalSecret.Protect("test-password");
        Check(password != "test-password" && LocalSecret.Unprotect(password) == "test-password", "Password is encrypted and decrypts for current user");
        var handler = new ScriptedHandler();
        handler.Add("/api/v1/auth/login", Login("token-one"), inspect: async r => {
            using var body = JsonDocument.Parse(await r.Content!.ReadAsStringAsync());
            Check(body.RootElement.GetProperty("email").GetString() == "test@example.com" && body.RootElement.GetProperty("password").GetString() == "test-password", "Login sends configured credentials");
        });
        handler.Add("/api/v1/auth/me", Balance, token: "token-one");
        using (var service = new BalanceService(new HttpClient(handler), path))
            Check(await service.ReadAsync("test@example.com", password, CancellationToken.None) == 258.89553202m, "Initial login reads exact decimal balance");
        Check(!File.ReadAllText(path).Contains("token-one"), "Cached token is encrypted on disk");

        handler = new ScriptedHandler();
        handler.Add("/api/v1/auth/me", Balance, token: "token-one");
        handler.Add("/api/v1/auth/me", "{}", HttpStatusCode.Unauthorized, "token-one");
        handler.Add("/api/v1/auth/login", Login("token-two"));
        handler.Add("/api/v1/auth/me", Balance, token: "token-two");
        handler.Add("/api/v1/auth/login", Login("token-three"));
        handler.Add("/api/v1/auth/me", Balance, token: "token-three");
        using (var service = new BalanceService(new HttpClient(handler), path))
        {
            await service.ReadAsync("test@example.com", password, CancellationToken.None);
            Check(handler.Count == 5, "Restart reuses persisted token without login");
            await service.ReadAsync("test@example.com", password, CancellationToken.None);
            Check(handler.Count == 2, "401 triggers exactly one login and balance retry");
            await service.ReadAsync("other@example.com", password, CancellationToken.None);
            Check(handler.Count == 0, "Changing account does not reuse old token");
        }
        handler = new ScriptedHandler();
        handler.Add("/api/v1/auth/me", "{}", HttpStatusCode.Unauthorized, "token-three");
        handler.Add("/api/v1/auth/login", Login("rejected-token"));
        handler.Add("/api/v1/auth/me", "{}", HttpStatusCode.Unauthorized, "rejected-token");
        using (var service = new BalanceService(new HttpClient(handler), path))
        {
            await ExpectFailure(() => service.ReadAsync("other@example.com", password, CancellationToken.None));
            Check(handler.Count == 0 && !File.Exists(path), "Repeated 401 stops retrying and removes rejected token");
        }
        handler = new ScriptedHandler();
        handler.Add("/api/v1/auth/login", Login("expired-token", 0));
        handler.Add("/api/v1/auth/me", Balance, token: "expired-token");
        handler.Add("/api/v1/auth/login", Login("fresh-token"));
        handler.Add("/api/v1/auth/me", "{\"code\":0,\"data\":{}}");
        using (var service = new BalanceService(new HttpClient(handler), path))
        {
            await service.ReadAsync("test@example.com", password, CancellationToken.None);
            await ExpectFailure(() => service.ReadAsync("test@example.com", password, CancellationToken.None));
            Check(handler.Count == 0, "Expired token logs in proactively; missing balance is not treated as zero");
        }
        File.WriteAllText(path, "corrupt-cache");
        handler = new ScriptedHandler();
        handler.Add("/api/v1/auth/login", "{}", HttpStatusCode.Unauthorized);
        using (var service = new BalanceService(new HttpClient(handler), path))
        {
            await ExpectFailure(() => service.ReadAsync("test@example.com", password, CancellationToken.None));
            Check(handler.Count == 0, "Corrupt cache falls back to login; wrong password does not loop");
        }
        File.Delete(path);
        handler = new ScriptedHandler();
        handler.Add("/api/v1/auth/login", Login("scheduled-token"));
        for (var i = 0; i < 4; i++) handler.Add("/api/v1/auth/me", Balance, token: "scheduled-token");
        using (var module = new BalanceModule(new BalanceService(new HttpClient(handler), path)))
        {
            var settings = new Settings { BalanceEnabled = true, BalanceEmail = "test@example.com", BalancePassword = password, BalanceRefreshSeconds = 0 };
            module.Configure(settings);
            await module.StartAsync(CancellationToken.None);
            Check(module.Balance == "258.90" && handler.Count == 3, "Enabled module fetches on startup and displays two decimal places");
            await Task.Delay(10500);
            Check(handler.Count == 3, "Zero interval does not poll automatically");
            module.Refresh.Execute(null);
            Check(handler.Count == 2, "Manual refresh works with automatic polling disabled");
            settings.BalanceRefreshSeconds = 10;
            module.Configure(settings);
            Check(handler.Count == 1, "Saving configuration refreshes immediately");
            await Task.Delay(10500);
            Check(handler.Count == 0, "Configured interval triggers automatic refresh");
            settings.BalanceEnabled = false;
            module.Configure(settings);
            Check(!module.IsVisible && !module.Refresh.CanExecute(null), "Disabled balance module hides and disables manual refresh");
        }
        File.Delete(path);
        Directory.Delete(directory);
    }
    private const string Balance = "{\"code\":0,\"data\":{\"balance\":258.89553202}}";
    private static string Login(string token, int seconds = 86400) => JsonSerializer.Serialize(new { code = 0, data = new { access_token = token, expires_in = seconds } });
    private static async Task ExpectFailure(Func<Task<decimal>> run)
    {
        try { await run(); } catch (InvalidOperationException) { return; }
        throw new Exception("Expected an actionable failure");
    }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>> steps = new();
        public int Count => steps.Count;
        public void Add(string path, string body, HttpStatusCode status = HttpStatusCode.OK, string? token = null, Func<HttpRequestMessage, Task>? inspect = null)
        {
            steps.Enqueue(async request => {
                Check(request.RequestUri!.AbsolutePath == path, "Expected endpoint " + path);
                if (path.EndsWith("login")) Check(request.Method == HttpMethod.Post, "Login uses POST");
                else Check(request.Method == HttpMethod.Get && request.RequestUri.Query.Contains("timezone=Asia%2FShanghai"), "Balance uses GET and Shanghai timezone");
                if (token != null) Check(request.Headers.Authorization?.ToString() == "Bearer " + token, "Correct bearer token");
                if (inspect != null) await inspect(request);
                return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            });
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return steps.Dequeue()(request); }
    }
}
