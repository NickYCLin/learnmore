using LearnMore.Options;
using Microsoft.Extensions.Options;

namespace LearnMore.Services;

public sealed class AudioStemProcessingHostedService : BackgroundService
{
    private static readonly string TracePath = @"D:\Data\learnmore-audio-stems-trace.log";
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AudioStemProcessingHostedService> _logger;
    private readonly AudioStemProcessingOptions _options;

    public AudioStemProcessingHostedService(
        IServiceScopeFactory scopeFactory,
        IOptions<AudioStemProcessingOptions> options,
        ILogger<AudioStemProcessingHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("音軌背景隊列停用。");
            return;
        }

        await ProcessDueJobsAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(10, _options.PollIntervalSeconds)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await ProcessDueJobsAsync(stoppingToken);
        }
    }

    private async Task ProcessDueJobsAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var jobService = scope.ServiceProvider.GetRequiredService<IAudioStemJobService>();
                var processor = scope.ServiceProvider.GetRequiredService<IAudioStemProcessor>();
                var job = await jobService.TryLeaseNextJobAsync(stoppingToken);
                if (job == null)
                {
                    return;
                }

                AppendTrace($"start songUid={job.SongUid}; attempt={job.AttemptCount}/{job.MaxAttempts}");
                _logger.LogInformation("開始背景處理伴奏/人聲 songUid={SongUid} attempt={Attempt}/{MaxAttempts}", job.SongUid, job.AttemptCount, job.MaxAttempts);
                try
                {
                    await processor.ProcessAsync(job, stoppingToken);
                    await jobService.MarkJobCompletedAsync(job, stoppingToken);
                    AppendTrace($"completed songUid={job.SongUid}");
                    _logger.LogInformation("背景伴奏/人聲處理完成 songUid={SongUid}", job.SongUid);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await jobService.MarkJobFailedAsync(job, ex.Message, CancellationToken.None);
                    AppendTrace($"failed songUid={job.SongUid}; error={ex.Message}");
                    _logger.LogWarning(ex, "背景伴奏/人聲處理失敗 songUid={SongUid}", job.SongUid);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                AppendTrace($"queue-exception {ex.Message}");
                _logger.LogWarning(ex, "音軌背景隊列掃描失敗");
                return;
            }
        }
    }

    private static void AppendTrace(string message)
    {
        try
        {
            File.AppendAllText(
                TracePath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}
