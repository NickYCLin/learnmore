using System.Data.SqlClient;
using System.Text.RegularExpressions;

namespace LearnMore.Services.Mobile;

// Durable outbox: deleting an avatar fails independently of deleting its account and retries after restart.
public sealed class MobileFileCleanupService(IConfiguration config, IWebHostEnvironment environment,
    ILogger<MobileFileCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue<bool>("MobileAuth:Enabled")) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                await using var db = new SqlConnection(config.GetConnectionString("DefaultConnection"));
                await db.OpenAsync(stoppingToken);
                using var query = new SqlCommand("SELECT TOP (100) Id, FileName, Kind FROM dbo.MobileFileDeletionJobs ORDER BY Id", db);
                var jobs = new List<(long Id, string Name, string Kind)>();
                using (var rows = await query.ExecuteReaderAsync(stoppingToken))
                    while (await rows.ReadAsync(stoppingToken)) jobs.Add((rows.GetInt64(0), rows.GetString(1), rows.GetString(2)));
                foreach (var job in jobs)
                {
                    if (job.Kind == "song" && Regex.IsMatch(job.Name, "\\A[A-Za-z0-9_-]{1,100}\\z"))
                    {
                        var workRoot = config["AudioStemProcessing:WorkRoot"];
                        if (string.IsNullOrWhiteSpace(workRoot)) workRoot = Path.Combine(Path.GetTempPath(), "learnmore-audio-stems");
                        foreach (var path in new[] { Path.Combine(environment.WebRootPath, "audio-stems", job.Name),
                            Path.Combine(workRoot, job.Name), Path.Combine(workRoot, "remote-api", job.Name) })
                            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                    }
                    else
                    {
                    if (job.Name != Path.GetFileName(job.Name) || !Guid.TryParse(Path.GetFileNameWithoutExtension(job.Name), out _) ||
                        !Path.GetExtension(job.Name).Equals(".png", StringComparison.OrdinalIgnoreCase) || job.Kind != "avatar")
                    { logger.LogError("Invalid mobile file cleanup job {JobId}", job.Id); continue; }
                    File.Delete(Path.Combine(environment.WebRootPath, "uploads", job.Name));
                    }
                    using var done = new SqlCommand("DELETE FROM dbo.MobileFileDeletionJobs WHERE Id = @Id", db);
                    done.Parameters.AddWithValue("@Id", job.Id);
                    await done.ExecuteNonQueryAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { logger.LogError(exception, "Mobile avatar cleanup will retry."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
