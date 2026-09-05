using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace LearnMore.Services.Mobile;

public sealed record MobileUser(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("providers")] string[] Providers);

public sealed record MobileSession(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("user")] MobileUser User);

public sealed record ProviderIdentity(string Provider, string Subject, string Email, string Name,
    bool CanMatchLegacyEmail, string? RefreshToken = null);

public sealed class MobileLoginRequest
{
    [Required, RegularExpression("^(google|apple)$")]
    public string Provider { get; set; } = "";
    [Required, StringLength(8192, MinimumLength = 1)]
    public string Code { get; set; } = "";
    [StringLength(128)]
    public string? Nonce { get; set; }
    [StringLength(100)]
    public string? Name { get; set; }
}

public sealed class MobileAuthException(string message, int status = 401) : Exception(message)
{
    public int Status { get; } = status;
}

public interface IMobileIdentityVerifier
{
    bool GoogleEnabled { get; }
    bool AppleEnabled { get; }
    Task<ProviderIdentity> VerifyAsync(MobileLoginRequest request, CancellationToken ct);
    Task RevokeAppleAsync(string refreshToken, CancellationToken ct);
}

public interface IMobileAccountStore
{
    Task<MobileSession> SignInAsync(ProviderIdentity identity, CancellationToken ct);
    Task<MobileUser?> AuthenticateAsync(string token, CancellationToken ct);
    Task SignOutAsync(string token, CancellationToken ct);
    Task LinkAsync(int userId, ProviderIdentity identity, CancellationToken ct);
    Task DeleteAsync(int userId, ProviderIdentity proof, CancellationToken ct);
}
