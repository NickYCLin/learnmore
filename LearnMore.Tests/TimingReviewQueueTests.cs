using LearnMore.Controllers;
using LearnMore.Controllers.API;
using LearnMore.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnMore.Tests;

public sealed class TimingReviewQueueTests
{
    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task HtmlAndJsonDenyWithoutReading(int status)
    {
        var reader = new Reader();
        await using var application = Build(new Access(new StatusCodeResult(status)), reader);
        await application.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(application.Urls.Single()) };
        foreach (var path in new[] { "/Management/ReviewQueue", "/Management/ReviewQueue/data" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(status, (int)response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
        Assert.Equal(0, reader.Calls);
    }

    [Fact]
    public async Task ActualManagerPolicyRejectsAnonymousBeforeOpeningDatabase()
    {
        var reader = new Reader();
        var controller = new TimingReviewQueueController(new TimingReviewQueueManagerAccess(new ConfigurationBuilder().Build()), reader);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { Session = new MemorySession() } };
        Assert.IsType<UnauthorizedObjectResult>(await controller.Data());
        Assert.Equal(0, reader.Calls);
    }

    [Fact]
    public async Task ManagerPaginationAndReadOnlyRoutesHaveRealHttpCoverage()
    {
        var reader = new Reader();
        await using var application = Build(new Access(null), reader);
        await application.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(application.Urls.Single()) };
        using var response = await client.GetAsync("/Management/ReviewQueue/data?page=2&pageSize=1");
        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal((2, 1), reader.Request);
        Assert.Contains("all_songs", await response.Content.ReadAsStringAsync());
        foreach (var query in new[] { "page=bad", "page=0", "pageSize=0", "pageSize=101" })
        {
            using var invalid = await client.GetAsync("/Management/ReviewQueue/data?" + query);
            Assert.Equal(400, (int)invalid.StatusCode);
        }
        foreach (var path in new[] { "/Management/ReviewQueue", "/Management/ReviewQueue/data", "/api/music-automation/review-queue" })
        {
            using var mutation = await client.PostAsync(path, new StringContent("{}"));
            Assert.Equal(405, (int)mutation.StatusCode);
        }
        Assert.Equal(1, reader.Calls);
    }

    [Fact]
    public async Task AutomationUsesExistingReadTokenPolicyNotManagerSession()
    {
        var reader = new Reader();
        await using var application = Build(new Access(new StatusCodeResult(403)), reader);
        await application.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(application.Urls.Single()) };
        using var denied = await client.GetAsync("/api/music-automation/review-queue");
        Assert.Equal(401, (int)denied.StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", new string('r', 40));
        using var allowed = await client.GetAsync("/api/music-automation/review-queue?page=2&pageSize=1");
        Assert.Equal(200, (int)allowed.StatusCode);
        Assert.Equal((2, 1), reader.Request);
        Assert.Contains("requestKeyHash", await allowed.Content.ReadAsStringAsync());
        using var invalid = await client.GetAsync("/api/music-automation/review-queue?pageSize=101");
        Assert.Equal(400, (int)invalid.StatusCode);
        Assert.Equal(1, reader.Calls);
    }

    [Fact]
    public async Task MissingSchemaIs503NotZeroSongs()
    {
        var reader = new Reader { SchemaMissing = true };
        var html = new TimingReviewQueueController(new Access(null), reader);
        Assert.Equal(503, Assert.IsType<ObjectResult>(await html.Data()).StatusCode);
        var api = new TimingReviewQueueAutomationController(reader);
        Assert.Equal(503, Assert.IsType<ObjectResult>(await api.Get()).StatusCode);
    }

    [Theory]
    [InlineData("high_accuracy_partial", true)]
    [InlineData("high_accuracy_needs_review", true)]
    [InlineData("high_accuracy_failed", true)]
    [InlineData("high_accuracy_completed", false)]
    [InlineData(null, false)]
    public void StatusScopeIsExact(string? status, bool expected)
        => Assert.Equal(expected, TimingReviewQueueReader.NeedsReview(status));

    [Fact]
    public async Task ActualRazorEncodesFieldsAndHasNoMutationControls()
    {
        await using var application = Build(new Access(null), new Reader());
        using var scope = application.Services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        var action = new ActionContext(context, new RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var view = scope.ServiceProvider.GetRequiredService<IRazorViewEngine>().GetView(null, "/Views/TimingReviewQueue/Index.cshtml", false);
        Assert.True(view.Success);
        var model = new TimingReviewQueuePage(2, 1, 3, true,
            [new("69e99493-ac81-4f17-941e-834df328555e", "<script>title</script>", "javascript:bad", null,
                "high_accuracy_failed", "<script>alert(1)</script>", null, null, null, null, null, null)]);
        var data = new ViewDataDictionary<TimingReviewQueuePage>(new EmptyModelMetadataProvider(), new ModelStateDictionary()) { Model = model };
        var temp = new TempDataDictionary(context, scope.ServiceProvider.GetRequiredService<ITempDataProvider>());
        using var writer = new StringWriter();
        await view.View.RenderAsync(new ViewContext(action, view.View, data, temp, writer, new HtmlHelperOptions()));
        var html = writer.ToString();
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<form", html);
        Assert.DoesNotContain("<button", html);
        Assert.DoesNotContain("href=\"javascript:", html);
        Assert.Contains("page=1", html);
        Assert.Contains("page=3", html);
    }

    private static WebApplication Build(ITimingReviewQueueManagerAccess access, Reader reader)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        { ApplicationName = typeof(TimingReviewQueueController).Assembly.FullName, EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        { ["MusicAutomation:Enabled"] = "true", ["MusicAutomation:ReadToken"] = new string('r', 40) });
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(TimingReviewQueueController).Assembly);
        builder.Services.AddSingleton(access);
        builder.Services.AddSingleton<ITimingReviewQueueReader>(reader);
        builder.Services.AddScoped<MusicAutomationAuthorizeFilter>();
        var application = builder.Build();
        application.MapControllers();
        application.Urls.Add("http://127.0.0.1:0");
        return application;
    }

    private sealed class Access(IActionResult? result) : ITimingReviewQueueManagerAccess
    {
        public Task<IActionResult?> AuthorizeAsync(ControllerBase controller, CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class MemorySession : ISession
    {
        public bool IsAvailable => true;
        public string Id => "test";
        public IEnumerable<string> Keys => [];
        public void Clear() { }
        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Remove(string key) { }
        public void Set(string key, byte[] value) { }
        public bool TryGetValue(string key, out byte[] value) { value = []; return false; }
    }

    private sealed class Reader : ITimingReviewQueueReader
    {
        public int Calls { get; private set; }
        public (int, int) Request { get; private set; }
        public bool SchemaMissing { get; init; }
        public Task<TimingReviewQueuePage> ReadAsync(int page, int pageSize, CancellationToken cancellationToken)
        {
            Calls++;
            Request = (page, pageSize);
            if (SchemaMissing) throw new TimingReviewQueueSchemaException();
            return Task.FromResult(new TimingReviewQueuePage(page, pageSize, 3, true,
                [new("song-version-b", "title", "https://youtu.be/j_NHrPC3ij8", "j_NHrPC3ij8", "high_accuracy_needs_review",
                    "reason", null, "hash", "needs_review", null, null, null)]));
        }
    }
}
