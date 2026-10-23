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

public sealed record DomesticUsage(decimal Balance, decimal TodayCost);

public sealed class BalanceService : IDisposable
{
    private const string Origin = "https://xc.lifesecretary.com:8000";
    // pucn is a new-api instance: username login with a session cookie, quota in credits.
    private const string DomesticOrigin = "https://pucn.lifesecretary.com:9000";
    private const decimal DefaultQuotaPerUnit = 500 * 1000m;
    private readonly HttpClient client;
    private readonly HttpClient domestic;
    private readonly string cachePath;
    private string? domesticToken;
    private string domesticIdentity = "";
    private DateTimeOffset domesticExpires;
    // The site allows only 20 login attempts per 20 minutes per IP, so back off on 429 instead of retrying every refresh.
    private DateTimeOffset domesticRetryAt;
    public BalanceService(HttpClient? client = null, string? cachePath = null)
    {
        this.client = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        this.client.Timeout = TimeSpan.FromSeconds(20);
        domestic = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
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
    public async Task<DomesticUsage> ReadDomesticAsync(string username, string protectedPassword, CancellationToken cancellation)
    {
        var password = LocalSecret.Unprotect(protectedPassword);
        if (string.IsNullOrWhiteSpace(username) || password.Length == 0)
            throw new InvalidOperationException("请在设置中填写国内账户用户名和密码。");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var token = await DomesticTokenAsync(username, password, cancellation);
            using var self = await DomesticGetAsync("/api/user/self", token, cancellation);
            if (self == null) continue;
            var unit = await DomesticQuotaPerUnitAsync(token, cancellation);
            var data = self.RootElement.GetProperty("data");
            if (!data.TryGetProperty("quota", out var quota) || !quota.TryGetInt64(out var value))
                throw new InvalidOperationException("国内站点未返回有效余额。");
            return new DomesticUsage(value / unit, await DomesticTodayAsync(token, unit, cancellation));
        }
        throw new InvalidOperationException("国内站点登录已失效，请检查用户名密码。");
    }
    // new-api authenticates the dashboard with a short-lived JWT (data.access_token), not the cookie.
    private async Task<string> DomesticTokenAsync(string username, string password, CancellationToken cancellation)
    {
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(username + "\n" + password)));
        if (domesticToken != null && domesticIdentity == identity && domesticExpires > DateTimeOffset.UtcNow.AddSeconds(30)) return domesticToken;
        if (domesticRetryAt > DateTimeOffset.UtcNow)
            throw new InvalidOperationException($"国内站点请求过于频繁，{(int)Math.Ceiling((domesticRetryAt - DateTimeOffset.UtcNow).TotalSeconds)} 秒后自动重试。");
        using var response = await domestic.PostAsJsonAsync(DomesticOrigin + "/api/user/login?turnstile=", new { username, password }, cancellation);
        using var json = await DomesticJsonAsync(response, cancellation);
        var data = json.RootElement.GetProperty("data");
        if (!data.TryGetProperty("access_token", out var access) || string.IsNullOrWhiteSpace(access.GetString()))
            throw new InvalidOperationException("国内站点登录未返回访问令牌，可能需要额外验证。");
        var seconds = data.TryGetProperty("access_expires_at", out var expiry) && expiry.TryGetInt64(out var at)
            ? Math.Clamp(at - DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 0, 7 * 86400) : 900;
        domesticIdentity = identity;
        domesticExpires = DateTimeOffset.UtcNow.AddSeconds(seconds);
        return domesticToken = access.GetString()!;
    }
    private async Task<JsonDocument?> DomesticGetAsync(string path, string token, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, DomesticOrigin + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await domestic.SendAsync(request, cancellation);
        if (response.StatusCode != HttpStatusCode.Unauthorized) return await DomesticJsonAsync(response, cancellation);
        domesticToken = null;
        return null;
    }
    private async Task<decimal> DomesticQuotaPerUnitAsync(string token, CancellationToken cancellation)
    {
        using var json = await DomesticGetAsync("/api/status", token, cancellation);
        return json != null && json.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("quota_per_unit", out var unit) &&
            unit.TryGetDouble(out var value) && value > 0 ? (decimal)value : DefaultQuotaPerUnit;
    }
    private async Task<decimal> DomesticTodayAsync(string token, decimal unit, CancellationToken cancellation)
    {
        var start = new DateTimeOffset(DateTime.Today).ToUnixTimeSeconds();
        using var json = await DomesticGetAsync($"/api/data/self?start_timestamp={start}&end_timestamp={DateTimeOffset.Now.ToUnixTimeSeconds()}&default_time=hour", token, cancellation);
        if (json == null) throw new InvalidOperationException("国内站点登录已失效，请检查用户名密码。");
        if (!json.RootElement.TryGetProperty("data", out var rows) || rows.ValueKind != JsonValueKind.Array) return 0;
        var total = 0L;
        foreach (var row in rows.EnumerateArray())
            if (row.TryGetProperty("quota", out var quota) && quota.TryGetInt64(out var value)) total += value;
        return total / unit;
    }
    private async Task<JsonDocument> DomesticJsonAsync(HttpResponseMessage response, CancellationToken cancellation)
    {
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retry = Math.Clamp(response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 60, 30, 300);
                domesticRetryAt = DateTimeOffset.UtcNow.AddSeconds(retry);
                throw new InvalidOperationException($"国内站点请求过于频繁，{(int)retry} 秒后自动重试（该站点限制 20 分钟 20 次登录）。");
            }
            throw new InvalidOperationException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "国内站点登录已失效，请检查用户名密码。",
                HttpStatusCode.Forbidden => "国内站点拒绝了请求，请检查账号权限。",
                _ => "国内站点请求失败（HTTP " + (int)response.StatusCode + "）。"
            });
        }
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
        if (!json.RootElement.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
        {
            var message = json.RootElement.TryGetProperty("message", out var value) ? value.GetString() : null;
            json.Dispose();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? "国内站点返回失败，请稍后重试。" : "国内站点：" + message);
        }
        return json;
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
    public void Dispose() { client.Dispose(); domestic.Dispose(); }
    private sealed record TokenCache(string Identity, string Token, DateTimeOffset ExpiresAt);
}
