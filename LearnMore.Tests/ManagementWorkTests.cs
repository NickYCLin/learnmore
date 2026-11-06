using System.Net;
using System.Text.RegularExpressions;
using LearnMore.Controllers;
using LearnMore.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnMore.Tests;

public sealed class ManagementWorkTests
{
    [Theory]
    [InlineData(null, 401)]
    [InlineData("member", 403)]
    public async Task NonManagersCannotReadHistory(string? user, int status)
    {
        var store = new Store();
        await using var application = Build(store);
        await application.StartAsync();
        using var client = Client(application, user);
        var response = await client.GetAsync("/Management/Work?kind=report&itemId=1");
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(0, store.Reads);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public async Task PostRequiresAntiforgeryAndUsesOnlySessionIdentity()
    {
        var store = new Store();
        await using var application = Build(store);
        await application.StartAsync();
        using var client = Client(application, "manager");
        using var invalid = await client.PostAsync("/Management/Work", Form(null));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(0, store.Saves);
        var html = await client.GetStringAsync("/Management/Work?kind=report&itemId=1");
        var token = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        Assert.NotEmpty(token);
        using var valid = await client.PostAsync("/Management/Work", Form(token));
        Assert.Equal(HttpStatusCode.Redirect, valid.StatusCode);
        Assert.Equal(1, store.Saves);
        Assert.Equal("manager@example.invalid", store.Actor);
        Assert.Equal("resolved", store.Status);
    }

    [Theory]
    [InlineData("member", 403)]
    [InlineData("anonymous", 401)]
    [InlineData("manager", 400)]
    public async Task ValidTokenDoesNotBypassAuthorizationOrValidation(string user, int expected)
    {
        var store = new Store();
        await using var application = Build(store);
        await application.StartAsync();
        using var client = Client(application, "manager");
        var html = await client.GetStringAsync("/Management/Work?kind=report&itemId=1");
        var token = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        client.DefaultRequestHeaders.Remove("X-Test-User");
        client.DefaultRequestHeaders.Add("X-Test-User", user);
        using var response = await client.PostAsync("/Management/Work", Form(token, "not-a-status"));
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public async Task ConflictShowsLatestHistoryAndPreservesUnsavedNote()
    {
        var store = new Store { Conflict = true };
        await using var application = Build(store);
        await application.StartAsync();
        using var client = Client(application, "manager");
        var html = await client.GetStringAsync("/Management/Work?kind=report&itemId=1");
        var token = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        using var response = await client.PostAsync("/Management/Work", Form(token));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains("尚未儲存", body);
        Assert.Contains("已核對影片", body);
        Assert.Equal(2, store.Reads);
    }

    [Fact]
    public async Task MissingStorageIsUnavailableNotPending()
    {
        var store = new Store { Unavailable = true };
        await using var application = Build(store);
        await application.StartAsync();
        using var client = Client(application, "manager");
        using var response = await client.GetAsync("/Management/Work?kind=report&itemId=1");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public void SourceChangesInvalidateCompletedBadge()
    {
        var report = new ErrorReportItem("1", "song", "title", null, "7", "description", "0", null);
        var evidence = ManagementWork.Evidence(report, "ReportId");
        var entry = new ManagementWorkEvent(1, evidence, "resolved", "fixed", "manager", DateTime.UtcNow);
        Assert.NotNull(new ManagementWorkBadge("report", "1", evidence, entry).Current);
        Assert.Null(new ManagementWorkBadge("report", "1", ManagementWork.Evidence(report with { Description = "new" }, "ReportId"), entry).Current);
        Assert.NotEqual(evidence, ManagementWork.Evidence(report, "OtherKey"));
        var song = new TimingReviewQueueItem("song", "title", null, null, "high_accuracy_needs_review", null,
            Guid.NewGuid(), null, "needs_review", null, null, null);
        Assert.NotEqual(ManagementWork.Evidence(song), ManagementWork.Evidence(song with { JobId = Guid.NewGuid() }));
    }

    private static FormUrlEncodedContent Form(string? token, string status = "resolved")
    {
        var fields = new Dictionary<string, string>
        {
            ["kind"] = "report", ["itemId"] = "1", ["ExpectedRevision"] = "0",
            ["EvidenceHash"] = new string('A', 64), ["Status"] = status,
            ["Note"] = "已核對影片", ["ActorEmail"] = "forged@example.invalid"
        };
        if (token != null) fields["__RequestVerificationToken"] = token;
        return new(fields);
    }

    private static HttpClient Client(WebApplication application, string? user)
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { BaseAddress = new Uri(application.Urls.Single()) };
        if (user != null) client.DefaultRequestHeaders.Add("X-Test-User", user);
        return client;
    }

    private static WebApplication Build(Store store)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        { ApplicationName = typeof(ManagementWorkController).Assembly.FullName, EnvironmentName = "Testing" });
        builder.Services.AddLogging();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddSession();
        builder.Services.AddSingleton<IErrorReportManagerAccess, Access>();
        builder.Services.AddSingleton<IManagementWorkStore>(store);
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(ManagementWorkController).Assembly);
        var application = builder.Build();
        application.UseSession();
        application.Use(async (context, next) =>
        {
            var user = context.Request.Headers["X-Test-User"].ToString();
            context.Session.Clear();
            if (user is "manager" or "member") context.Session.SetString("Email", user + "@example.invalid");
            await next();
        });
        application.MapControllers();
        application.Urls.Add("http://127.0.0.1:0");
        return application;
    }

    private sealed class Access : IErrorReportManagerAccess
    {
        public Task<IActionResult?> AuthorizeAsync(ControllerBase controller, CancellationToken cancellationToken)
        {
            var email = controller.HttpContext.Session.GetString("Email");
            IActionResult? result = email == null ? new UnauthorizedResult()
                : email == "manager@example.invalid" ? null : new StatusCodeResult(403);
            return Task.FromResult(result);
        }
    }

    private sealed class Store : IManagementWorkStore
    {
        public int Reads { get; private set; }
        public int Saves { get; private set; }
        public string? Actor { get; private set; }
        public string? Status { get; private set; }
        public bool Conflict { get; init; }
        public bool Unavailable { get; init; }
        public Task<IReadOnlyDictionary<string, ManagementWorkEvent>> ReadLatestAsync(string kind, IReadOnlyList<string> itemIds, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyDictionary<string, ManagementWorkEvent>>(new Dictionary<string, ManagementWorkEvent>());
        public Task<ManagementWorkDetail?> ReadAsync(string kind, string itemId, CancellationToken cancellationToken)
        {
            Reads++;
            if (Unavailable) throw new ManagementWorkUnavailableException();
            return Task.FromResult<ManagementWorkDetail?>(new(new(kind, itemId, "標題", "問題", null, new string('A', 64)), []));
        }
        public Task SaveAsync(string kind, string itemId, long expectedRevision, string evidenceHash, string status, string note, string actorEmail, CancellationToken cancellationToken)
        {
            if (Conflict) throw new ManagementWorkConflictException();
            Saves++;
            Actor = actorEmail;
            Status = status;
            return Task.CompletedTask;
        }
    }
}
