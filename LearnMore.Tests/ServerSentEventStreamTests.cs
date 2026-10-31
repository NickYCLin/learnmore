using System.Text;
using System.Text.Json;
using LearnMore.Services;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LearnMore.Tests;

public class ServerSentEventStreamTests
{
    [Fact]
    public async Task NormalDisposalFinishesAnInFlightHeartbeat()
    {
        var body = new PausedStream();
        var response = new DefaultHttpContext().Response;
        response.Body = body;
        var stream = new ServerSentEventStream(response, CancellationToken.None, TimeSpan.FromMilliseconds(5));
        await body.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var disposal = stream.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        body.Continue.TrySetResult();
        await disposal.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(": keep-alive\n\n", Encoding.UTF8.GetString(body.ToArray()));
    }

    [Fact]
    public async Task LongWorkReceivesHeartbeatAndDisposalStopsIt()
    {
        var body = new ObservedStream();
        var response = new DefaultHttpContext().Response;
        response.Body = body;
        var stream = new ServerSentEventStream(response, CancellationToken.None, TimeSpan.FromMilliseconds(5));
        await body.Written.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await stream.DisposeAsync();
        var length = body.Length;
        Assert.Contains(": keep-alive\n\n", Encoding.UTF8.GetString(body.ToArray()));
        await Task.Delay(20);
        Assert.Equal(length, body.Length);
    }

    [Fact]
    public async Task ConcurrentProgressAndHeartbeatRemainSeparateFrames()
    {
        var body = new ObservedStream();
        var response = new DefaultHttpContext().Response;
        response.Body = body;
        await using (var stream = new ServerSentEventStream(response, CancellationToken.None, TimeSpan.FromMilliseconds(1)))
        {
            await body.Written.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.WhenAll(Enumerable.Range(0, 30).Select(i => stream.SendAsync("progress", new { index = i })));
        }
        var frames = Encoding.UTF8.GetString(body.ToArray()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var indexes = new List<int>();
        foreach (var frame in frames)
        {
            if (frame == ": keep-alive") continue;
            Assert.StartsWith("event: progress\ndata: ", frame);
            using var json = JsonDocument.Parse(frame["event: progress\ndata: ".Length..]);
            indexes.Add(json.RootElement.GetProperty("index").GetInt32());
        }
        Assert.Equal(Enumerable.Range(0, 30), indexes.Order());
    }

    [Fact]
    public async Task ClientCancellationStopsFurtherWrites()
    {
        var response = new DefaultHttpContext().Response;
        response.Body = new MemoryStream();
        using var request = new CancellationTokenSource();
        await using var stream = new ServerSentEventStream(response, request.Token);
        request.Cancel();
        await stream.SendAsync("done", new { success = true });
        Assert.True(stream.CancellationToken.IsCancellationRequested);
        Assert.Equal(0, response.Body.Length);
    }

    [Fact]
    public async Task BrokenConnectionCancelsWorkWithoutAnotherResponseWrite()
    {
        var response = new DefaultHttpContext().Response;
        response.Body = new BrokenStream();
        await using var stream = new ServerSentEventStream(response, CancellationToken.None);
        await stream.SendAsync("progress", new { message = "處理中" });
        Assert.True(stream.CancellationToken.IsCancellationRequested);
    }

    private sealed class ObservedStream : MemoryStream
    {
        public TaskCompletionSource Written { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var half = buffer.Length / 2;
            await base.WriteAsync(buffer[..half], cancellationToken);
            await Task.Yield();
            await base.WriteAsync(buffer[half..], cancellationToken);
            Written.TrySetResult();
        }
    }

    private sealed class BrokenStream : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException(new IOException("connection closed"));
    }

    private sealed class PausedStream : MemoryStream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var half = buffer.Length / 2;
            await base.WriteAsync(buffer[..half], cancellationToken);
            Started.TrySetResult();
            await Continue.Task;
            await base.WriteAsync(buffer[half..], cancellationToken);
        }
    }
}
