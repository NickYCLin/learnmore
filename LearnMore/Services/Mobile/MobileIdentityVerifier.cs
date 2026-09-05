using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Apis.Auth;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Tokens;

namespace LearnMore.Services.Mobile;

// Authorization codes are exchanged on the server and can only be used once.
// Client-supplied emails, user IDs and decoded-but-unverified JWTs are never trusted.
public sealed class MobileIdentityVerifier(IConfiguration config, IHttpClientFactory factory, IMemoryCache cache)
    : IMobileIdentityVerifier
{
    private string Setting(string key) => config[$"MobileAuth:{key}"] ?? "";
    public bool GoogleEnabled => config.GetValue<bool>("MobileAuth:Enabled") &&
        Setting("GoogleServerClientId").Length > 0 && Setting("GoogleClientSecret").Length > 0;
    public bool AppleEnabled => config.GetValue<bool>("MobileAuth:Enabled") &&
        new[] { "AppleBundleId", "AppleTeamId", "AppleKeyId", "ApplePrivateKeyPath" }.All(k => Setting(k).Length > 0);

    public async Task<ProviderIdentity> VerifyAsync(MobileLoginRequest request, CancellationToken ct)
    {
        if (request.Provider == "google")
        {
            if (!GoogleEnabled) throw new MobileAuthException("Google 登入暫時無法使用。", 503);
            using var json = await PostAsync("https://oauth2.googleapis.com/token", new()
            {
                ["code"] = request.Code, ["client_id"] = Setting("GoogleServerClientId"),
                ["client_secret"] = Setting("GoogleClientSecret"), ["grant_type"] = "authorization_code", ["redirect_uri"] = ""
            }, ct);
            var idToken = json.RootElement.GetProperty("id_token").GetString()!;
            GoogleJsonWebSignature.Payload payload;
            try
            {
                payload = await GoogleJsonWebSignature.ValidateAsync(idToken, new()
                { Audience = new[] { Setting("GoogleServerClientId") } });
            }
            catch (InvalidJwtException) { throw new MobileAuthException("登入驗證失敗，請重新登入。"); }
            if (!payload.EmailVerified || string.IsNullOrWhiteSpace(payload.Subject) || string.IsNullOrWhiteSpace(payload.Email))
                throw new MobileAuthException("需要已驗證的 Google 電子郵件。");
            // Google is authoritative for Gmail and Workspace addresses, but not arbitrary external mailboxes.
            var authoritative = payload.Email.EndsWith("@gmail.com", StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrWhiteSpace(payload.HostedDomain);
            return new("google", payload.Subject, payload.Email, payload.Name ?? "LearnMore 使用者", authoritative);
        }
        if (request.Provider != "apple") throw new MobileAuthException("不支援的登入方式。", 400);
        if (!AppleEnabled) throw new MobileAuthException("Apple 登入暫時無法使用。", 503);
        if (request.Nonce is null || request.Nonce.Length < 32)
            throw new MobileAuthException("登入驗證已失效，請重新登入。", 400);
        using var apple = await PostAsync("https://appleid.apple.com/auth/token", new()
        {
            ["code"] = request.Code, ["client_id"] = Setting("AppleBundleId"),
            ["client_secret"] = CreateAppleSecret(), ["grant_type"] = "authorization_code"
        }, ct);
        var token = apple.RootElement.GetProperty("id_token").GetString()!;
        var keys = await GetAppleKeysAsync(false, ct);
        ClaimsPrincipal principal;
        try { principal = ValidateAppleToken(token, keys, Setting("AppleBundleId"), request.Nonce); }
        catch (SecurityTokenSignatureKeyNotFoundException)
        {
            // Handle Apple's signing-key rotation without waiting for the cache to expire.
            principal = ValidateAppleToken(token, await GetAppleKeysAsync(true, ct), Setting("AppleBundleId"), request.Nonce);
        }
        var email = principal.FindFirst("email")?.Value ?? "";
        if (principal.FindFirst("email_verified")?.Value is not ("true" or "True"))
            throw new MobileAuthException("Apple 電子郵件尚未驗證。");
        var refreshToken = apple.RootElement.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null;
        if (string.IsNullOrEmpty(refreshToken)) throw new MobileAuthException("Apple 授權不完整，請重新登入。");
        return new("apple", principal.FindFirst("sub")!.Value, email,
            string.IsNullOrWhiteSpace(request.Name) ? "LearnMore 使用者" : request.Name.Trim(), false, refreshToken);
    }

    public static ClaimsPrincipal ValidateAppleToken(string token, IEnumerable<SecurityKey> keys, string audience, string rawNonce)
    {
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var principal = handler.ValidateToken(token, new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = "https://appleid.apple.com",
            ValidateAudience = true, ValidAudience = audience,
            ValidateLifetime = true, RequireExpirationTime = true,
            RequireSignedTokens = true, ValidateIssuerSigningKey = true, IssuerSigningKeys = keys,
            ValidAlgorithms = new[] { SecurityAlgorithms.RsaSha256 }, ClockSkew = TimeSpan.FromSeconds(30)
        }, out _);
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawNonce))).ToLowerInvariant();
        var nonce = principal.FindFirst("nonce")?.Value;
        if (nonce != expected || string.IsNullOrWhiteSpace(principal.FindFirst("sub")?.Value))
            throw new SecurityTokenValidationException("Invalid Apple nonce or subject.");
        return principal;
    }

    private async Task<IEnumerable<SecurityKey>> GetAppleKeysAsync(bool refresh, CancellationToken ct)
    {
        const string key = "MobileAuth.AppleKeys";
        if (!refresh && cache.TryGetValue(key, out IEnumerable<SecurityKey>? saved)) return saved!;
        using var client = factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        var json = await client.GetStringAsync("https://appleid.apple.com/auth/keys", ct);
        var keys = new JsonWebKeySet(json).GetSigningKeys();
        cache.Set(key, (IEnumerable<SecurityKey>)keys, TimeSpan.FromHours(6));
        return keys;
    }

    private string CreateAppleSecret()
    {
        using var ec = ECDsa.Create();
        ec.ImportFromPem(File.ReadAllText(Setting("ApplePrivateKeyPath")));
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(Setting("AppleTeamId"), "https://appleid.apple.com",
            new[] { new Claim("sub", Setting("AppleBundleId")),
                new Claim("iat", new DateTimeOffset(now).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64) },
            now, now.AddMinutes(5), new SigningCredentials(new ECDsaSecurityKey(ec)
            { KeyId = Setting("AppleKeyId") }, SecurityAlgorithms.EcdsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public async Task RevokeAppleAsync(string refreshToken, CancellationToken ct)
    {
        if (!AppleEnabled) throw new MobileAuthException("帳號刪除服務暫時無法使用，請稍後再試。", 503);
        using var client = factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        using var response = await client.PostAsync("https://appleid.apple.com/auth/revoke", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = Setting("AppleBundleId"), ["client_secret"] = CreateAppleSecret(),
            ["token"] = refreshToken, ["token_type_hint"] = "refresh_token"
        }), ct);
        if (!response.IsSuccessStatusCode) throw new MobileAuthException("Apple 授權撤銷失敗，帳號尚未刪除，請稍後重試。", 503);
    }

    private async Task<JsonDocument> PostAsync(string url, Dictionary<string, string> form, CancellationToken ct)
    {
        using var client = factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        using var response = await client.PostAsync(url, new FormUrlEncodedContent(form), ct);
        if (!response.IsSuccessStatusCode) throw new MobileAuthException("登入授權已失效，請重新登入。");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }
}
