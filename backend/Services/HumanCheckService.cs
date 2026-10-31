using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FloodRisk.Api.Services;

/// <summary>
/// Keeps bots off the API: a visitor passes a human check once — a typed code (default, see
/// CaptchaService) or Cloudflare Turnstile — and the server issues a signed, expiring session pass that
/// every /api call must carry (X-Session header). "HumanCheck:Mode" = code | turnstile | off.
/// </summary>
public sealed class HumanCheckService
{
    private const string VerifyUrl = "https://challenges.cloudflare.com/turnstile/v0/siteverify";
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);

    private readonly HttpClient _http;
    private readonly ILogger<HumanCheckService> _log;
    private readonly string _secret;
    private readonly byte[] _signingKey;

    public HumanCheckService(HttpClient http, IConfiguration config, ILogger<HumanCheckService> log)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(10);
        _log = log;
        _secret = config["Turnstile:SecretKey"] ?? "";
        Mode = (config["HumanCheck:Mode"] ?? "code").Trim().ToLowerInvariant();
        if (Mode is not ("code" or "turnstile" or "off")) Mode = "code";
        SiteKey = config["Turnstile:SiteKey"] ?? "";
        // A fixed signing key keeps passes valid across restarts; without one, a random key is used
        // (visitors simply verify again after a restart).
        var configured = config["Session:SigningKey"] ?? "";
        _signingKey = configured.Length >= 32 ? Encoding.UTF8.GetBytes(configured) : RandomNumberGenerator.GetBytes(32);
        if (Required && configured.Length < 32)
            _log.LogWarning("Session:SigningKey is not set (or shorter than 32 characters); using a random key, so passes reset on restart.");
    }

    /// <summary>"code" (typed captcha), "turnstile" (Cloudflare) or "off".</summary>
    public string Mode { get; }

    /// <summary>True when the human check is enforced.</summary>
    public bool Required => Mode == "code" || (Mode == "turnstile" && _secret.Length > 0 && SiteKey.Length > 0);

    /// <summary>Public Turnstile site key the page needs to render the widget.</summary>
    public string SiteKey { get; }

    /// <summary>Confirms a Turnstile token with Cloudflare (single use, ~5 minutes valid).</summary>
    public async Task<bool> VerifyAsync(string token, string? remoteIp, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 2048) return false;
        var form = new Dictionary<string, string> { ["secret"] = _secret, ["response"] = token };
        if (!string.IsNullOrEmpty(remoteIp)) form["remoteip"] = remoteIp;
        try
        {
            using var resp = await _http.PostAsync(VerifyUrl, new FormUrlEncodedContent(form), ct);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            bool ok = doc.RootElement.TryGetProperty("success", out var s) && s.GetBoolean();
            if (!ok && doc.RootElement.TryGetProperty("error-codes", out var codes))
                _log.LogInformation("Turnstile rejected a token: {Codes}", codes.ToString());
            return ok;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Turnstile verification failed");
            return false;
        }
    }

    /// <summary>"expiryUnixSeconds.nonce.signature" (base64url), signed with HMAC-SHA256.</summary>
    public (string pass, DateTimeOffset expires) IssuePass()
    {
        var expires = DateTimeOffset.UtcNow.Add(SessionLifetime);
        string body = $"{expires.ToUnixTimeSeconds()}.{B64(RandomNumberGenerator.GetBytes(12))}";
        return ($"{body}.{Sign(body)}", expires);
    }

    public bool IsValidPass(string? pass)
    {
        if (string.IsNullOrEmpty(pass) || pass.Length > 256) return false;
        int last = pass.LastIndexOf('.');
        if (last <= 0) return false;
        string body = pass[..last], sig = pass[(last + 1)..];
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(sig), Encoding.ASCII.GetBytes(Sign(body)))) return false;
        int dot = body.IndexOf('.');
        return dot > 0 && long.TryParse(body[..dot], out var exp) && DateTimeOffset.FromUnixTimeSeconds(exp) > DateTimeOffset.UtcNow;
    }

    private string Sign(string body) => B64(HMACSHA256.HashData(_signingKey, Encoding.ASCII.GetBytes(body)));

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
