using LearnMore.Controllers;
using LearnMore.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LearnMore.Tests;

public sealed class TestLoginSecurityTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong-token")]
    public async Task TestLogin_ShouldHideEndpointWithoutMatchingSmokeToken(string? providedToken)
    {
        var controller = CreateController();
        if (providedToken is not null)
        {
            controller.HttpContext.Request.Headers["X-LearnMore-Smoke-Token"] = providedToken;
        }

        var result = await controller.TestLogin(new LoginController.TestLoginRequest
        {
            Email = "smoke@example.invalid",
            Password = "wrong-password"
        });

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task TestLogin_ShouldCheckCredentialsAfterSmokeTokenPasses()
    {
        var controller = CreateController();
        controller.HttpContext.Request.Headers["X-LearnMore-Smoke-Token"] = "test-smoke-token";

        var result = await controller.TestLogin(new LoginController.TestLoginRequest
        {
            Email = "smoke@example.invalid",
            Password = "wrong-password"
        });

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    private static LoginController CreateController()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TestAccount:Email"] = "smoke@example.invalid",
                ["TestAccount:Password"] = "test-password",
                ["TestAccount:SmokeToken"] = "test-smoke-token"
            })
            .Build();
        var persistentSession = new PersistentLoginSessionService(
            new EphemeralDataProtectionProvider());

        return new LoginController(configuration, persistentSession)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
    }
}
