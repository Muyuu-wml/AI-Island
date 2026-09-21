using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AIIsland.Services;

public static class LocalSecret
{
    public static string Protect(string value) => string.IsNullOrEmpty(value) ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    public static string Unprotect(string value)
    {
        try { return string.IsNullOrEmpty(value) ? "" : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser)); }
        catch (Exception e) when (e is CryptographicException or FormatException) { return ""; }
    }
}

public sealed record DailyUsage(decimal TodayCost);

public sealed class BalanceService : IDisposable
{
    private const string Origin = "https://xc.lifesecretary.com:8000";
    private readonly HttpClient client;
    private readonly string cachePath;
    public BalanceService(HttpClient? client = null, string? cachePath = null)
    {
        this.client = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        this.client.Timeout = TimeSpan.FromSeconds(20);
        this.cachePath = cachePath ?? Path.Combine(SettingsService.DataDirectory, "balance-token.dat");
    }
    public async Task<decimal> ReadAsync(string email, string protectedPassword, CancellationToken cancellation)
    {
        using var json = await GetAsync("/api/v1/auth/me?timezone=Asia%2FShanghai", email, protectedPassword, cancellation);
        if (!json.RootElement.GetProperty("data").TryGetProperty("balance", out var balance) || !balance.TryGetDecimal(out var value))
            throw new InvalidOperationException("接口未返回有效余额。");
        return value;
    }
    public async Task<DailyUsage> ReadUsageAsync(string email, string protectedPassword, CancellationToken cancellation)
    {
        using var json = await GetAsync("/api/v1/usage/dashboard/stats?timezone=Asia%2FShanghai", email, protectedPassword, cancellation);
        var data = json.RootElement.GetProperty("data");
        if (!data.TryGetProperty("today_cost", out var cost) || cost.ValueKind != JsonValueKind.Number || !cost.TryGetDecimal(out var value))
            throw new InvalidOperationException("接口未返回有效今日消耗。");
        return new DailyUsage(value);
    }
    private async Task<JsonDocument> GetAsync(string path, string email, string protectedPassword, CancellationToken cancellation)
    {
        var password = LocalSecret.Unprotect(protectedPassword);
        if (string.IsNullOrWhiteSpace(email) || password.Length == 0) throw new InvalidOperationException("请在设置中填写账户邮箱和密码。");
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(email + "\n" + password)));
        var cache = Load();
        var token = cache?.Identity == identity && cache.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(30) ? cache.Token : null;
        if (string.IsNullOrEmpty(token)) token = await LoginAsync(email, password, identity, cancellation);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Origin + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request, cancellation);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                File.Delete(cachePath);
                if (attempt == 0) { token = await LoginAsync(email, password, identity, cancellation); continue; }
                throw new InvalidOperationException("登录已失效，请检查账号密码后手动刷新。");
            }
            return await ReadResponseAsync(response, cancellation);
        }
        throw new InvalidOperationException("无法获取账户数据。");
    }
    private async Task<string> LoginAsync(string email, string password, string identity, CancellationToken cancellation)
    {
        using var response = await client.PostAsJsonAsync(Origin + "/api/v1/auth/login", new { email, password }, cancellation);
        using var json = await ReadResponseAsync(response, cancellation);
        var data = json.RootElement.GetProperty("data");
        if (!data.TryGetProperty("access_token", out var access) || string.IsNullOrWhiteSpace(access.GetString()))
            throw new InvalidOperationException("登录未返回 token，可能需要额外验证。");
        var token = access.GetString()!;
        var seconds = data.TryGetProperty("expires_in", out var expiry) && expiry.TryGetInt32(out var n) ? Math.Clamp(n, 0, 31536000) : 3600;
        var cache = new TokenCache(identity, token, DateTimeOffset.UtcNow.AddSeconds(seconds));
        cancellation.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        File.WriteAllText(cachePath + ".tmp", LocalSecret.Protect(JsonSerializer.Serialize(cache)));
        File.Move(cachePath + ".tmp", cachePath, true);
        return token;
    }
    private static async Task<JsonDocument> ReadResponseAsync(HttpResponseMessage response, CancellationToken cancellation)
    {
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "账号密码错误或登录已失效。",
            HttpStatusCode.Forbidden => "请求被拒绝，请检查账号权限或登录验证要求。",
            HttpStatusCode.TooManyRequests => "请求过于频繁，请稍后刷新。",
            _ => "服务请求失败（HTTP " + (int)response.StatusCode + "）。"
        });
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
        if (!json.RootElement.TryGetProperty("code", out var code) || !code.TryGetInt32(out var n) || n != 0)
        { json.Dispose(); throw new InvalidOperationException("接口返回失败，请检查登录信息或稍后重试。"); }
        return json;
    }
    private TokenCache? Load()
    {
        try { return JsonSerializer.Deserialize<TokenCache>(LocalSecret.Unprotect(File.ReadAllText(cachePath))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    public void Dispose() => client.Dispose();
    private sealed record TokenCache(string Identity, string Token, DateTimeOffset ExpiresAt);
}
