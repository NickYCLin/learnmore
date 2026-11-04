using System.Net;
using LearnMore.Controllers;
using LearnMore.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnMore.Tests;

public sealed class ErrorReportRawTests
{
    [Theory]
    [InlineData(47, 7, 8)]
    [InlineData(50, 21, 22)]
    public void ExactBindingPreservesRawValuesAndRejectsMismatches(int reportId, int first, int second)
    {
        var target = ErrorReportRawTarget.Find(reportId)!;
        var report = new SortedDictionary<string, object?> { ["ReportID"] = reportId, ["ErrorSongUid"] = RubyRepairTarget.SongUid, ["ErrorLyricID"] = target.ReportLyricId };
        var song = new SortedDictionary<string, object?> { ["SongUid"] = RubyRepairTarget.SongUid, ["YouTubeVideoUrl"] = RubyRepairTarget.VideoSource };
        var rows = new[] { Row(first), Row(second) };
        var result = ErrorReportRawReader.Validate(reportId, report, song, rows);
        Assert.Same(rows, result.Lyrics);
        Assert.NotEqual(result.Lyrics[0]["Japanese"], result.Lyrics[0]["JapaneseRuby"]);
        Assert.Null(result.Lyrics[0]["Chinese"]);
        foreach (var field in report.Keys.ToArray())
        {
            var changed = new SortedDictionary<string, object?>(report) { [field] = "wrong" };
            Assert.Throws<RubyRepairConflictException>(() => ErrorReportRawReader.Validate(reportId, changed, song, rows));
        }
        foreach (var field in song.Keys.ToArray())
        {
            var changed = new SortedDictionary<string, object?>(song) { [field] = "other-version" };
            Assert.Throws<RubyRepairConflictException>(() => ErrorReportRawReader.Validate(reportId, report, changed, rows));
        }
        Assert.Throws<RubyRepairConflictException>(() => ErrorReportRawReader.Validate(reportId, report, song, new[] { rows[1], rows[0] }));
        Assert.Throws<RubyRepairConflictException>(() => ErrorReportRawReader.Validate(reportId, report, song, new[] { rows[0] }));
        Assert.Throws<RubyRepairConflictException>(() => ErrorReportRawReader.Validate(reportId, report, song, new[] { rows[0], rows[0] }));
        foreach (var field in new[] { "Japanese", "JapaneseRuby", "Roman", "Chinese", "TimeStamp" })
        {
            var incomplete = Row(first);
            incomplete.Remove(field);
            Assert.Throws<RubyRepairConflictException>(() => ErrorReportRawReader.Validate(reportId, report, song, new[] { incomplete, rows[1] }));
        }
    }

    [Fact]
    public async Task RealRazorGetIsManagerOnlyEncodedNoStoreAndHasNoWriteEndpoint()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        { ApplicationName = typeof(ErrorReportRawController).Assembly.FullName, EnvironmentName = "Testing" });
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(ErrorReportRawController).Assembly);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddSession();
        var access = new Access();
        var reader = new Reader();
        builder.Services.AddSingleton<IErrorReportManagerAccess>(access);
        builder.Services.AddSingleton<IErrorReportRawReader>(reader);
        await using var application = builder.Build();
        application.UseSession();
        application.MapControllers();
        application.Urls.Add("http://127.0.0.1:0");
        await application.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(application.Urls.Single()) };
        foreach (var status in new[] { 401, 403 })
        {
            access.Status = status;
            using var denied = await client.GetAsync("/Management/ErrorReports/raw/47");
            Assert.Equal(status, (int)denied.StatusCode);
            Assert.Equal(0, reader.Reads);
        }
        access.Status = null;
        foreach (var reportId in new[] { 47, 50 })
        {
            using var response = await client.GetAsync($"/Management/ErrorReports/raw/{reportId}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var html = await response.Content.ReadAsStringAsync();
            foreach (var field in new[] { "Japanese", "JapaneseRuby", "Roman", "Chinese", "TimeStamp" })
                Assert.Equal(2, html.Split($"<dt>{field}</dt>").Length - 1);
            Assert.Contains("&lt;script&gt;raw&lt;/script&gt;", html);
            Assert.Contains("ruby-original", html);
            Assert.Contains("NULL", html);
            Assert.DoesNotContain("<script>raw</script>", html);
            Assert.DoesNotContain("fingerprint", html);
            Assert.DoesNotMatch("<form[^>]*ErrorReports/raw", html);
            using var post = await client.PostAsync($"/Management/ErrorReports/raw/{reportId}", new StringContent(""));
            Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        }
        var reads = reader.Reads;
        foreach (var excluded in new[] { 0, 46, 48, 49, 51 })
        {
            Assert.Null(ErrorReportRawTarget.Find(excluded));
            using var response = await client.GetAsync($"/Management/ErrorReports/raw/{excluded}");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        Assert.Equal(reads, reader.Reads);
        reader.Conflict = true;
        using var conflict = await client.GetAsync("/Management/ErrorReports/raw/47");
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        await application.StopAsync();
    }

    private static SortedDictionary<string, object?> Row(int lyricId) => new()
    {
        ["LyricID"] = lyricId, ["Japanese"] = "<script>raw</script>", ["JapaneseRuby"] = "ruby-original",
        ["Roman"] = "roman-original", ["Chinese"] = null, ["TimeStamp"] = 73.14
    };

    private sealed class Access : IErrorReportManagerAccess
    {
        public int? Status { get; set; }
        public Task<IActionResult?> AuthorizeAsync(ControllerBase controller, CancellationToken cancellationToken)
            => Task.FromResult<IActionResult?>(Status.HasValue ? new StatusCodeResult(Status.Value) : null);
    }

    private sealed class Reader : IErrorReportRawReader
    {
        public int Reads { get; private set; }
        public bool Conflict { get; set; }
        public Task<ErrorReportRawSnapshot> ReadAsync(int reportId, CancellationToken cancellationToken)
        {
            Reads++;
            if (Conflict) throw new RubyRepairConflictException();
            var target = ErrorReportRawTarget.Find(reportId)!;
            return Task.FromResult(new ErrorReportRawSnapshot(reportId, RubyRepairTarget.SongUid,
                RubyRepairTarget.VideoSource, new[] { Row(target.FirstLyricId), Row(target.SecondLyricId) }));
        }
    }
}
