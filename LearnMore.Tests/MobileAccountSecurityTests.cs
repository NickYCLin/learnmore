using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using LearnMore.Controllers.API;
using LearnMore.Services.Mobile;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace LearnMore.Tests;

public sealed class MobileAccountSecurityTests
{
    [Fact]
    public void RemovingOwnedContentDoesNotRemoveSongsWithSimilarIdentifiers()
    {
        var deleted = new HashSet<string>(StringComparer.Ordinal) { "abc", "xyz" };
        Assert.Equal(new[] { "abcd", "other", "ABC" }, MobileAccountStore.RemoveSongLinks("abc,abcd,other,xyz,ABC", deleted));
    }

    private const string Nonce = "a-client-nonce-with-at-least-32-characters";
    private const string Audience = "com.example.learnmore";

    private static string AppleToken(SecurityKey key, string audience = Audience, string issuer = "https://appleid.apple.com",
        string nonce = Nonce, DateTime? expires = null)
    {
        var token = new JwtSecurityToken(issuer, audience, new[]
        {
            new Claim("sub", "apple-subject"),
            new Claim("nonce", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(nonce))).ToLowerInvariant()),
            new Claim("email", "hidden@privaterelay.appleid.com"), new Claim("email_verified", "true")
        }, DateTime.UtcNow.AddHours(-1), expires ?? DateTime.UtcNow.AddMinutes(5), new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    [Fact]
    public void AppleValidationAcceptsSignedMatchingAudienceIssuerAndNonce()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "test-key" };
        var principal = MobileIdentityVerifier.ValidateAppleToken(AppleToken(key), new[] { key }, Audience, Nonce);
        Assert.Equal("apple-subject", principal.FindFirst("sub")?.Value);
    }

    [Theory]
    [InlineData("audience")]
    [InlineData("issuer")]
    [InlineData("nonce")]
    [InlineData("expiry")]
    [InlineData("signature")]
    public void AppleValidationRejectsForgedOrMismatchedProof(string fault)
    {
        using var rsa = RSA.Create(2048);
        using var attacker = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "test-key" };
        var signing = fault == "signature" ? new RsaSecurityKey(attacker) { KeyId = "test-key" } : key;
        var token = AppleToken(signing, fault == "audience" ? "other-app" : Audience,
            fault == "issuer" ? "https://attacker.example" : "https://appleid.apple.com",
            fault == "nonce" ? "different" : Nonce, fault == "expiry" ? DateTime.UtcNow.AddMinutes(-10) : null);
        Assert.ThrowsAny<SecurityTokenException>(() => MobileIdentityVerifier.ValidateAppleToken(token, new[] { key }, Audience, Nonce));
    }

    [Fact]
    public void AppleValidationRejectsUnsignedTokens()
    {
        var unsigned = new JwtSecurityToken("https://appleid.apple.com", Audience,
            new[] { new Claim("sub", "attacker") }, expires: DateTime.UtcNow.AddMinutes(5));
        using var rsa = RSA.Create(2048);
        Assert.ThrowsAny<SecurityTokenException>(() => MobileIdentityVerifier.ValidateAppleToken(
            new JwtSecurityTokenHandler().WriteToken(unsigned), new[] { new RsaSecurityKey(rsa) }, Audience, Nonce));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Basic credentials")]
    [InlineData("Bearer short")]
    [InlineData("Bearer a,b")]
    public async Task ProtectedEndpointRejectsMissingOrMalformedTokensWithoutDatabaseAccess(string? header)
    {
        var store = new FakeAccountStore();
        var context = Context(header);
        var filter = new MobileAuthorizeFilter(store, Config());
        await filter.OnActionExecutionAsync(context, () => throw new Exception("Action must not run"));
        Assert.IsType<UnauthorizedObjectResult>(context.Result);
        Assert.Equal(0, store.AuthCalls);
    }

    [Fact]
    public async Task ExpiredOrRevokedSessionCannotReachAccountAction()
    {
        var context = Context("Bearer " + new string('A', 64));
        var store = new FakeAccountStore();
        await new MobileAuthorizeFilter(store, Config()).OnActionExecutionAsync(context,
            () => throw new Exception("Action must not run"));
        Assert.Equal(1, store.AuthCalls);
        Assert.IsType<UnauthorizedObjectResult>(context.Result);
    }

    [Fact]
    public async Task CookieOrClientUserIdCannotOverrideBearerIdentity()
    {
        var context = Context("Bearer " + new string('B', 64));
        context.HttpContext.Request.Headers.Cookie = "UserId=999; Email=admin@example.com";
        context.HttpContext.Request.QueryString = new QueryString("?userId=999");
        var expected = new MobileUser(7, "Member", "member@example.com", new[] { "google" });
        var store = new FakeAccountStore { User = expected };
        var invoked = false;
        await new MobileAuthorizeFilter(store, Config()).OnActionExecutionAsync(context, () =>
        {
            invoked = true;
            Assert.Same(expected, context.HttpContext.Items[typeof(MobileUser)]);
            return Task.FromResult(new ActionExecutedContext(context, new List<IFilterMetadata>(), new object()));
        });
        Assert.True(invoked);
    }

    [Fact]
    public async Task DisabledMemberServiceDoesNotQueryDatabase()
    {
        var context = Context("Bearer " + new string('A', 64));
        var store = new FakeAccountStore();
        await new MobileAuthorizeFilter(store, new ConfigurationBuilder().Build()).OnActionExecutionAsync(context,
            () => throw new Exception("Action must not run"));
        Assert.Equal(503, Assert.IsType<ObjectResult>(context.Result).StatusCode);
        Assert.Equal(0, store.AuthCalls);
    }

    [Fact]
    public async Task InvalidDeletionProofNeverCallsDeletionStore()
    {
        var store = new FakeAccountStore();
        var controller = new MobileAccountController(store, new RejectingVerifier())
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        controller.HttpContext.Items[typeof(MobileUser)] = new MobileUser(7, "Member", "member@example.com", new[] { "apple" });
        await Assert.ThrowsAsync<MobileAuthException>(() => controller.Delete(new() { Provider = "apple", Code = "replayed-code" }, default));
        Assert.False(store.Deleted);
    }

    [Theory]
    [InlineData("../invalid")]
    [InlineData("abc]; DROP TABLE Songs;--")]
    [InlineData("abc\n")]
    public async Task InvalidFavoriteIdentifiersCannotTouchTheDatabase(string song)
    {
        var controller = new MobileFavoritesController(new ConfigurationBuilder().Build());
        Assert.IsType<BadRequestResult>(await controller.Add(1, song, default));
        Assert.IsType<BadRequestResult>(await controller.Remove(1, song, default));
    }

    private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["MobileAuth:Enabled"] = "true" }).Build();
    private static ActionExecutingContext Context(string? header)
    {
        var http = new DefaultHttpContext();
        if (header is not null) http.Request.Headers.Authorization = header;
        return new ActionExecutingContext(new ActionContext(http, new RouteData(), new ActionDescriptor()),
            new List<IFilterMetadata>(), new Dictionary<string, object?>(), new object());
    }

    private sealed class FakeAccountStore : IMobileAccountStore
    {
        public int AuthCalls; public bool Deleted; public MobileUser? User;
        public Task<MobileUser?> AuthenticateAsync(string token, CancellationToken ct) { AuthCalls++; return Task.FromResult(User); }
        public Task DeleteAsync(int userId, ProviderIdentity proof, CancellationToken ct) { Deleted = true; return Task.CompletedTask; }
        public Task LinkAsync(int userId, ProviderIdentity identity, CancellationToken ct) => Task.CompletedTask;
        public Task SignOutAsync(string token, CancellationToken ct) => Task.CompletedTask;
        public Task<MobileSession> SignInAsync(ProviderIdentity identity, CancellationToken ct) => throw new NotImplementedException();
    }
    private sealed class RejectingVerifier : IMobileIdentityVerifier
    {
        public bool GoogleEnabled => true; public bool AppleEnabled => true;
        public Task<ProviderIdentity> VerifyAsync(MobileLoginRequest request, CancellationToken ct) => throw new MobileAuthException("Invalid proof");
        public Task RevokeAppleAsync(string refreshToken, CancellationToken ct) => throw new NotImplementedException();
    }
}
