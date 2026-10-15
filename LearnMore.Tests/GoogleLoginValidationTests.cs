using Google.Apis.Auth;
using LearnMore.Controllers;
using LearnMore.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LearnMore.Tests;

public sealed class GoogleLoginValidationTests
{
    [Theory]
    [InlineData(null, "csrf", "csrf")]
    [InlineData(" ", "csrf", "csrf")]
    [InlineData("credential", null, "csrf")]
    [InlineData("credential", "csrf", null)]
    [InlineData("credential", " ", " ")]
    [InlineData("credential", "csrf", " ")]
    [InlineData("credential", "csrf", "different")]
    public async Task InvalidInputsNeverReachSignatureVerification(string? credential, string? form, string? cookie)
    {
        var controller = new TestController("configured-client");
        Assert.Null(await controller.VerifyGoogleToken(credential, form, cookie));
        Assert.Equal(0, controller.ValidationCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task MissingClientIdNeverReachesSignatureVerification(string? clientId)
    {
        var controller = new TestController(clientId);
        Assert.Null(await controller.VerifyGoogleToken("credential", "csrf", "csrf"));
        Assert.Equal(0, controller.ValidationCalls);
    }

    [Theory]
    [InlineData(false, "accounts.google.com", false)]
    [InlineData(true, "other.example", false)]
    [InlineData(true, "accounts.google.com", true)]
    [InlineData(true, "https://accounts.google.com", true)]
    public async Task ChecksVerifiedEmailIssuerAndInjectedAudience(bool verified, string issuer, bool accepted)
    {
        var controller = new TestController("in-memory-client");
        controller.Payload.EmailVerified = verified;
        controller.Payload.Issuer = issuer;
        var result = await controller.VerifyGoogleToken("credential", "csrf", "csrf");
        Assert.Equal(accepted, result is not null);
        Assert.Equal(new[] { "in-memory-client" }, controller.Audience);
    }

    [Fact]
    public async Task RejectsExpiredCredentialAndSignatureFailure()
    {
        var controller = new TestController("client");
        controller.Payload.ExpirationTimeSeconds = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds();
        Assert.Null(await controller.VerifyGoogleToken("credential", "csrf", "csrf"));
        controller.Validation = Task.FromException<GoogleJsonWebSignature.Payload>(new InvalidOperationException("invalid signature"));
        Assert.Null(await controller.VerifyGoogleToken("credential", "csrf", "csrf"));
    }

    [Fact]
    public async Task LoginActionAwaitsVerificationBeforeReturningFailure()
    {
        var controller = new TestController("client");
        var completion = new TaskCompletionSource<GoogleJsonWebSignature.Payload>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.Validation = completion.Task;
        controller.Request.Form = new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["credential"] = "credential", ["g_csrf_token"] = "csrf"
        });
        controller.Request.Headers.Cookie = "g_csrf_token=csrf";
        var pending = controller.ValidGoogleLogin();
        Assert.False(pending.IsCompleted);
        controller.Payload.EmailVerified = false;
        completion.SetResult(controller.Payload);
        var result = Assert.IsType<RedirectToActionResult>(await pending);
        Assert.Equal("Login", result.ControllerName);
    }

    [Theory]
    [InlineData("/Mobile/Callback?state=test", true)]
    [InlineData("https://other.example/callback", false)]
    [InlineData("//other.example/callback", false)]
    [InlineData(" ", false)]
    [InlineData(null, false)]
    public void PreservesOnlyLocalMobileRedirectAndConsumesIntent(string? returnUrl, bool local)
    {
        var controller = new TestController("client");
        controller.HttpContext.Session = new TestSession();
        controller.Url = new UrlHelper(controller.ControllerContext);
        if (returnUrl is not null) controller.HttpContext.Session.SetString("MobileLoginReturnUrl", returnUrl);
        var result = controller.CompleteRedirect();
        if (local) Assert.Equal(returnUrl, Assert.IsType<LocalRedirectResult>(result).Url);
        else Assert.Equal("Home", Assert.IsType<RedirectToActionResult>(result).ControllerName);
        Assert.Null(controller.HttpContext.Session.GetString("MobileLoginReturnUrl"));
    }

    private sealed class TestController : LoginController
    {
        public int ValidationCalls { get; private set; }
        public IEnumerable<string>? Audience { get; private set; }
        public Task<GoogleJsonWebSignature.Payload>? Validation { get; set; }
        public GoogleJsonWebSignature.Payload Payload { get; } = new()
        {
            EmailVerified = true, Issuer = "accounts.google.com",
            ExpirationTimeSeconds = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()
        };

        public TestController(string? clientId) : base(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["GoogleApiClientId"] = clientId }).Build(),
            new PersistentLoginSessionService(new EphemeralDataProtectionProvider(), new SessionValidatorFake()))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
                RouteData = new Microsoft.AspNetCore.Routing.RouteData(),
                ActionDescriptor = new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor()
            };
        }

        protected override Task<GoogleJsonWebSignature.Payload> ValidateGoogleCredentialAsync(
            string credential, GoogleJsonWebSignature.ValidationSettings settings)
        {
            ValidationCalls++;
            Audience = settings.Audience;
            Assert.Equal("credential", credential);
            return Validation ?? Task.FromResult(Payload);
        }

        public IActionResult CompleteRedirect() => RedirectAfterGoogleLogin();
    }

    private sealed class TestSession : ISession
    {
        private readonly Dictionary<string, byte[]> _values = new();
        public bool IsAvailable => true;
        public string Id => "test";
        public IEnumerable<string> Keys => _values.Keys;
        public void Clear() => _values.Clear();
        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Remove(string key) => _values.Remove(key);
        public void Set(string key, byte[] value) => _values[key] = value;
        public bool TryGetValue(string key, out byte[] value) => _values.TryGetValue(key, out value!);
    }
}
