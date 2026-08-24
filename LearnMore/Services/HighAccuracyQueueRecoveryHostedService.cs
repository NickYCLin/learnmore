namespace LearnMore.Services;

public sealed class HighAccuracyQueueRecoveryHostedService : BackgroundService
{
    private static readonly string HighAccuracyTracePath = Path.Combine(
        Path.GetTempPath(),
        "learnmore-high-accuracy-trace.log");
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<HighAccuracyQueueRecoveryHostedService> _logger;
    private readonly TimeSpan _interval;
    private readonly bool _enabled;

    public HighAccuracyQueueRecoveryHostedService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<HighAccuracyQueueRecoveryHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _enabled = configuration.GetValue<bool?>("HighAccuracyQueueRecoveryEnabled") ?? true;
        var intervalMinutes = Math.Max(1, configuration.GetValue<int?>("HighAccuracyQueueRecoveryIntervalMinutes") ?? 5);
        _interval = TimeSpan.FromMinutes(intervalMinutes);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            AppendTrace("queue-recovery:disabled");
            _logger.LogInformation("高精度背景隊列恢復掃描已由設定停用");
            return;
        }

        await RecoverQueueAsync(stoppingToken);

        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RecoverQueueAsync(stoppingToken);
        }
    }

    private async Task RecoverQueueAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var queryService = scope.ServiceProvider.GetRequiredService<IWhisperLyricsQueryService>();
            var highAccuracyService = scope.ServiceProvider.GetRequiredService<IWhisperHighAccuracyInitialPassService>();
            var songs = await queryService.GetRetryableHighAccuracySongsAsync(stoppingToken, includeNeedsReview: false);
            if (songs.Count == 0)
            {
                return;
            }

            AppendTrace($"queue-recovery: retryable={songs.Count}; songUids={string.Join(",", songs.Select(song => song.SongUid))}");
            _logger.LogInformation("高精度背景隊列恢復掃描排入 {Count} 首", songs.Count);
            foreach (var song in songs.Where(song => !string.IsNullOrWhiteSpace(song.SongUid)))
            {
                highAccuracyService.EnqueueHighAccuracyInitialPass(song.SongUid);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "高精度背景隊列恢復掃描失敗");
            AppendTrace($"queue-recovery:exception {ex.Message}");
        }
    }

    private static void AppendTrace(string message)
    {
        try
        {
            File.AppendAllText(
                HighAccuracyTracePath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}
