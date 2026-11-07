using LearnMore.Controllers;
using LearnMore.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnMore.Tests;

public sealed class ManagerUsersTests
{
    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task HttpReadsRequireManagerAndChangesRequireAntiforgery(int deniedStatus)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        { ApplicationName = typeof(ManagerUsersController).Assembly.FullName, EnvironmentName = "Testing" });
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(ManagerUsersController).Assembly);
        builder.Services.AddSingleton<IErrorReportManagerAccess>(new Access(deniedStatus));
        builder.Services.AddSingleton(new ManagerUserStore(new ConfigurationBuilder().Build()));
        await using var app = builder.Build();
        app.MapControllers();
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        foreach (var path in new[] { "/Management/Users", "/Management/Users/1" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(deniedStatus, (int)response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
        using var change = await client.PostAsync("/Management/Users/1/change", new FormUrlEncodedContent(new Dictionary<string,string>
        { ["action"]="delete", ["confirmation"]="test@example.invalid", ["reason"]="test", ["revision"]="0" }));
        Assert.Equal(400, (int)change.StatusCode);
        using var getChange = await client.GetAsync("/Management/Users/1/change");
        Assert.Equal(405, (int)getChange.StatusCode);
    }

    private sealed class Access(int status) : IErrorReportManagerAccess
    {
        public Task<IActionResult?> AuthorizeAsync(ControllerBase controller, CancellationToken cancellationToken)
            => Task.FromResult<IActionResult?>(new StatusCodeResult(status));
    }
}
