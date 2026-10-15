using LearnMore.Controllers;
using LearnMore.Controllers.API;
using LearnMore.Services.Mobile;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnMore.Tests;

public class MobileCompatibilityTests
{
    [Fact]
    public void BothClientsHaveDistinctDiscoveredRoutes()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllersWithViews().AddApplicationPart(typeof(MobileApiController).Assembly);
        using var provider = services.BuildServiceProvider();
        var actions = provider.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .Where(action => action.AttributeRouteInfo?.Template?.StartsWith("api/mobile/") == true)
            .SelectMany(action => action.ActionConstraints!.OfType<HttpMethodActionConstraint>()
                .SelectMany(constraint => constraint.HttpMethods.Select(method => new
                {
                    Route = action.AttributeRouteInfo!.Template!, Method = method, Controller = action.ControllerTypeInfo.AsType()
                }))).ToArray();
        Assert.All(actions.GroupBy(action => (action.Method, action.Route)), group => Assert.Single(group));
        Assert.Equal(typeof(MobileApiController), Assert.Single(actions, action => action.Method == "GET" && action.Route == "api/mobile/v1/songs").Controller);
        Assert.Equal(typeof(MobileCatalogController), Assert.Single(actions, action => action.Method == "GET" && action.Route == "api/mobile/v2/songs").Controller);
        Assert.Contains(actions, action => action.Route == "api/mobile/v1/session");
        Assert.Contains(actions, action => action.Route == "api/mobile/v1/account");
    }

    [Theory]
    [InlineData("Privacy")]
    [InlineData("Support")]
    [InlineData("Connect")]
    [InlineData("Approve")]
    [InlineData("Player")]
    public void MobilePagesRemainAvailable(string action)
    {
        Assert.NotNull(typeof(MobileController).GetMethod(action));
    }

    [Theory]
    [InlineData("google", "same-email-new-subject", 7)]
    [InlineData("apple", "different-account", 8)]
    [InlineData("google", "expired-or-replayed", 7)]
    public async Task MutationRetriesFailClosedWithoutOAuthOrDatabase(string provider, string subject, int userId)
    {
        var store = new MobileAccountStore(new ConfigurationBuilder().Build(), new EphemeralDataProtectionProvider());
        var identity = new ProviderIdentity(provider, subject, "same@example.com", "Test", true);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var linkError = await Assert.ThrowsAsync<MobileAuthException>(() => store.LinkAsync(userId, identity, default));
            var deleteError = await Assert.ThrowsAsync<MobileAuthException>(() => store.DeleteAsync(userId, identity, default));
            Assert.Equal(400, linkError.Status);
            Assert.Equal(403, deleteError.Status);
        }
        var controller = new MobileAccountController(store, new UnexpectedVerifier())
        {
            ControllerContext = new() { HttpContext = new DefaultHttpContext() }
        };
        controller.HttpContext.Items[typeof(MobileUser)] = new MobileUser(userId, "Test", identity.Email, [provider]);
        var proof = new MobileLoginRequest { Provider = provider, Code = subject };
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.Link(new MobileLinkRequest { CurrentProof = proof, TargetProof = proof }, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.Delete(proof, default));
    }

    [Fact]
    public void ExistingEmailCannotBootstrapProviderOwnership()
    {
        Assert.Equal(409, Assert.Throws<MobileAuthException>(() => MobileIdentityPolicy.RequireNewAccountEmail(true)).Status);
        MobileIdentityPolicy.RequireNewAccountEmail(false);
    }

    private sealed class UnexpectedVerifier : IMobileIdentityVerifier
    {
        public bool GoogleEnabled => false;
        public bool AppleEnabled => false;
        public Task<ProviderIdentity> VerifyAsync(MobileLoginRequest request, CancellationToken ct) => throw new InvalidOperationException("OAuth must not run");
        public Task RevokeAppleAsync(string refreshToken, CancellationToken ct) => throw new InvalidOperationException("Revoke must not run");
    }
}
