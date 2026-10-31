using LearnMore.Models;

namespace LearnMore.Services;

public sealed class MusicAlignmentWorker(MusicAlignmentStore store, RemoteHighAccuracyAlignmentClient client,
    IConfiguration config, ILogger<MusicAlignmentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue<bool>("MusicAutomation:Enabled") || (config["MusicAutomation:ReadToken"]?.Length ?? 0) < 32) return;
        var schemaReady = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!schemaReady) { await store.EnsureSchemaAsync(stoppingToken); schemaReady = true; }
                var claim = await store.ClaimAsync(stoppingToken);
                if (claim != null)
                {
                    var (id, snapshot) = claim.Value;
                    try
                    {
                        var remote = await client.AlignWithReportAsync(new SongLyricsProcessingSnapshot
                        { SongUid = snapshot.SongUid, YouTubeUrl = snapshot.YoutubeUrl, Lyrics = snapshot.Lyrics }, stoppingToken);
                        await store.CompleteAsync(id, MusicAlignmentValidation.Build(snapshot, remote), stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                    catch (Exception ex)
                    {
                        logger.LogWarning("音樂對齊工作失敗 jobId={JobId} type={Type}", id, ex.GetType().Name);
                        await store.CompleteAsync(id, null, stoppingToken);
                    }
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning("音樂工作佇列暫時無法讀取 type={Type}", ex.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}
