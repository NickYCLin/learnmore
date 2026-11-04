using System.Net;
using System.Text;
using LearnMore.Models;
using LearnMore.Options;
using LearnMore.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static LearnMore.Services.MusicAlignmentValidation;
using static LearnMore.Services.RemoteHighAccuracyAlignmentClient;

namespace LearnMore.Tests;

public sealed class MusicLocalEvidenceTests
{
    private static MusicSongSnapshot Snapshot() => new("fixture-song", "https://www.youtube.com/watch?v=abcdefghijk",
        "high_accuracy_needs_review", "keep", new() { new() { LyricID = 7, Japanese = "Stand by", TimeStamp = 2,
            Chinese = "保留", JapaneseRuby = "Stand by", Roman = "Stand by" } });

    [Theory]
    [InlineData("en", "en", true)]
    [InlineData("ja", "ja", true)]
    [InlineData("en", "ja", false)]
    [InlineData("auto", "auto", false)]
    public async Task LanguageRequiresExplicitMatchingContract(string requested, string returned, bool accepted)
    {
        var snapshot = Snapshot();
        var response = Response(snapshot, returned);
        var handler = new Handler(response);
        var binding = new MusicLocalEvidenceBinding(new('a', 32), Hash(snapshot), requested);
        var remote = await Client(handler).VerifyLocalEvidenceAsync(snapshot, binding);
        var report = Build(snapshot, remote, requested);
        Assert.Equal(accepted, report.Accepted);
        Assert.Equal("/v1/local-alignment-evidence/verify", handler.Path);
        Assert.Contains("\"currentStart\":2", handler.Body);
        Assert.Contains("\"snapshotHash\":\"" + Hash(snapshot), handler.Body);
        Assert.DoesNotContain("accepted", handler.Body);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("model")]
    [InlineData("duplicate")]
    [InlineData("http")]
    public async Task UntrustedResponseCannotBecomeEvidence(string changed)
    {
        var snapshot = Snapshot();
        var response = Response(snapshot, "en");
        if (changed == "source") response = response with { AlignedLines = new() { response.AlignedLines[0] with { Source = "whisperx_contextual_timestamp_bridge" } } };
        if (changed == "model") response = response with { Model = "qwen3-asr-pinned-local-evidence:client" };
        if (changed == "duplicate") response.AlignedLines.Add(response.AlignedLines[0]);
        var exception = await Assert.ThrowsAsync<MusicAutomationException>(() => Client(new Handler(response,
            changed == "http" ? HttpStatusCode.Conflict : HttpStatusCode.OK)).VerifyLocalEvidenceAsync(snapshot,
                new(new('a', 32), Hash(snapshot), "en")));
        Assert.StartsWith("evidence_", exception.Code);
        Assert.DoesNotContain("sensitive", exception.Message);
    }

    [Theory]
    [InlineData("song")]
    [InlineData("video")]
    [InlineData("text")]
    [InlineData("zero")]
    [InlineData("low")]
    [InlineData("context")]
    public async Task OriginalSourceTextTimingAndScoreGatesStillReject(string changed)
    {
        var snapshot = Snapshot();
        var response = Response(snapshot, "en");
        if (changed == "song") response = response with { SongUid = "other" };
        if (changed == "video") response = response with { YoutubeUrl = snapshot.YoutubeUrl + "other" };
        var line = response.AlignedLines[0];
        if (changed == "text") line = line with { Japanese = "Other" };
        if (changed == "zero") line = line with { End = line.Start };
        if (changed == "low") line = line with { Score = 0.1 };
        if (changed == "context") line = line with { Source = "current_timestamp_context" };
        response = response with { AlignedLines = new() { line } };
        var remote = await Client(new Handler(response)).VerifyLocalEvidenceAsync(snapshot, new(new('a', 32), Hash(snapshot), "en"));
        Assert.False(Build(snapshot, remote, "en").Accepted);
    }

    private static HighAccuracyAlignResponse Response(MusicSongSnapshot snapshot, string language) => new(
        snapshot.SongUid, snapshot.YoutubeUrl, language, "qwen3-asr-pinned-local-evidence:" + new string('a', 64),
        10, new() { new(7, "Stand by", 2.1, 2.8, "qwen3_asr_word_match", 1, null, null, null) }, 1, 1);

    private static RemoteHighAccuracyAlignmentClient Client(Handler handler) => new(
        Microsoft.Extensions.Options.Options.Create(new VocalOnsetDetectionOptions {
            UseRemoteHighAccuracyApi = true, RemoteHighAccuracyApiBaseUrl = "https://example.invalid/",
            RemoteHighAccuracyApiToken = "fixture-token" }), new Factory(handler),
        NullLogger<RemoteHighAccuracyAlignmentClient>.Instance);

    private sealed class Factory(Handler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    private sealed class Handler(HighAccuracyAlignResponse response, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string Path { get; private set; } = "";
        public string Body { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Path = request.RequestUri!.AbsolutePath;
            Body = await request.Content!.ReadAsStringAsync(ct);
            Assert.Equal("fixture-token", request.Headers.Authorization!.Parameter);
            return new(status) { Content = new StringContent(status == HttpStatusCode.OK ? Serialize(response) : "sensitive", Encoding.UTF8, "application/json") };
        }
    }
}
