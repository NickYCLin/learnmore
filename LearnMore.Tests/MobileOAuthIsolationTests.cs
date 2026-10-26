using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LearnMore.Services.Mobile;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace LearnMore.Tests;

public sealed class MobileOAuthIsolationTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(-600, false)]
    [InlineData(null, false)]
    public async Task SignedAppleExchangeRequiresAuthenticationTimeNotIssuedTime(int? authenticationAge, bool fresh)
    {
        using var signing = RSA.Create(2048);
        using var clientKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keyFile = Path.Combine(Path.GetTempPath(), "learnmore-synthetic-oauth-" + Guid.NewGuid().ToString("N") + ".pem");
        await File.WriteAllTextAsync(keyFile, clientKey.ExportPkcs8PrivateKeyPem());
        try
        {
            const string nonce = "synthetic-nonce-longer-than-thirty-two-characters";
            var now = DateTimeOffset.UtcNow;
            var claims = new List<Claim>
            {
                new("sub", "synthetic-apple-subject"), new("email", "synthetic@example.invalid"),
                new("email_verified", "true"), new("iat", now.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
                new("nonce", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(nonce))).ToLowerInvariant())
            };
            if (authenticationAge.HasValue)
                claims.Add(new("auth_time", now.AddSeconds(authenticationAge.Value).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64));
            var key = new RsaSecurityKey(signing) { KeyId = "synthetic-key" };
            var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("https://appleid.apple.com", "test.learnmore",
                claims, now.UtcDateTime.AddMinutes(-1), now.UtcDateTime.AddMinutes(5), new SigningCredentials(key, SecurityAlgorithms.RsaSha256)));
            using var handler = new SyntheticAppleHandler(token);
            using var cache = new MemoryCache(new MemoryCacheOptions());
            cache.Set("MobileAuth.AppleKeys", (IEnumerable<SecurityKey>)new[] { key });
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MobileAuth:Enabled"] = "true", ["MobileAuth:AppleBundleId"] = "test.learnmore",
                ["MobileAuth:AppleTeamId"] = "synthetic-team", ["MobileAuth:AppleKeyId"] = "synthetic-client-key",
                ["MobileAuth:ApplePrivateKeyPath"] = keyFile
            }).Build();
            var verifier = new MobileIdentityVerifier(configuration, new SyntheticClientFactory(handler), cache);
            var request = new MobileLoginRequest { Provider = "apple", Code = "synthetic-once", Nonce = nonce };
            var identity = await verifier.VerifyAsync(request, default);
            Assert.Equal("synthetic-apple-subject", identity.Subject);
            if (fresh) MobileMutationProofPolicy.RequireFresh(identity, now);
            else Assert.Throws<MobileAuthException>(() => MobileMutationProofPolicy.RequireFresh(identity, now));
            await Assert.ThrowsAsync<MobileAuthException>(() => verifier.VerifyAsync(request, default));
            await verifier.RevokeAppleAsync(identity.RefreshToken!, default);
            handler.RevokeFails = true;
            var error = await Assert.ThrowsAsync<MobileAuthException>(() => verifier.RevokeAppleAsync(identity.RefreshToken!, default));
            Assert.DoesNotContain("帳號尚未刪除", error.Message);
            Assert.Equal(2, handler.Revocations);
        }
        finally { File.Delete(keyFile); }
    }

    private sealed class SyntheticClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class SyntheticAppleHandler(string token) : HttpMessageHandler
    {
        private bool used;
        public bool RevokeFails { get; set; }
        public int Revocations { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("appleid.apple.com", request.RequestUri!.Host);
            Assert.Equal(HttpMethod.Post, request.Method);
            if (request.RequestUri.AbsolutePath == "/auth/token")
            {
                if (used) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
                used = true;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(JsonSerializer.Serialize(new { id_token = token, refresh_token = "synthetic-refresh" })) });
            }
            Assert.Equal("/auth/revoke", request.RequestUri.AbsolutePath);
            Revocations++;
            return Task.FromResult(new HttpResponseMessage(RevokeFails ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
        }
    }
}
