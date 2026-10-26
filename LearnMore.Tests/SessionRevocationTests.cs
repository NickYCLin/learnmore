using System.Security.Cryptography;
using System.Text;
using LearnMore.Services;
using LearnMore.Services.Mobile;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace LearnMore.Tests;

public class SessionRevocationTests
{
    private const string Verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    private static string Challenge => WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(Verifier)));

    [Fact]
    public void DeletedAccountRejectsAllTokensAndOutstandingCodes()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var accounts = new SessionValidatorFake();
        var sessions = new MobileSessionService(cache, accounts);
        var first = sessions.Exchange(sessions.IssueCode(new(7, "Test"), Challenge), Verifier)!.Value.Token;
        var second = sessions.Exchange(sessions.IssueCode(new(7, "Test"), Challenge), Verifier)!.Value.Token;
        var pending = sessions.IssueCode(new(7, "Test"), Challenge);
        accounts.Available = false;
        Assert.Null(sessions.Read("Bearer " + first));
        Assert.Null(sessions.Read("Bearer " + second));
        Assert.Null(sessions.Exchange(pending, Verifier));
        accounts.Available = true;
        Assert.Null(sessions.Read("Bearer " + first));
        Assert.Null(sessions.Exchange(pending, Verifier));
    }

    [Fact]
    public void DatabaseFailureDoesNotFallBackToCachedAuthorization()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var accounts = new SessionValidatorFake();
        var sessions = new MobileSessionService(cache, accounts);
        var token = sessions.Exchange(sessions.IssueCode(new(7, "Test"), Challenge), Verifier)!.Value.Token;
        accounts.Throw = true;
        Assert.Throws<InvalidOperationException>(() => sessions.Read("Bearer " + token));
        Assert.Throws<InvalidOperationException>(() => sessions.Exchange(sessions.IssueCode(new(7, "Test"), Challenge), Verifier));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeletedWebsiteAccountCannotRestoreOrRenewCookie(bool activeSession)
    {
        var accounts = new SessionValidatorFake();
        var service = new PersistentLoginSessionService(new EphemeralDataProtectionProvider(), accounts);
        var login = Context();
        await service.PersistAsync(login, "test@example.invalid", "7", "Test");
        var cookie = login.Response.Headers.SetCookie.ToString().Split(';')[0];
        var next = Context();
        next.Request.Headers.Cookie = cookie;
        if (activeSession)
        {
            next.Session.SetString("Email", "test@example.invalid");
            next.Session.SetString("UserId", "7");
        }
        accounts.Available = false;
        await service.RestoreSessionAsync(next);
        Assert.Null(next.Session.GetString("Email"));
        Assert.Contains("LearnMore.PersistentSession=;", next.Response.Headers.SetCookie.ToString());
        Assert.Contains("expires=Thu, 01 Jan 1970", next.Response.Headers.SetCookie.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-301)]
    [InlineData(31)]
    public void MutationProofRequiresServerVerifiedFreshAuthentication(int? age)
    {
        var now = DateTimeOffset.UtcNow;
        var proof = new ProviderIdentity("google", "subject", "same@example.invalid", "Test", true,
            AuthenticatedAt: age.HasValue ? now.AddSeconds(age.Value) : null);
        Assert.Throws<MobileAuthException>(() => MobileMutationProofPolicy.RequireFresh(proof, now));
        Assert.Throws<MobileAuthException>(() => MobileMutationProofPolicy.RequireOwner(7, 8));
    }

    private static DefaultHttpContext Context() => new() { Session = new MemorySession() };

    private sealed class MemorySession : ISession
    {
        private readonly Dictionary<string, byte[]> values = new();
        public bool IsAvailable => true;
        public string Id => "test";
        public IEnumerable<string> Keys => values.Keys;
        public void Clear() => values.Clear();
        public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Remove(string key) => values.Remove(key);
        public void Set(string key, byte[] value) => values[key] = value;
        public bool TryGetValue(string key, out byte[] value) => values.TryGetValue(key, out value!);
    }
}
