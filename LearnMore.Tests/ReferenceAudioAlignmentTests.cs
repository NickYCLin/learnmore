using System.Net;
using System.Text;
using LearnMore.Models;
using LearnMore.Options;
using LearnMore.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LearnMore.Tests;

public sealed class ReferenceAudioAlignmentTests
{
    [Theory]
    [InlineData("valid", true)]
    [InlineData("missing_proof", false)]
    [InlineData("wrong_video", false)]
    [InlineData("wrong_provider", false)]
    [InlineData("weak_recording", false)]
    [InlineData("weak_window", false)]
    [InlineData("missing_windows", false)]
    [InlineData("missing_lines", false)]
    [InlineData("inflated_score", false)]
    [InlineData("invalid_overlap", false)]
    public async Task ReferenceSourceRequiresCompleteRecordingEvidence(string change, bool expected)
    {
        var proof = new RemoteHighAccuracyAlignmentClient.ReferenceAudioEvidence(
            change == "wrong_provider" ? "https://example.invalid/lyrics" : "https://www.marumaru-x.com/japanese-song/play-vxn5w9eln9",
            change == "wrong_video" ? "LjuJgrdh5eY" : "-D_Me-bDFGU", "x-37TLZcqbQ", new('a', 64),
            change == "weak_recording" ? 0.94 : 0.99, change == "weak_window" ? 0.89 : 0.97,
            change == "missing_windows" ? 1 : 12, change == "invalid_overlap" ? 0.99 : 1, 0, 60, 60,
            change == "missing_lines" ? 0 : 1);
        var response = new RemoteHighAccuracyAlignmentClient.HighAccuracyAlignResponse(
            "song", "https://www.youtube.com/watch?v=-D_Me-bDFGU", "ja",
            "marumaru-timed-reference+recording-identity-12-windows", 60,
            new() { new(1, "Stand by", 3, 4, "marumaru_reference_audio_timing_verified",
                change == "inflated_score" ? 1 : 0.97, null, null, null) }, 1, 1,
            change == "missing_proof" ? null : proof);
        var body = MusicAlignmentValidation.Serialize(response);
        var client = new RemoteHighAccuracyAlignmentClient(Microsoft.Extensions.Options.Options.Create(new VocalOnsetDetectionOptions
        { UseRemoteHighAccuracyApi = true, RemoteHighAccuracyApiBaseUrl = "https://example.invalid/", RemoteHighAccuracyApiToken = "test-only" }),
            new Factory(body), NullLogger<RemoteHighAccuracyAlignmentClient>.Instance);
        var result = await client.AlignWithReportAsync(new SongLyricsProcessingSnapshot
        { SongUid = "song", YouTubeUrl = response.YoutubeUrl, Lyrics = new() { new() { LyricID = 1, Japanese = "Stand by", TimeStamp = 2 } } });
        Assert.Equal(expected, result.Attempt.Alignments.Single().IsMatched);
    }

    private sealed class Factory(string body) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(body));
    }
    private sealed class Handler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
