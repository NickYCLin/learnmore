using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;

namespace LearnMore.Services;

// 第一版使用單站記憶體工作階段；站台重啟後需重新登入。
public sealed class MobileSessionService(IMemoryCache cache)
{
    private readonly object gate = new();
    public record User(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("name")] string Name);
    private record Grant(User User, string Challenge);
    public static bool ValidChallenge(string? value) => value is not null && Regex.IsMatch(value, "^[A-Za-z0-9_-]{43}$");
    public static bool ValidState(string? value) => value is not null && Regex.IsMatch(value, "^[A-Za-z0-9_-]{32,128}$");

    public string IssueCode(User user, string challenge)
    {
        if (!ValidChallenge(challenge)) throw new ArgumentException("無效的登入驗證碼", nameof(challenge));
        var code = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        cache.Set("mobile-code:" + code, new Grant(user, challenge), TimeSpan.FromMinutes(2));
        return code;
    }

    public (string Token, User User)? Exchange(string? code, string? verifier)
    {
        if (code is null || code.Length != 43 || verifier is null || !Regex.IsMatch(verifier, "^[A-Za-z0-9._~-]{43,128}$")) return null;
        var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        lock (gate)
        {
            if (!cache.TryGetValue<Grant>("mobile-code:" + code, out var grant) || grant is null) return null;
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(challenge), Encoding.ASCII.GetBytes(grant.Challenge))) return null;
            cache.Remove("mobile-code:" + code);
            var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            cache.Set("mobile-token:" + token, grant.User, TimeSpan.FromHours(8));
            return (token, grant.User);
        }
    }

    public User? Read(string? authorization)
    {
        var token = TokenFromHeader(authorization);
        return token is null ? null : cache.Get<User>("mobile-token:" + token);
    }

    public void Revoke(string? authorization)
    {
        var token = TokenFromHeader(authorization);
        if (token is not null) cache.Remove("mobile-token:" + token);
    }

    private static string? TokenFromHeader(string? value)
        => value is not null && value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            && Regex.IsMatch(value[7..], "^[A-Za-z0-9_-]{43}$") ? value[7..] : null;
}
