using System.Net;
using System.Text.Json;
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

public sealed class Report50RepairTests
{
    internal static Report50RepairSnapshot Before()
    {
        using var stream = typeof(Report50RepairPlan).Assembly.GetManifestResourceStream("LearnMore.Report50RepairTarget.json")!;
        var target = JsonSerializer.Deserialize<Report50RepairTarget>(stream)!;
        return new(50, new() { ["ReportID"] = 50, ["ErrorSongUid"] = RubyRepairTarget.SongUid, ["ErrorLyricID"] = 22, ["Status"] = 0 },
            new() { ["SongUid"] = RubyRepairTarget.SongUid, ["YouTubeVideoUrl"] = RubyRepairTarget.VideoSource },
            target.before.Select(row => new SortedDictionary<string, object?>
            {
                ["LyricID"] = row.LyricID, ["Japanese"] = row.Japanese, ["JapaneseRuby"] = row.JapaneseRuby,
                ["Roman"] = row.Roman, ["Chinese"] = row.Chinese,
                ["TimeStamp"] = double.Parse(row.TimeStamp, System.Globalization.CultureInfo.InvariantCulture), ["OtherColumn"] = "preserved"
            }).ToArray());
    }

    [Fact]
    public void ExactTwoRowCandidatePreservesChineseTimingAndAllOtherColumnsAndCannotReplay()
    {
        var before = Before();
        var preview = Report50RepairPlan.Preview(before);
        Assert.True(preview.CanApply);
        Assert.False(preview.AlreadyApplied);
        var after = Report50RepairPlan.Prepare(before, before.Fingerprint);
        Assert.Equal(before.Lyrics[0]["Japanese"] + "る", after.Lyrics[0]["Japanese"]);
        Assert.Equal(((string)before.Lyrics[1]["Japanese"]!)[1..], after.Lyrics[1]["Japanese"]);
        Assert.Equal(((string)before.Lyrics[0]["JapaneseRuby"]!).Replace("<ruby>呟<rt>げん</rt></ruby>いて", "<ruby>呟<rt>つぶや</rt></ruby>いてる"), after.Lyrics[0]["JapaneseRuby"]);
        Assert.Equal(((string)before.Lyrics[1]["JapaneseRuby"]!)[1..], after.Lyrics[1]["JapaneseRuby"]);
        Assert.Equal(((string)before.Lyrics[0]["Roman"]!)[..^"tsubuyai te".Length] + "tsubuyai teru", after.Lyrics[0]["Roman"]);
        Assert.Equal(((string)before.Lyrics[1]["Roman"]!)[3..], after.Lyrics[1]["Roman"]);
        foreach (var index in new[] { 0, 1 })
            foreach (var field in new[] { "Chinese", "TimeStamp", "LyricID", "OtherColumn" })
                Assert.Equal(before.Lyrics[index][field], after.Lyrics[index][field]);
        Assert.Equal(211.95, after.Lyrics[0]["TimeStamp"]);
        Assert.Equal(225.61, after.Lyrics[1]["TimeStamp"]);
        Assert.Equal(before.Report, after.Report);
        Assert.Equal(before.Song, after.Song);
        var readback = Report50RepairPlan.Preview(after);
        Assert.False(readback.CanApply);
        Assert.True(readback.AlreadyApplied);
        Assert.Throws<RubyRepairConflictException>(() => Report50RepairPlan.Prepare(after, before.Fingerprint));
        Assert.Throws<RubyRepairConflictException>(() => Report50RepairPlan.Prepare(after, after.Fingerprint));
        Assert.True(Report50RepairPlan.Preview(before).CanApply);
    }

    [Theory]
    [InlineData(0, "Japanese")]
    [InlineData(0, "JapaneseRuby")]
    [InlineData(0, "Roman")]
    [InlineData(0, "Chinese")]
    [InlineData(0, "TimeStamp")]
    [InlineData(1, "Japanese")]
    [InlineData(1, "JapaneseRuby")]
    [InlineData(1, "Roman")]
    [InlineData(1, "Chinese")]
    [InlineData(1, "TimeStamp")]
    public void AnyRawFieldMismatchDisablesWholeCandidate(int index, string field)
    {
        var snapshot = Before();
        snapshot.Lyrics[index][field] = "changed";
        Assert.False(Report50RepairPlan.Preview(snapshot).CanApply);
        Assert.Throws<RubyRepairConflictException>(() => Report50RepairPlan.Prepare(snapshot, snapshot.Fingerprint));
    }

    [Fact]
    public void ScopeIdentityFullFingerprintAndMixedRowsReject()
    {
        var snapshot = Before();
        foreach (var reportId in new[] { 47, 46, 48, 49, 51 })
            Assert.Throws<RubyRepairConflictException>(() => Report50RepairPlan.Preview(snapshot with { ReportId = reportId }));
        foreach (var field in snapshot.Report.Keys.ToArray())
        {
            var changed = snapshot with { Report = new(snapshot.Report) { [field] = "changed" } };
            Assert.NotEqual(snapshot.Fingerprint, changed.Fingerprint);
            Assert.Throws<RubyRepairConflictException>(() => Report50RepairPlan.Prepare(changed, snapshot.Fingerprint));
        }
        foreach (var field in snapshot.Song.Keys.ToArray())
        {
            var changed = snapshot with { Song = new(snapshot.Song) { [field] = "other" } };
            Assert.Throws<RubyRepairConflictException>(() => Report50RepairPlan.Preview(changed));
        }
        var after = Report50RepairPlan.Prepare(snapshot, snapshot.Fingerprint);
        var mixed = snapshot with { Lyrics = new[] { after.Lyrics[0], snapshot.Lyrics[1] } };
        Assert.False(Report50RepairPlan.Preview(mixed).CanApply);
        Assert.False(Report50RepairPlan.Preview(mixed).AlreadyApplied);
        Assert.Throws<RubyRepairConflictException>(() => Report50RepairPlan.Prepare(mixed, mixed.Fingerprint));
        Assert.Throws<RubyRepairConflictException>(() => Report50RepairPlan.Preview(snapshot with { Lyrics = snapshot.Lyrics.Reverse().ToArray() }));
        var fingerprint = snapshot.Fingerprint;
        snapshot.Lyrics[1]["OtherColumn"] = "concurrent change";
        Assert.Throws<RubyRepairConflictException>(() => Report50RepairPlan.Prepare(snapshot, fingerprint));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task AccessDeniedNeverReachesRepair(int status)
    {
        var store = new Store();
        var access = new Access { Status = status };
        var controller = new Report50RepairController(access, store, NullLogger<Report50RepairController>.Instance);
        Assert.Equal(status, Assert.IsType<StatusCodeResult>(await controller.Index(50, default)).StatusCode);
        Assert.Equal(status, Assert.IsType<StatusCodeResult>(await controller.Apply(50, new string('a', 64), default)).StatusCode);
        Assert.Equal(0, store.Reads + store.Writes);
    }

    [Fact]
    public async Task RealRazorPreviewCsrfScopeConflictAndReadbackOnlyAfterOneApply()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        { ApplicationName = typeof(Report50RepairController).Assembly.FullName, EnvironmentName = "Testing" });
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(Report50RepairController).Assembly);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddSession();
        var access = new Access();
        var store = new Store();
        builder.Services.AddSingleton<IErrorReportManagerAccess>(access);
        builder.Services.AddSingleton<IReport50Repair>(store);
        await using var application = builder.Build();
        application.UseSession();
        application.MapControllers();
        application.Urls.Add("http://127.0.0.1:0");
        await application.StartAsync();
        using var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = new Uri(application.Urls.Single()) };
        using var preview = await client.GetAsync("/Management/ErrorReports/split/50");
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.True(preview.Headers.CacheControl?.NoStore);
        var html = await preview.Content.ReadAsStringAsync();
        Assert.Contains("name=\"fingerprint\"", html);
        foreach (var field in new[] { "Japanese", "JapaneseRuby", "Roman", "Chinese", "TimeStamp" })
            Assert.Equal(4, html.Split($"<dt>{field}</dt>").Length - 1);
        Assert.DoesNotContain("<ruby>", html);
        Assert.Contains("&lt;ruby&gt;", html);
        var token = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        Assert.NotEmpty(token);
        async Task<HttpResponseMessage> Post(int reportId, string fingerprint) => await client.PostAsync($"/Management/ErrorReports/split/{reportId}",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["fingerprint"] = fingerprint, ["__RequestVerificationToken"] = token,
                ["Chinese"] = "ignored", ["TimeStamp"] = "0", ["Status"] = "1", ["SongUid"] = "other" }));
        using var noCsrf = await client.PostAsync("/Management/ErrorReports/split/50", new FormUrlEncodedContent(new Dictionary<string, string> { ["fingerprint"] = store.Current.Fingerprint }));
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        foreach (var reportId in new[] { 47, 46, 48, 49, 51 })
        {
            using var excludedGet = await client.GetAsync($"/Management/ErrorReports/split/{reportId}");
            using var excludedPost = await Post(reportId, store.Current.Fingerprint);
            Assert.Equal(HttpStatusCode.NotFound, excludedGet.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, excludedPost.StatusCode);
        }
        using var invalid = await Post(50, "invalid");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        foreach (var status in new[] { 401, 403 })
        {
            access.Status = status;
            using var deniedGet = await client.GetAsync("/Management/ErrorReports/split/50");
            using var deniedPost = await Post(50, store.Current.Fingerprint);
            Assert.Equal(status, (int)deniedGet.StatusCode);
            Assert.Equal(status, (int)deniedPost.StatusCode);
        }
        access.Status = null;
        var originalFingerprint = store.Current.Fingerprint;
        using var stale = await Post(50, new string('0', 64));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(0, store.Writes);
        using var applied = await Post(50, originalFingerprint);
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.Equal(1, store.Writes);
        using var replay = await Post(50, originalFingerprint);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        using var readback = await client.GetAsync("/Management/ErrorReports/split/50");
        var readbackHtml = await readback.Content.ReadAsStringAsync();
        Assert.DoesNotContain("name=\"fingerprint\"", readbackHtml);
        Assert.DoesNotMatch("<form[^>]*ErrorReports/split", readbackHtml);
        using var newFingerprintReplay = await Post(50, store.Current.Fingerprint);
        Assert.Equal(HttpStatusCode.Conflict, newFingerprintReplay.StatusCode);
        store.Current.Lyrics[0]["Japanese"] = "<script>unexpected</script>";
        using var conflictReadback = await client.GetAsync("/Management/ErrorReports/split/50");
        var conflictHtml = await conflictReadback.Content.ReadAsStringAsync();
        Assert.DoesNotContain("name=\"fingerprint\"", conflictHtml);
        Assert.Contains("&lt;script&gt;unexpected&lt;/script&gt;", conflictHtml);
        Assert.Equal(1, store.Writes);
        await application.StopAsync();
    }

    private sealed class Access : IErrorReportManagerAccess
    {
        public int? Status { get; set; }
        public Task<IActionResult?> AuthorizeAsync(ControllerBase controller, CancellationToken cancellationToken)
            => Task.FromResult<IActionResult?>(Status.HasValue ? new StatusCodeResult(Status.Value) : null);
    }

    private sealed class Store : IReport50Repair
    {
        public Report50RepairSnapshot Current { get; } = Before();
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public Task<Report50RepairPreview> ReadAsync(int reportId, CancellationToken cancellationToken)
        { Reads++; return Task.FromResult(Report50RepairPlan.Preview(Current)); }
        public Task<RubyRepairReceipt> ApplyAsync(int reportId, string fingerprint, string actor, CancellationToken cancellationToken)
        {
            var after = Report50RepairPlan.Prepare(Current, fingerprint);
            for (var index = 0; index < after.Lyrics.Count; index++)
                foreach (var field in new[] { "Japanese", "JapaneseRuby", "Roman" }) Current.Lyrics[index][field] = after.Lyrics[index][field];
            Writes++;
            return Task.FromResult(new RubyRepairReceipt("test", true));
        }
    }
}
