using System.Net;
using System.Text.RegularExpressions;
using LearnMore.Controllers;
using LearnMore.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LearnMore.Tests;

public sealed class ErrorReportRubyTests
{
    [Theory]
    [InlineData(46, 3, "人", "にん", "ひと")]
    [InlineData(48, 13, "人", "にん", "ひと")]
    [InlineData(49, 15, "重な", "おもな", "かさな")]
    public void OnlyApprovedReadingChangesAndSurroundingBytesRemain(int reportId, int lyricId, string baseText, string before, string after)
    {
        var target = RubyRepairTarget.Find(reportId)!;
        Assert.Equal(lyricId, target.LyricId);
        var original = $"前<ruby>{baseText}<rt>{before}</rt></ruby>る 後<ruby>別<rt>べつ</rt></ruby>";
        Assert.Equal(original.Replace($"<rt>{before}</rt>", $"<rt>{after}</rt>"), target.Propose(original));
        Assert.Throws<RubyRepairConflictException>(() => target.Propose(original + original));
        Assert.Throws<RubyRepairConflictException>(() => target.Propose(target.Propose(original)));
    }

    [Theory]
    [InlineData(47)]
    [InlineData(50)]
    [InlineData(45)]
    [InlineData(0)]
    public void TimingSplitAndOtherReportsHaveNoRepair(int reportId) => Assert.Null(RubyRepairTarget.Find(reportId));

    [Theory]
    [InlineData(46)]
    [InlineData(48)]
    [InlineData(49)]
    public void ReadbackPreservesAllRawColumnsAndNeverProposesReplay(int reportId)
    {
        var target = RubyRepairTarget.Find(reportId)!;
        var snapshot = Snapshot() with { ReportId = reportId };
        snapshot.Lyric["LyricID"] = target.LyricId;
        snapshot.Lyric["JapaneseRuby"] = target.After;
        var preview = RubyRepairPreview.ForRead(snapshot);
        Assert.Same(snapshot, preview.Snapshot);
        Assert.Equal(snapshot.Fingerprint, preview.Fingerprint);
        Assert.Null(preview.ProposedRuby);
        Assert.Throws<RubyRepairConflictException>(() => target.Propose(target.After));
        snapshot.Lyric["JapaneseRuby"] = "他人の編集";
        Assert.Null(RubyRepairPreview.ForRead(snapshot).ProposedRuby);
        snapshot.Lyric["JapaneseRuby"] = target.Before;
        Assert.Equal(target.After, RubyRepairPreview.ForRead(snapshot).ProposedRuby);
    }

    [Fact]
    public void FingerprintBindsAllColumnsAndExactSourceWithoutRecursion()
    {
        var snapshot = Snapshot();
        var hash = snapshot.Fingerprint;
        Assert.Equal(64, hash.Length);
        foreach (var field in snapshot.Lyric.Keys.ToArray())
        {
            var changed = new SortedDictionary<string, object?>(snapshot.Lyric) { [field] = "changed" };
            Assert.NotEqual(hash, (snapshot with { Lyric = changed }).Fingerprint);
        }
        Assert.NotEqual(hash, (snapshot with { ReportId = 48 }).Fingerprint);
        Assert.NotEqual(hash, (snapshot with { Song = new() { ["YouTubeVideoUrl"] = "other-version" } }).Fingerprint);
        Assert.NotEqual(hash, (snapshot with { Report = new() { ["Status"] = 1 } }).Fingerprint);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task DeniedGetAndPostDoNotReachStore(int status)
    {
        var store = new Repair();
        var controller = new ErrorReportRubyController(new Access(new StatusCodeResult(status)), store, NullLogger<ErrorReportRubyController>.Instance);
        Assert.Equal(status, Assert.IsType<StatusCodeResult>(await controller.Index(46, default)).StatusCode);
        Assert.Equal(status, Assert.IsType<StatusCodeResult>(await controller.Apply(46, new string('a', 64), default)).StatusCode);
        Assert.Equal(0, store.Reads + store.Writes);
    }

    [Fact]
    public async Task RealHttpRazorEncodesOriginalAndPostRequiresCsrfAndManager()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        { ApplicationName = typeof(ErrorReportRubyController).Assembly.FullName, EnvironmentName = "Testing" });
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(ErrorReportRubyController).Assembly);
        builder.Services.AddHttpContextAccessor();
        var access = new Access(null);
        var store = new Repair();
        builder.Services.AddSingleton<IErrorReportManagerAccess>(access);
        builder.Services.AddSingleton<IErrorReportRubyRepair>(store);
        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddSession();
        await using var application = builder.Build();
        application.UseSession();
        application.MapControllers();
        application.Urls.Add("http://127.0.0.1:0");
        await application.StartAsync();
        using var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = new Uri(application.Urls.Single()) };
        using var get = await client.GetAsync("/Management/ErrorReports/ruby/46");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.True(get.Headers.CacheControl?.NoStore);
        var html = await get.Content.ReadAsStringAsync();
        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script>attack", html);
        var token = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        Assert.NotEmpty(token);
        using var missing = await client.PostAsync("/Management/ErrorReports/ruby/46", new FormUrlEncodedContent(new Dictionary<string, string> { ["fingerprint"] = Snapshot().Fingerprint }));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(0, store.Writes);
        async Task<HttpResponseMessage> Post(int reportId, string fingerprint) => await client.PostAsync($"/Management/ErrorReports/ruby/{reportId}", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["fingerprint"] = fingerprint, ["__RequestVerificationToken"] = token, ["Japanese"] = "must-ignore", ["Status"] = "1" }));
        using var invalid = await Post(46, "invalid");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var excluded = await Post(50, Snapshot().Fingerprint);
        Assert.Equal(HttpStatusCode.NotFound, excluded.StatusCode);
        Assert.Equal(0, store.Writes);
        access.Result = new StatusCodeResult(403);
        using var denied = await Post(46, Snapshot().Fingerprint);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(0, store.Writes);
        access.Result = null;
        using var allowed = await Post(46, Snapshot().Fingerprint);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(1, store.Writes);
        store.Conflict = true;
        using var conflict = await Post(46, Snapshot().Fingerprint);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        store.ReadbackOnly = true;
        foreach (var reportId in new[] { 46, 48, 49 })
        {
            using var readback = await client.GetAsync($"/Management/ErrorReports/ruby/{reportId}");
            Assert.Equal(HttpStatusCode.OK, readback.StatusCode);
            Assert.True(readback.Headers.CacheControl?.NoStore);
            var readbackHtml = await readback.Content.ReadAsStringAsync();
            foreach (var field in new[] { "Japanese", "JapaneseRuby", "Roman", "Chinese", "TimeStamp" })
                Assert.Contains($"<dt>{field}</dt>", readbackHtml);
            Assert.Contains("&lt;script&gt;", readbackHtml);
            Assert.DoesNotMatch("<form[^>]*action=\"[^\"]*ErrorReports/ruby/", readbackHtml);
            Assert.DoesNotContain("name=\"fingerprint\"", readbackHtml);
        }
        foreach (var reportId in new[] { 47, 50 })
        {
            using var excludedGet = await client.GetAsync($"/Management/ErrorReports/ruby/{reportId}");
            Assert.Equal(HttpStatusCode.NotFound, excludedGet.StatusCode);
        }
        foreach (var status in new[] { 401, 403 })
        {
            access.Result = new StatusCodeResult(status);
            using var deniedGet = await client.GetAsync("/Management/ErrorReports/ruby/46");
            Assert.Equal(status, (int)deniedGet.StatusCode);
        }
        await application.StopAsync();
    }

    private static RubyRepairSnapshot Snapshot() => new(46, new() { ["Status"] = 0 }, new() { ["YouTubeVideoUrl"] = RubyRepairTarget.VideoSource }, new()
    {
        ["Japanese"] = "<script>attack</script>", ["JapaneseRuby"] = RubyRepairTarget.Find(46)!.Before,
        ["Roman"] = "hito", ["Chinese"] = "原值", ["TimeStamp"] = 1.23, ["LyricID"] = 3
    });

    private sealed class Access(IActionResult? result) : IErrorReportManagerAccess
    {
        public IActionResult? Result { get; set; } = result;
        public Task<IActionResult?> AuthorizeAsync(ControllerBase controller, CancellationToken cancellationToken) => Task.FromResult(Result);
    }

    private sealed class Repair : IErrorReportRubyRepair
    {
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public bool Conflict { get; set; }
        public bool ReadbackOnly { get; set; }
        public Task<RubyRepairPreview> ReadAsync(int reportId, CancellationToken cancellationToken)
        {
            Reads++;
            var snapshot = Snapshot();
            if (ReadbackOnly)
                return Task.FromResult(new RubyRepairPreview(snapshot, snapshot.Fingerprint, null));
            return Task.FromResult(new RubyRepairPreview(snapshot, snapshot.Fingerprint, RubyRepairTarget.Find(reportId)!.After));
        }
        public Task<RubyRepairReceipt> ApplyAsync(int reportId, string fingerprint, string actor, CancellationToken cancellationToken)
        {
            if (Conflict) throw new RubyRepairConflictException();
            Writes++;
            return Task.FromResult(new RubyRepairReceipt("test-audit-id", true));
        }
    }
}
