using System.Net;
using System.Text;
using LearnMore.Models;
using LearnMore.Options;
using LearnMore.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LearnMore.Tests;

public sealed class QwenAlignmentEvidenceTests
{
    [Theory]
    [InlineData("qwen3_asr_word_match", 1.0, true)]
    [InlineData("qwen3_asr_word_match", 0.70, true)]
    [InlineData("qwen3_asr_word_match", 0.69, false)]
    [InlineData("qwen3_asr_contextual_timestamp_bridge", 1.0, false)]
    [InlineData("qwen3_asr_word_match_unverified", 1.0, false)]
    public async Task QwenWordEvidenceKeepsExistingAudioThreshold(string source, double score, bool expected)
    {
        var response = new RemoteHighAccuracyAlignmentClient.HighAccuracyAlignResponse(
            "song", "https://www.youtube.com/watch?v=qbcqvrV6-2Q", "ja", "primary+qwen3-asr-evidence", 10,
            new() { new(1, "Stand by", 2, 3, source, score, null, null, null) }, 1, 1);
        var client = new RemoteHighAccuracyAlignmentClient(Microsoft.Extensions.Options.Options.Create(
            new VocalOnsetDetectionOptions { UseRemoteHighAccuracyApi = true,
                RemoteHighAccuracyApiBaseUrl = "https://example.invalid/", RemoteHighAccuracyApiToken = "test-only" }),
            new Factory(MusicAlignmentValidation.Serialize(response)), NullLogger<RemoteHighAccuracyAlignmentClient>.Instance);
        var result = await client.AlignWithReportAsync(new SongLyricsProcessingSnapshot {
            SongUid = "song", YouTubeUrl = response.YoutubeUrl,
            Lyrics = new() { new() { LyricID = 1, Japanese = "Stand by", TimeStamp = 1.9 } } });
        Assert.Equal(expected, result.Attempt.Alignments.Single().IsMatched);
    }

    private sealed class Factory(string body) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(body));
    }
    private sealed class Handler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
