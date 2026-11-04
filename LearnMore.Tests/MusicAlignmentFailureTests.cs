using System.Net;
using LearnMore.Models;
using LearnMore.Options;
using LearnMore.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LearnMore.Tests;

public sealed class MusicAlignmentFailureTests
{
    private static MusicSongSnapshot Snapshot() => new("song", "https://www.youtube.com/watch?v=j_NHrPC3ij8",
        "high_accuracy_needs_review", "keep", new() { new() { LyricID = 7, Japanese = "音楽", TimeStamp = 3 } });

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public async Task RemoteHttpFailurePreservesOnlyStatusNotResponseBody(int status)
    {
        var client = new RemoteHighAccuracyAlignmentClient(Microsoft.Extensions.Options.Options.Create(new VocalOnsetDetectionOptions
        {
            UseRemoteHighAccuracyApi = true, RemoteHighAccuracyApiBaseUrl = "https://example.invalid/",
            RemoteHighAccuracyApiToken = "test-only"
        }), new Factory(status), NullLogger<RemoteHighAccuracyAlignmentClient>.Instance);
        var snapshot = Snapshot();
        var remote = await client.AlignWithReportAsync(new SongLyricsProcessingSnapshot
        { SongUid = snapshot.SongUid, YouTubeUrl = snapshot.YoutubeUrl, Lyrics = snapshot.Lyrics });
        var report = MusicAlignmentValidation.Build(snapshot, remote);
        Assert.Equal($"remote_high_accuracy_http_{status}", report.FailureCode);
        Assert.Equal(report.FailureCode, MusicAlignmentValidation.CompletionFailureCode(report));
        Assert.Equal("failed", MusicAlignmentValidation.CompletionState(report));
        Assert.False(report.Accepted);
        Assert.Null(report.Lines.Single().After);
        Assert.DoesNotContain("sensitive-test-body", MusicAlignmentValidation.Serialize(report));
    }

    [Theory]
    [InlineData("remote_high_accuracy_api_not_configured")]
    [InlineData("remote_high_accuracy_lyrics_empty")]
    [InlineData("remote_high_accuracy_api_empty_response")]
    [InlineData("remote_high_accuracy_timeout")]
    [InlineData("remote_high_accuracy_transport_failed")]
    [InlineData("remote_high_accuracy_invalid_response")]
    public void ServiceFailuresAreNotMislabelledAsAudioReview(string code)
    {
        var report = MusicAlignmentValidation.BuildFailure(Snapshot(), code);
        Assert.Equal(code, report.FailureCode);
        Assert.Equal("failed", MusicAlignmentValidation.CompletionState(report));
        Assert.Equal(code, MusicAlignmentValidation.CompletionFailureCode(report));
        Assert.Equal(0, report.MatchedCount);
        Assert.All(report.Lines, line => { Assert.False(line.Matched); Assert.Null(line.After); });
    }

    [Fact]
    public void HistoricalReportsKeepExistingStateAndMissingFieldCompatibility()
    {
        var report = MusicAlignmentValidation.BuildFailure(Snapshot(), "remote_high_accuracy_timeout") with { FailureCode = null };
        var json = MusicAlignmentValidation.Serialize(report);
        var oldJson = json.Replace(",\"failureCode\":null", "");
        var historical = MusicAlignmentValidation.Parse<MusicAlignmentReport>(oldJson);
        Assert.Null(historical.FailureCode);
        Assert.Equal("needs_review", MusicAlignmentValidation.CompletionState(historical));
        Assert.Equal("verification_incomplete", MusicAlignmentValidation.CompletionFailureCode(historical));
    }

    [Theory]
    [InlineData("HTTP 500 sensitive-test-body")]
    [InlineData("remote_high_accuracy_http_200")]
    [InlineData("remote_high_accuracy_http_500 sensitive-test-body")]
    public void UnknownFailureDetailsAreNeverPersistedAsCodes(string unsafeCode)
    {
        var remote = new RemoteHighAccuracyAlignmentClient.RemoteAlignmentReport(new(false, new(), unsafeCode, "sensitive-test-body"));
        var report = MusicAlignmentValidation.Build(Snapshot(), remote);
        Assert.Equal("remote_high_accuracy_response_missing", report.FailureCode);
        Assert.DoesNotContain("sensitive-test-body", MusicAlignmentValidation.Serialize(report));
    }

    [Fact]
    public void RealResponseStillUsesAudioReviewRegardlessOfAttemptFailure()
    {
        var snapshot = Snapshot();
        var response = new RemoteHighAccuracyAlignmentClient.HighAccuracyAlignResponse(snapshot.SongUid,
            snapshot.YoutubeUrl, "ja", "test", 10, new(), 0, 1);
        var report = MusicAlignmentValidation.Build(snapshot, new(new(false, new(), "remote_high_accuracy_api_failed"), response));
        Assert.Null(report.FailureCode);
        Assert.Equal("needs_review", MusicAlignmentValidation.CompletionState(report));
        Assert.Equal("verification_incomplete", MusicAlignmentValidation.CompletionFailureCode(report));
        Assert.False(report.Accepted);
    }

    private sealed class Factory(int status) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(status));
    }

    private sealed class Handler(int status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            { Content = new StringContent("sensitive-test-body") });
    }
}
