using System.Net;
using System.Text.Json;
using LearnMore.Controllers;
using LearnMore.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LearnMore.Tests;

public sealed class SiteTrafficTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "learnmore-traffic-" + Guid.NewGuid().ToString("N"));
    private readonly TrafficClock clock = new();

    [Fact]
    public async Task SnapshotSurvivesRestartAndExpiresOldDates()
    {
        var store = Store();
        store.Load();
        store.Record("歌曲與歌詞", "Google");
        Assert.Equal(new DateOnly(2026, 11, 23), Assert.Single(store.Read().Snapshot!.Counts).Day);
        await store.SaveAsync(default);

        var restored = Store();
        restored.Load();
        Assert.Equal(1, Assert.Single(restored.Read().Snapshot!.Counts).Views);
        Assert.Equal(clock.Now, restored.Read().Snapshot!.LastViewAt);
        clock.Now = clock.Now.AddDays(30);
        restored.Record("群組播放", "無來源資訊");
        await restored.SaveAsync(default);
        Assert.Equal("群組播放", Assert.Single(restored.Read().Snapshot!.Counts).Page);
    }

    [Fact]
    public async Task MiddlewareCountsPublicHtmlWithoutSavingReferrerOrSearchText()
    {
        var store = Store();
        store.Load();
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(store);
        await using var app = builder.Build();
        app.UseRouting();
        app.UseMiddleware<SiteTrafficMiddleware>();
        app.MapGet("/song", () => Results.Content("歌詞", "text/html"))
            .WithMetadata(new ControllerActionDescriptor { ControllerName = "Lyrics", ActionName = "Index" });
        app.MapGet("/missing", () => Results.NotFound()).WithMetadata(new ControllerActionDescriptor
            { ControllerName = "Lyrics", ActionName = "Index" });
        app.MapGet("/api", () => Results.Json(new { ok = true })).WithMetadata(new ControllerActionDescriptor
            { ControllerName = "Lyrics", ActionName = "Index" });
        app.MapGet("/manager", () => Results.Content("後台", "text/html"));
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TestBrowser/1.0");
        client.DefaultRequestHeaders.Referrer = new Uri("https://www.google.com.tw/search?q=private-referrer");
        await client.GetStringAsync("/song?q=private-query");
        await client.GetStringAsync("/api");
        await client.GetStringAsync("/manager");
        using var missing = await client.GetAsync("/missing");
        client.DefaultRequestHeaders.Add("Sec-Purpose", "prefetch");
        await client.GetStringAsync("/song");
        client.DefaultRequestHeaders.Remove("Sec-Purpose");
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Googlebot/2.1");
        await client.GetStringAsync("/song");
        await app.StopAsync();

        var count = Assert.Single(store.Read().Snapshot!.Counts);
        Assert.Equal("Google", count.Source);
        Assert.Equal(1, count.Views);
        await store.SaveAsync(default);
        Assert.DoesNotContain("private-", File.ReadAllText(Path.Combine(root, "App_Data", "site-traffic.json")));
    }

    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("member", HttpStatusCode.Forbidden)]
    [InlineData("manager", HttpStatusCode.OK)]
    public async Task TrafficPageRequiresManagerAndRendersUnconfiguredState(string role, HttpStatusCode status)
    {
        var store = Store();
        store.Load();
        store.Record("歌曲與歌詞", "無來源資訊");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            { ApplicationName = typeof(SiteTrafficController).Assembly.FullName, EnvironmentName = "Testing" });
        builder.Services.AddControllersWithViews();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddSession();
        builder.Services.AddMemoryCache();
        builder.Services.AddHttpClient<SearchConsoleTraffic>();
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton<IErrorReportManagerAccess>(new Access(role));
        await using var app = builder.Build();
        app.UsePathBase("/LearnMore");
        app.UseRouting();
        app.UseSession();
        app.MapControllers();
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var response = await client.GetAsync("/LearnMore/Management/Traffic?days=30");
        Assert.Equal(status, response.StatusCode);
        if (status != HttpStatusCode.OK) return;

        Assert.True(response.Headers.CacheControl!.NoStore);
        var body = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains("尚未連接 Google Search Console", body);
        Assert.Contains("/LearnMore/Management/Traffic?days=7", body);
        Assert.Contains("歌曲與歌詞", body);
        Assert.Contains("日均瀏覽", body);
        Assert.Contains("traffic-column", body);
    }

    [Fact]
    public async Task SearchConsoleUsesReadOnlySearchReportWithPageBoundary()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var client = new HttpClient(new SearchHandler());
        var search = new SearchConsoleTraffic(client, new ConfigurationBuilder().Build(), cache, clock,
            NullLogger<SearchConsoleTraffic>.Instance);
        var report = await search.QueryAsync("sc-domain:example.com", "https://example.com/LearnMore/",
            new DateOnly(2026, 11, 13), new DateOnly(2026, 11, 19), "fake-token", default);

        Assert.Equal("ready", report.State);
        Assert.Equal(new SearchTrafficMetrics(11, 40, 0.275, 6.5), report.Totals);
        Assert.Equal(new SearchTrafficRow("日文歌詞", new(3, 12, 0.25, 7.4)), Assert.Single(report.Keywords!));
        Assert.Equal(new[] { "2026-11-15", "2026-11-19" }, report.Daily!.Select(row => row.Label));
        Assert.Equal("https://example.com/LearnMore/song", Assert.Single(report.Pages!).Label);
        Assert.Equal("MOBILE", Assert.Single(report.Devices!).Label);
        Assert.Equal(new[] { "台灣", "日本", "美國", "未知地區" }, report.Countries!.Select(row => row.Label));
    }

    [Fact]
    public async Task SearchConsoleKeepsMissingRowsDistinctFromZero()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var client = new HttpClient(new SearchHandler(empty: true));
        var search = new SearchConsoleTraffic(client, new ConfigurationBuilder().Build(), cache, clock,
            NullLogger<SearchConsoleTraffic>.Instance);
        var report = await search.QueryAsync("sc-domain:example.com", "https://example.com/LearnMore/",
            new DateOnly(2026, 11, 13), new DateOnly(2026, 11, 19), "fake-token", default);

        Assert.Equal("ready", report.State);
        Assert.Null(report.Totals);
        Assert.Empty(report.Keywords!);
        Assert.Empty(report.Daily!);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private SiteTrafficStore Store() => new(new ConfigurationBuilder().Build(), new TrafficEnvironment { ContentRootPath = root }, clock);

    private sealed class TrafficEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "LearnMore";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = "";
        public string WebRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class Access(string role) : IErrorReportManagerAccess
    {
        public Task<IActionResult?> AuthorizeAsync(ControllerBase controller, CancellationToken cancellationToken)
            => Task.FromResult<IActionResult?>(role == "manager" ? null : new StatusCodeResult(role == "member" ? 403 : 401));
    }

    private sealed class TrafficClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 22, 17, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class SearchHandler(bool empty = false) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Contains("sc-domain%3Aexample.com/searchAnalytics/query", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var dimensions = json.RootElement.GetProperty("dimensions");
            var dimension = dimensions.GetArrayLength() == 0 ? "" : dimensions[0].GetString();
            Assert.Equal("final", json.RootElement.GetProperty("dataState").GetString());
            Assert.Equal("^https://example\\.com/LearnMore(/|[?]|$)",
                json.RootElement.GetProperty("dimensionFilterGroups")[0].GetProperty("filters")[0].GetProperty("expression").GetString());
            var (limit, rows) = dimension switch
            {
                "" => (1, """[{"clicks":11,"impressions":40,"ctr":0.275,"position":6.5}]"""),
                "query" => (20, """[{"keys":["日文歌詞"],"clicks":3,"impressions":12,"ctr":0.25,"position":7.4}]"""),
                "date" => (7, """[{"keys":["2026-11-19"]},{"keys":["2026-11-15"]}]"""),
                "page" => (10, """[{"keys":["https://example.com/LearnMore/song"]}]"""),
                "device" => (10, """[{"keys":["MOBILE"]}]"""),
                "country" => (10, """[{"keys":["twn"]},{"keys":["JPN"]},{"keys":["usa"]},{"keys":["zzz"]}]"""),
                _ => throw new InvalidOperationException($"Unexpected dimension: {dimension}")
            };
            Assert.Equal(limit, json.RootElement.GetProperty("rowLimit").GetInt32());
            return new(HttpStatusCode.OK) { Content = new StringContent(empty ? "{}" : "{\"rows\":" + rows + "}") };
        }
    }
}
