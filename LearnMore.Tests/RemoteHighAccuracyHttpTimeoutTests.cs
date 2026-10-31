using System.Net;
using System.Text;
using LearnMore.Models;
using LearnMore.Options;
using LearnMore.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LearnMore.Tests;

public class RemoteHighAccuracyHttpTimeoutTests
{
    [Fact]
    public async Task ConfiguredDeadlineAllowsResponseBeyondFactoryDefaultTimeout()
    {
        var handler = new DelegateHandler(async token =>
        {
            await Task.Delay(150, token);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"youtubeUrl":"https://www.youtube.com/watch?v=LjuJgrdh5eY","language":"ja","model":"fixture",
                    "alignedLines":[{"lyricId":1,"japanese":"歌詞","start":1.5,"end":2.5,"score":0.95,"source":"whisper_word_match"}],
                    "matchedCount":1,"totalCount":1}
                    """, Encoding.UTF8, "application/json")
            };
        });
        var result = await CreateClient(handler).AlignAsync(Snapshot());
        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.MatchedCount);
        Assert.Equal(1.5, Assert.Single(result.Alignments).Start);
    }

    [Fact]
    public async Task CallerCancellationStillStopsInFlightAlignment()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;
        var handler = new DelegateHandler(async token =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { cancelled = true; throw; }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var cts = new CancellationTokenSource();
        var pending = CreateClient(handler).AlignAsync(Snapshot(), cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(cancelled);
    }

    private static RemoteHighAccuracyAlignmentClient CreateClient(HttpMessageHandler handler)
        => new(Microsoft.Extensions.Options.Options.Create(new VocalOnsetDetectionOptions
        {
            UseRemoteHighAccuracyApi = true,
            RemoteHighAccuracyApiBaseUrl = "https://example.invalid/",
            RemoteHighAccuracyApiToken = "synthetic-test-value",
            RemoteHighAccuracyApiTimeoutSeconds = 60
        }), new ClientFactory(handler), NullLogger<RemoteHighAccuracyAlignmentClient>.Instance);

    private static SongLyricsProcessingSnapshot Snapshot() => new()
    {
        SongUid = "00000000-0000-0000-0000-000000000001",
        YouTubeUrl = "https://www.youtube.com/watch?v=LjuJgrdh5eY",
        Lyrics = new() { new() { LyricID = 1, Japanese = "歌詞", TimeStamp = 1 } }
    };

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromMilliseconds(50)
        };
    }

    private sealed class DelegateHandler(Func<CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(cancellationToken);
    }
}
