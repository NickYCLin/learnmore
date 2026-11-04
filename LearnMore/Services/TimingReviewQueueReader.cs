using System.Data;
using System.Data.SqlClient;

namespace LearnMore.Services;

public sealed record TimingReviewQueueItem(string SongUid, string? Title, string? YoutubeUrl,
    string? YoutubeId, string Status, string? Reason, Guid? JobId, string? RequestKeyHash,
    string? JobState, string? FailureCode, DateTime? JobUpdatedAt, DateTime? AppliedAt);

public sealed record TimingReviewQueuePage(int Page, int PageSize, long TotalCount,
    bool JobsAvailable, IReadOnlyList<TimingReviewQueueItem> Items)
{
    public long TotalPages => (TotalCount + PageSize - 1) / PageSize;
    public string Scope => "all_songs";
    public string RequestIdAvailability => "Only SHA256 request keys are stored; original request IDs cannot be recovered.";
}

public interface ITimingReviewQueueReader
{
    Task<TimingReviewQueuePage> ReadAsync(int page, int pageSize, CancellationToken cancellationToken);
}

public sealed class TimingReviewQueueSchemaException : Exception;

public sealed class TimingReviewQueueReader(IConfiguration configuration) : ITimingReviewQueueReader
{
    public static bool IsValidPage(int page, int pageSize) => page >= 1 && pageSize is >= 1 and <= 100;

    public static bool NeedsReview(string? status)
        => status is "high_accuracy_partial" or "high_accuracy_needs_review" or "high_accuracy_failed";

    public async Task<TimingReviewQueuePage> ReadAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        if (!IsValidPage(page, pageSize)) throw new ArgumentOutOfRangeException(nameof(page));
        await using var connection = new SqlConnection(configuration.GetConnectionString("DefaultConnection"));
        await connection.OpenAsync(cancellationToken);
        const string schemaSql = """
            SELECT
              (SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.Songs') AND name IN
                ('SongID','SongUid','Title','YouTubeVideoUrl','HighAccuracyStatus','HighAccuracyStatusReason')),
              OBJECT_ID(N'dbo.MusicAlignmentJobs', N'U'),
              (SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.MusicAlignmentJobs') AND name IN
                ('JobId','RequestKey','SongUid','State','FailureCode','CreatedAt','UpdatedAt','AppliedAt'));
            """;
        bool jobsAvailable;
        await using (var schemaCommand = new SqlCommand(schemaSql, connection))
        await using (var schema = await schemaCommand.ExecuteReaderAsync(cancellationToken))
        {
            await schema.ReadAsync(cancellationToken);
            if (schema.GetInt32(0) != 6 || (!schema.IsDBNull(1) && schema.GetInt32(2) != 8))
                throw new TimingReviewQueueSchemaException();
            jobsAvailable = !schema.IsDBNull(1);
        }
        const string queueCte = """
            ;WITH candidates AS (
              SELECT SongID,SongUid,Title,YouTubeVideoUrl,HighAccuracyStatus,HighAccuracyStatusReason,
                ROW_NUMBER() OVER(PARTITION BY SongUid ORDER BY SongID DESC) AS DuplicateRank
              FROM dbo.Songs WHERE HighAccuracyStatus IN (@Partial,@NeedsReview,@Failed)
            ), queue AS (SELECT * FROM candidates WHERE DuplicateRank=1)
            """;
        var jobColumns = jobsAvailable
            ? "jobs.JobId,jobs.RequestKey,jobs.State,jobs.FailureCode,jobs.UpdatedAt,jobs.AppliedAt"
            : "CAST(NULL AS uniqueidentifier),CAST(NULL AS char(64)),CAST(NULL AS varchar(32)),CAST(NULL AS varchar(100)),CAST(NULL AS datetime2),CAST(NULL AS datetime2)";
        var jobJoin = jobsAvailable ? """
            OUTER APPLY (SELECT TOP(1) JobId,RequestKey,State,FailureCode,UpdatedAt,AppliedAt
              FROM dbo.MusicAlignmentJobs WHERE SongUid=queue.SongUid ORDER BY CreatedAt DESC,JobId DESC) jobs
            """ : "";
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        await using var command = new SqlCommand(queueCte + " SELECT COUNT_BIG(*) FROM queue; " + queueCte + $"""
            SELECT queue.SongUid,queue.Title,queue.YouTubeVideoUrl,queue.HighAccuracyStatus,queue.HighAccuracyStatusReason,
              {jobColumns}
            FROM queue {jobJoin}
            ORDER BY queue.SongID DESC,queue.SongUid
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY;
            """, connection, transaction);
        command.Parameters.Add("@Partial", SqlDbType.NVarChar, 50).Value = "high_accuracy_partial";
        command.Parameters.Add("@NeedsReview", SqlDbType.NVarChar, 50).Value = "high_accuracy_needs_review";
        command.Parameters.Add("@Failed", SqlDbType.NVarChar, 50).Value = "high_accuracy_failed";
        command.Parameters.Add("@Offset", SqlDbType.BigInt).Value = ((long)page - 1) * pageSize;
        command.Parameters.Add("@PageSize", SqlDbType.Int).Value = pageSize;
        long totalCount;
        var items = new List<TimingReviewQueueItem>();
        await using (var rows = await command.ExecuteReaderAsync(cancellationToken))
        {
            await rows.ReadAsync(cancellationToken);
            totalCount = rows.GetInt64(0);
            await rows.NextResultAsync(cancellationToken);
            while (await rows.ReadAsync(cancellationToken))
            {
                string? Text(int ordinal) => rows.IsDBNull(ordinal) ? null : rows.GetString(ordinal);
                string? videoId = null;
                try { videoId = MusicAlignmentStore.VideoId(Text(2) ?? ""); }
                catch (MusicAutomationException) { }
                items.Add(new TimingReviewQueueItem(Text(0)!, Text(1), Text(2), videoId, Text(3)!, Text(4),
                    rows.IsDBNull(5) ? null : rows.GetGuid(5), Text(6), Text(7), Text(8),
                    rows.IsDBNull(9) ? null : rows.GetDateTime(9), rows.IsDBNull(10) ? null : rows.GetDateTime(10)));
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return new TimingReviewQueuePage(page, pageSize, totalCount, jobsAvailable, items);
    }
}
