using System.Data;
using System.Data.SqlClient;
using LearnMore.Models;
using LearnMore.Options;
using Microsoft.Extensions.Options;

namespace LearnMore.Services;

public sealed class AudioStemJobService : IAudioStemJobService
{
    private readonly IConfiguration _configuration;
    private readonly AudioStemProcessingOptions _options;

    public AudioStemJobService(IConfiguration configuration, IOptions<AudioStemProcessingOptions> options)
    {
        _configuration = configuration;
        _options = options.Value;
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(SchemaSql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task EnqueueSongAsync(string songUid, string? youTubeVideoUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(songUid) || string.IsNullOrWhiteSpace(youTubeVideoUrl))
        {
            return;
        }

        await EnsureSchemaAsync(cancellationToken);
        await using var connection = new SqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(@"
IF NOT EXISTS (
    SELECT 1
    FROM dbo.SongAudioStemJobs WITH (UPDLOCK, HOLDLOCK)
    WHERE SongUid = @SongUid
)
AND NOT EXISTS (
    SELECT stems.SongUid
    FROM dbo.SongAudioStems stems
    WHERE stems.SongUid = @SongUid
      AND stems.StemKind IN (N'instrumental', N'vocals')
    GROUP BY stems.SongUid
    HAVING COUNT(DISTINCT stems.StemKind) >= 2
)
BEGIN
    INSERT INTO dbo.SongAudioStemJobs
        (SongUid, YouTubeVideoUrl, Status, MaxAttempts, NextAttemptAt, CreatedAt, UpdatedAt)
    VALUES
        (@SongUid, @YouTubeVideoUrl, N'pending', @MaxAttempts, SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME())
END", connection);
        command.Parameters.AddWithValue("@SongUid", songUid);
        command.Parameters.AddWithValue("@YouTubeVideoUrl", youTubeVideoUrl);
        command.Parameters.AddWithValue("@MaxAttempts", Math.Max(1, _options.MaxAttempts));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<AudioStemJob?> TryLeaseNextJobAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = new SqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        try
        {
            await using var command = new SqlCommand(@"
DECLARE @JobId int;

SELECT TOP (1) @JobId = jobs.Id
FROM dbo.SongAudioStemJobs jobs WITH (UPDLOCK, READPAST, ROWLOCK)
WHERE jobs.AttemptCount < jobs.MaxAttempts
  AND (
      jobs.Status IN (N'pending', N'failed')
      OR (jobs.Status = N'processing' AND (jobs.LockedUntil IS NULL OR jobs.LockedUntil < SYSUTCDATETIME()))
  )
  AND (jobs.NextAttemptAt IS NULL OR jobs.NextAttemptAt <= SYSUTCDATETIME())
  AND NOT EXISTS (
      SELECT stems.SongUid
      FROM dbo.SongAudioStems stems
      WHERE stems.SongUid = jobs.SongUid
        AND stems.StemKind IN (N'instrumental', N'vocals')
      GROUP BY stems.SongUid
      HAVING COUNT(DISTINCT stems.StemKind) >= 2
  )
ORDER BY jobs.CreatedAt, jobs.Id;

IF @JobId IS NULL
BEGIN
    SELECT CAST(NULL AS int) AS Id WHERE 1 = 0;
    RETURN;
END

UPDATE dbo.SongAudioStemJobs
SET Status = N'processing',
    AttemptCount = AttemptCount + 1,
    StartedAt = SYSUTCDATETIME(),
    LockedUntil = DATEADD(minute, @LeaseMinutes, SYSUTCDATETIME()),
    UpdatedAt = SYSUTCDATETIME()
WHERE Id = @JobId;

SELECT Id, SongUid, YouTubeVideoUrl, AttemptCount, MaxAttempts
FROM dbo.SongAudioStemJobs
WHERE Id = @JobId;", connection, transaction);
            command.Parameters.AddWithValue("@LeaseMinutes", Math.Max(5, _options.LeaseMinutes));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            AudioStemJob? job = null;
            if (await reader.ReadAsync(cancellationToken))
            {
                job = new AudioStemJob
                {
                    Id = reader.GetInt32(reader.GetOrdinal("Id")),
                    SongUid = reader.GetString(reader.GetOrdinal("SongUid")),
                    YouTubeVideoUrl = reader.GetString(reader.GetOrdinal("YouTubeVideoUrl")),
                    AttemptCount = reader.GetInt32(reader.GetOrdinal("AttemptCount")),
                    MaxAttempts = reader.GetInt32(reader.GetOrdinal("MaxAttempts"))
                };
            }

            await reader.CloseAsync();
            await transaction.CommitAsync(cancellationToken);
            return job;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task MarkJobCompletedAsync(AudioStemJob job, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(@"
UPDATE dbo.SongAudioStemJobs
SET Status = N'completed',
    CompletedAt = SYSUTCDATETIME(),
    LockedUntil = NULL,
    LastError = NULL,
    UpdatedAt = SYSUTCDATETIME()
WHERE Id = @Id", connection);
        command.Parameters.AddWithValue("@Id", job.Id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkJobFailedAsync(AudioStemJob job, string error, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(@"
UPDATE dbo.SongAudioStemJobs
SET Status = CASE WHEN AttemptCount >= MaxAttempts THEN N'dead' ELSE N'failed' END,
    LastError = @LastError,
    LockedUntil = NULL,
    NextAttemptAt = CASE
        WHEN AttemptCount >= MaxAttempts THEN NULL
        ELSE DATEADD(minute, @RetryDelayMinutes, SYSUTCDATETIME())
    END,
    UpdatedAt = SYSUTCDATETIME()
WHERE Id = @Id", connection);
        command.Parameters.AddWithValue("@Id", job.Id);
        command.Parameters.AddWithValue("@RetryDelayMinutes", Math.Max(1, _options.RetryDelayMinutes));
        command.Parameters.AddWithValue("@LastError", Truncate(error, 4000));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RegisterCompletedStemsAsync(string songUid, string instrumentalPath, string vocalsPath, string modelName, string source, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = new SqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            // A worker may finish after its song/account was deleted. Do not recreate orphaned stems.
            await using (var exists = new SqlCommand("SELECT COUNT(*) FROM dbo.Songs WITH (UPDLOCK, HOLDLOCK) WHERE SongUid = @SongUid", connection, transaction))
            {
                exists.Parameters.AddWithValue("@SongUid", songUid);
                if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken)) == 0)
                {
                    // Keep cleanup durable when mobile account deletion is installed.
                    await using var cleanup = new SqlCommand(@"
IF OBJECT_ID('dbo.MobileFileDeletionJobs', 'U') IS NOT NULL
BEGIN
    INSERT INTO dbo.MobileFileDeletionJobs (FileName, Kind) VALUES (@SongUid, 'song');
    SELECT 1;
END
ELSE SELECT 0;", connection, transaction);
                    cleanup.Parameters.AddWithValue("@SongUid", songUid);
                    if (Convert.ToInt32(await cleanup.ExecuteScalarAsync(cancellationToken)) == 0)
                    {
                        File.Delete(instrumentalPath);
                        File.Delete(vocalsPath);
                    }
                    await transaction.CommitAsync(cancellationToken);
                    return;
                }
            }
            await using (var deleteCommand = new SqlCommand("DELETE FROM dbo.SongAudioStems WHERE SongUid = @SongUid AND StemKind IN (N'instrumental', N'vocals')", connection, transaction))
            {
                deleteCommand.Parameters.AddWithValue("@SongUid", songUid);
                await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var stem in new[]
            {
                new { Kind = "instrumental", Path = instrumentalPath, File = "instrumental.flac" },
                new { Kind = "vocals", Path = vocalsPath, File = "vocals.flac" }
            })
            {
                await using var insertCommand = new SqlCommand(@"
INSERT INTO dbo.SongAudioStems (SongUid, StemKind, PublicUrl, StoragePath, ModelName, Source, UpdatedAt)
VALUES (@SongUid, @StemKind, @PublicUrl, @StoragePath, @ModelName, @Source, SYSUTCDATETIME())", connection, transaction);
                insertCommand.Parameters.AddWithValue("@SongUid", songUid);
                insertCommand.Parameters.AddWithValue("@StemKind", stem.Kind);
                insertCommand.Parameters.AddWithValue("@PublicUrl", $"~/audio-stems/{songUid}/{stem.File}");
                insertCommand.Parameters.AddWithValue("@StoragePath", stem.Path);
                insertCommand.Parameters.AddWithValue("@ModelName", string.IsNullOrWhiteSpace(modelName) ? (object)DBNull.Value : modelName);
                insertCommand.Parameters.AddWithValue("@Source", string.IsNullOrWhiteSpace(source) ? (object)DBNull.Value : source);
                await insertCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength];
    }

    private const string SchemaSql = @"
IF OBJECT_ID('dbo.SongAudioStems', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.SongAudioStems
    (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_SongAudioStems PRIMARY KEY,
        SongUid nvarchar(500) NOT NULL,
        StemKind nvarchar(50) NOT NULL,
        PublicUrl nvarchar(2048) NOT NULL,
        StoragePath nvarchar(2048) NULL,
        ModelName nvarchar(200) NULL,
        Source nvarchar(100) NULL,
        CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_SongAudioStems_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt datetime2(0) NOT NULL CONSTRAINT DF_SongAudioStems_UpdatedAt DEFAULT SYSUTCDATETIME()
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SongAudioStems_SongUid_StemKind_CreatedAt' AND object_id = OBJECT_ID('dbo.SongAudioStems'))
BEGIN
    CREATE INDEX IX_SongAudioStems_SongUid_StemKind_CreatedAt
        ON dbo.SongAudioStems (SongUid, StemKind, CreatedAt DESC);
END;

IF OBJECT_ID('dbo.SongAudioStemJobs', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.SongAudioStemJobs
    (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_SongAudioStemJobs PRIMARY KEY,
        SongUid nvarchar(500) NOT NULL,
        YouTubeVideoUrl nvarchar(2048) NOT NULL,
        Status nvarchar(50) NOT NULL CONSTRAINT DF_SongAudioStemJobs_Status DEFAULT N'pending',
        AttemptCount int NOT NULL CONSTRAINT DF_SongAudioStemJobs_AttemptCount DEFAULT 0,
        MaxAttempts int NOT NULL CONSTRAINT DF_SongAudioStemJobs_MaxAttempts DEFAULT 3,
        NextAttemptAt datetime2(0) NULL,
        LockedUntil datetime2(0) NULL,
        LastError nvarchar(4000) NULL,
        CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_SongAudioStemJobs_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt datetime2(0) NOT NULL CONSTRAINT DF_SongAudioStemJobs_UpdatedAt DEFAULT SYSUTCDATETIME(),
        StartedAt datetime2(0) NULL,
        CompletedAt datetime2(0) NULL
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_SongAudioStemJobs_SongUid' AND object_id = OBJECT_ID('dbo.SongAudioStemJobs'))
BEGIN
    CREATE UNIQUE INDEX UX_SongAudioStemJobs_SongUid ON dbo.SongAudioStemJobs (SongUid);
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SongAudioStemJobs_Status_NextAttemptAt' AND object_id = OBJECT_ID('dbo.SongAudioStemJobs'))
BEGIN
    CREATE INDEX IX_SongAudioStemJobs_Status_NextAttemptAt
        ON dbo.SongAudioStemJobs (Status, NextAttemptAt, CreatedAt)
        INCLUDE (AttemptCount, MaxAttempts, LockedUntil);
END;
";
}
