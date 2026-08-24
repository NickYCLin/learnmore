using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using System.Text.Json;

namespace LearnMore.Services;

public sealed class PersistentLoginSessionService
{
    public const string CookieName = "LearnMore.PersistentSession";
    private const string DataProtectionPurpose = "LearnMore.PersistentLoginSession.v1";
    private static readonly TimeSpan CookieLifetime = TimeSpan.FromDays(30);
    private readonly IDataProtector _protector;

    public PersistentLoginSessionService(IDataProtectionProvider dataProtectionProvider)
    {
        _protector = dataProtectionProvider.CreateProtector(DataProtectionPurpose);
    }

    public async Task PersistAsync(
        HttpContext httpContext,
        string email,
        string userId,
        string userName,
        string? picture = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        httpContext.Session.SetString("Email", email);
        httpContext.Session.SetString("UserId", userId);
        httpContext.Session.SetString("UserName", userName);
        if (!string.IsNullOrWhiteSpace(picture))
        {
            httpContext.Session.SetString("Picture", picture);
        }

        RenewPersistentCookie(
            httpContext,
            new PersistentLoginSessionPayload(
                email,
                userId,
                userName,
                string.IsNullOrWhiteSpace(picture) ? null : picture,
                DateTimeOffset.UtcNow.Add(CookieLifetime)));

        await httpContext.Session.CommitAsync(cancellationToken);
    }

    public async Task RestoreSessionAsync(HttpContext httpContext, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        await httpContext.Session.LoadAsync(cancellationToken);
        var currentEmail = httpContext.Session.GetString("Email");
        if (!string.IsNullOrWhiteSpace(currentEmail))
        {
            RenewPersistentCookie(httpContext, new PersistentLoginSessionPayload(
                currentEmail,
                httpContext.Session.GetString("UserId"),
                httpContext.Session.GetString("UserName"),
                httpContext.Session.GetString("Picture"),
                DateTimeOffset.UtcNow.Add(CookieLifetime)));
            return;
        }

        if (!httpContext.Request.Cookies.TryGetValue(CookieName, out var protectedPayload) || string.IsNullOrWhiteSpace(protectedPayload))
        {
            return;
        }

        PersistentLoginSessionPayload? payload;
        try
        {
            var json = _protector.Unprotect(protectedPayload);
            payload = JsonSerializer.Deserialize<PersistentLoginSessionPayload>(json);
        }
        catch
        {
            ClearPersistentCookie(httpContext);
            return;
        }

        if (payload is null || payload.ExpiresAt <= DateTimeOffset.UtcNow || string.IsNullOrWhiteSpace(payload.Email))
        {
            ClearPersistentCookie(httpContext);
            return;
        }

        httpContext.Session.SetString("Email", payload.Email);
        httpContext.Session.SetString("UserId", payload.UserId ?? string.Empty);
        httpContext.Session.SetString("UserName", string.IsNullOrWhiteSpace(payload.UserName) ? payload.Email : payload.UserName);
        if (!string.IsNullOrWhiteSpace(payload.Picture))
        {
            httpContext.Session.SetString("Picture", payload.Picture);
        }

        RenewPersistentCookie(httpContext, payload);

        await httpContext.Session.CommitAsync(cancellationToken);
    }

    public void ClearPersistentCookie(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        httpContext.Response.Cookies.Delete(CookieName, BuildCookieOptions(httpContext));
    }

    private void RenewPersistentCookie(HttpContext httpContext, PersistentLoginSessionPayload payload)
    {
        var renewedPayload = payload with { ExpiresAt = DateTimeOffset.UtcNow.Add(CookieLifetime) };
        var protectedPayload = _protector.Protect(JsonSerializer.Serialize(renewedPayload));
        httpContext.Response.Cookies.Append(CookieName, protectedPayload, BuildCookieOptions(httpContext));
    }

    private static CookieOptions BuildCookieOptions(HttpContext httpContext) => new()
    {
        HttpOnly = true,
        IsEssential = true,
        SameSite = SameSiteMode.Lax,
        Secure = httpContext.Request.IsHttps,
        Expires = DateTimeOffset.UtcNow.Add(CookieLifetime),
        MaxAge = CookieLifetime,
        Path = "/"
    };

    private sealed record PersistentLoginSessionPayload(
        string Email,
        string? UserId,
        string? UserName,
        string? Picture,
        DateTimeOffset ExpiresAt);
}
