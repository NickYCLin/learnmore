using System.Text.Json;

namespace LearnMore.Services;

public sealed class ServerSentEventStream : IAsyncDisposable
{
    private readonly HttpResponse _response;
    private readonly CancellationTokenSource _shutdown;
    private readonly CancellationTokenSource _heartbeatStop;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly Task _heartbeat;
    public CancellationToken CancellationToken { get; }

    public ServerSentEventStream(HttpResponse response, CancellationToken requestAborted, TimeSpan? interval = null)
    {
        _response = response;
        _shutdown = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        CancellationToken = _shutdown.Token;
        _heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        _heartbeat = SendHeartbeatsAsync(interval ?? TimeSpan.FromSeconds(15));
    }

    public Task SendAsync(string eventName, object data)
        => WriteAsync($"event: {eventName}\ndata: {JsonSerializer.Serialize(data)}\n\n");

    private async Task SendHeartbeatsAsync(TimeSpan interval)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(_heartbeatStop.Token))
                await WriteAsync(": keep-alive\n\n");
        }
        catch (OperationCanceledException) when (_heartbeatStop.IsCancellationRequested) { }
    }

    private async Task WriteAsync(string frame)
    {
        var entered = false;
        try
        {
            await _writer.WaitAsync(CancellationToken);
            entered = true;
            await _response.WriteAsync(frame, CancellationToken);
            await _response.Body.FlushAsync(CancellationToken);
        }
        catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested) { }
        catch (IOException) { _shutdown.Cancel(); }
        finally { if (entered) _writer.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        _heartbeatStop.Cancel();
        await _heartbeat;
        _shutdown.Cancel();
        _writer.Dispose();
        _heartbeatStop.Dispose();
        _shutdown.Dispose();
    }
}
