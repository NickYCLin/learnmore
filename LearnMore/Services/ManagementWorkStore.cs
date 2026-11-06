using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LearnMore.Services;

public sealed record ManagementWorkEvent(long EventId, string EvidenceHash, string Status,
    string Note, string ActorEmail, DateTime UpdatedAtUtc);

public sealed record ManagementWorkTarget(string Kind, string ItemId, string? Title,
    string? Description, string? SongUid, string EvidenceHash);

public sealed record ManagementWorkDetail(ManagementWorkTarget Target, IReadOnlyList<ManagementWorkEvent> History)
{
    public ManagementWorkEvent? Latest => History.FirstOrDefault();
    public ManagementWorkEvent? Current => Latest?.EvidenceHash == Target.EvidenceHash ? Latest : null;
}

public sealed record ManagementWorkBadge(string Kind, string ItemId, string EvidenceHash,
    ManagementWorkEvent? Latest)
{
    public ManagementWorkEvent? Current => Latest?.EvidenceHash == EvidenceHash ? Latest : null;
}

public sealed class ManagementWorkConflictException : Exception;
public sealed class ManagementWorkUnavailableException : Exception;

public interface IManagementWorkStore
{
    Task<IReadOnlyDictionary<string, ManagementWorkEvent>> ReadLatestAsync(string kind,
        IReadOnlyList<string> itemIds, CancellationToken cancellationToken);
    Task<ManagementWorkDetail?> ReadAsync(string kind, string itemId, CancellationToken cancellationToken);
    Task SaveAsync(string kind, string itemId, long expectedRevision, string evidenceHash,
        string status, string note, string actorEmail, CancellationToken cancellationToken);
}

public static class ManagementWork
{
    public static bool ValidTarget(string kind, string? itemId)
        => kind is "report" or "review" && !string.IsNullOrWhiteSpace(itemId)
            && itemId.Length <= 450 && !itemId.Any(char.IsControl);
    public static bool ValidStatus(string status) => status is "pending" or "in_progress" or "resolved";
    public static string Label(string status) => status switch
    {
        "resolved" => "已處理",
        "in_progress" => "處理中",
        _ => "待處理"
    };
    public static string Hash(params object?[] values)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values))));
    public static string Evidence(TimingReviewQueueItem song)
        => Hash(song.SongUid, song.Status, song.Reason, song.JobId, song.JobState,
            song.FailureCode, song.JobUpdatedAt, song.AppliedAt);
    public static string Evidence(ErrorReportItem report, string keyColumn)
        => Hash(keyColumn, report.ReportId, report.SongUid, report.LyricId,
            report.Description, report.Status, report.ReportDate);
}

public sealed class ManagementWorkStore(IConfiguration configuration) : IManagementWorkStore
{
    public async Task<IReadOnlyDictionary<string, ManagementWorkEvent>> ReadLatestAsync(string kind,
        IReadOnlyList<string> itemIds, CancellationToken cancellationToken)
    {
        if (itemIds.Count > 100 || itemIds.Any(itemId => !ManagementWork.ValidTarget(kind, itemId)))
            throw new ArgumentException("處理項目不合法。");
        await using var connection = await OpenAsync(cancellationToken);
        await RequireSchemaAsync(connection, null, cancellationToken);
        var result = new Dictionary<string, ManagementWorkEvent>(StringComparer.Ordinal);
        if (itemIds.Count == 0) return result;
        var parameters = itemIds.Select((_, index) => "@Key" + index).ToArray();
        await using var command = new SqlCommand($"""
            WITH latest AS (
                SELECT *, ROW_NUMBER() OVER(PARTITION BY ItemKey ORDER BY EventId DESC) AS Rank
                FROM dbo.ManagementWorkEvents WHERE Kind=@Kind AND ItemKey IN ({string.Join(",", parameters)})
            ) SELECT ItemId,EventId,EvidenceHash,Status,Note,ActorEmail,UpdatedAtUtc FROM latest WHERE Rank=1;
            """, connection);
        command.Parameters.Add("@Kind", SqlDbType.VarChar, 16).Value = kind;
        for (var index = 0; index < itemIds.Count; index++)
            command.Parameters.Add(parameters[index], SqlDbType.Char, 64).Value = ManagementWork.Hash(kind, itemIds[index]);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(0), Event(reader, 1));
        return result;
    }

    public async Task<ManagementWorkDetail?> ReadAsync(string kind, string itemId, CancellationToken cancellationToken)
    {
        if (!ManagementWork.ValidTarget(kind, itemId)) throw new ArgumentException("處理項目不合法。");
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        await RequireSchemaAsync(connection, transaction, cancellationToken);
        var target = await TargetAsync(connection, transaction, kind, itemId, cancellationToken);
        if (target == null) return null;
        await using var command = ItemCommand("""
            SELECT TOP(50) EventId,EvidenceHash,Status,Note,ActorEmail,UpdatedAtUtc
            FROM dbo.ManagementWorkEvents WHERE Kind=@Kind AND ItemKey=@ItemKey ORDER BY EventId DESC;
            """, connection, transaction, kind, itemId);
        var history = new List<ManagementWorkEvent>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) history.Add(Event(reader, 0));
        await transaction.CommitAsync(cancellationToken);
        return new(target, history);
    }

    public async Task SaveAsync(string kind, string itemId, long expectedRevision, string evidenceHash,
        string status, string note, string actorEmail, CancellationToken cancellationToken)
    {
        if (!ManagementWork.ValidTarget(kind, itemId) || !ManagementWork.ValidStatus(status)
            || expectedRevision < 0 || evidenceHash.Length != 64 || !evidenceHash.All(Uri.IsHexDigit)
            || string.IsNullOrWhiteSpace(note) || note.Length > 2000
            || string.IsNullOrWhiteSpace(actorEmail) || actorEmail.Length > 320)
            throw new ArgumentException("處理狀態、備註或身分不合法。");
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        await RequireSchemaAsync(connection, transaction, cancellationToken);
        await using var command = ItemCommand("""
            DECLARE @Result int;
            EXEC @Result=sys.sp_getapplock @Resource=@ItemKey,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;
            IF @Result < 0 THROW 51000, 'Management work lock unavailable', 1;
            SELECT COALESCE(MAX(EventId),0) FROM dbo.ManagementWorkEvents WHERE Kind=@Kind AND ItemKey=@ItemKey;
            """, connection, transaction, kind, itemId);
        var revision = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        var target = await TargetAsync(connection, transaction, kind, itemId, cancellationToken);
        if (target == null || target.EvidenceHash != evidenceHash || revision != expectedRevision)
            throw new ManagementWorkConflictException();
        command.CommandText = """
            INSERT dbo.ManagementWorkEvents(Kind,ItemKey,ItemId,EvidenceHash,Status,Note,ActorEmail)
            VALUES(@Kind,@ItemKey,@ItemId,@EvidenceHash,@Status,@Note,@ActorEmail);
            """;
        command.Parameters.Add("@ItemId", SqlDbType.NVarChar, 450).Value = itemId;
        command.Parameters.Add("@EvidenceHash", SqlDbType.Char, 64).Value = evidenceHash;
        command.Parameters.Add("@Status", SqlDbType.VarChar, 16).Value = status;
        command.Parameters.Add("@Note", SqlDbType.NVarChar, 2000).Value = note.Trim();
        command.Parameters.Add("@ActorEmail", SqlDbType.NVarChar, 320).Value = actorEmail;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(configuration.GetConnectionString("DefaultConnection"));
        try { await connection.OpenAsync(cancellationToken); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    private static async Task RequireSchemaAsync(SqlConnection connection, SqlTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("""
            SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.ManagementWorkEvents', N'U')
                AND name IN ('EventId','Kind','ItemKey','ItemId','EvidenceHash','Status','Note','ActorEmail','UpdatedAtUtc');
            """, connection, transaction);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 9)
            throw new ManagementWorkUnavailableException();
    }

    private static SqlCommand ItemCommand(string sql, SqlConnection connection, SqlTransaction transaction, string kind, string itemId)
    {
        var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@Kind", SqlDbType.VarChar, 16).Value = kind;
        command.Parameters.Add("@ItemKey", SqlDbType.Char, 64).Value = ManagementWork.Hash(kind, itemId);
        return command;
    }

    private static ManagementWorkEvent Event(SqlDataReader reader, int offset)
        => new(reader.GetInt64(offset), reader.GetString(offset + 1), reader.GetString(offset + 2),
            reader.GetString(offset + 3), reader.GetString(offset + 4),
            DateTime.SpecifyKind(reader.GetDateTime(offset + 5), DateTimeKind.Utc));

    private static async Task<ManagementWorkTarget?> TargetAsync(SqlConnection connection, SqlTransaction transaction,
        string kind, string itemId, CancellationToken cancellationToken)
    {
        if (kind == "report")
        {
            await using var schema = new SqlCommand(ErrorReportReader.SchemaSql, connection, transaction);
            var key = await schema.ExecuteScalarAsync(cancellationToken) as string;
            if (string.IsNullOrEmpty(key)) throw new ErrorReportSchemaException();
            await using var command = new SqlCommand($"""
                SELECT CONVERT(nvarchar(450),reports.{ErrorReportReader.QuoteIdentifier(key)}),reports.ErrorSongUid,
                    (SELECT Title FROM dbo.Songs WHERE SongUid=reports.ErrorSongUid),
                    reports.ErrorLyricID,reports.ErrorDescription,reports.Status,reports.ReportDate
                FROM dbo.ErrorReports reports
                WHERE CONVERT(nvarchar(450),reports.{ErrorReportReader.QuoteIdentifier(key)}) COLLATE Latin1_General_100_BIN2=@ItemId;
                """, connection, transaction);
            command.Parameters.Add("@ItemId", SqlDbType.NVarChar, 450).Value = itemId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            string? Text(int ordinal) => reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
            var report = new ErrorReportItem(Text(0)!, Text(1), Text(2), null, Text(3), Text(4), Text(5),
                reader.IsDBNull(6) ? null : reader.GetDateTime(6));
            if (!string.Equals(report.ReportId, itemId, StringComparison.Ordinal)) return null;
            return new(kind, itemId, report.Title, report.Description, report.SongUid, ManagementWork.Evidence(report, key));
        }
        await using var jobs = new SqlCommand("SELECT OBJECT_ID(N'dbo.MusicAlignmentJobs', N'U');", connection, transaction);
        var jobsAvailable = await jobs.ExecuteScalarAsync(cancellationToken) is not (null or DBNull);
        var jobColumns = jobsAvailable ? "jobs.JobId,jobs.State,jobs.FailureCode,jobs.UpdatedAt,jobs.AppliedAt"
            : "CAST(NULL AS uniqueidentifier),CAST(NULL AS varchar(32)),CAST(NULL AS varchar(100)),CAST(NULL AS datetime2),CAST(NULL AS datetime2)";
        var jobJoin = jobsAvailable ? """
            OUTER APPLY (SELECT TOP(1) JobId,State,FailureCode,UpdatedAt,AppliedAt FROM dbo.MusicAlignmentJobs
                WHERE SongUid=songs.SongUid ORDER BY CreatedAt DESC,JobId DESC) jobs
            """ : "";
        await using var songCommand = new SqlCommand($"""
            SELECT TOP(1) songs.SongUid,songs.Title,songs.HighAccuracyStatus,songs.HighAccuracyStatusReason,{jobColumns}
            FROM dbo.Songs songs {jobJoin}
            WHERE songs.SongUid=@ItemId ORDER BY CASE WHEN songs.HighAccuracyStatus IN
                ('high_accuracy_partial','high_accuracy_needs_review','high_accuracy_failed') THEN 0 ELSE 1 END,songs.SongID DESC;
            """, connection, transaction);
        songCommand.Parameters.Add("@ItemId", SqlDbType.NVarChar, 450).Value = itemId;
        await using var songReader = await songCommand.ExecuteReaderAsync(cancellationToken);
        if (!await songReader.ReadAsync(cancellationToken)) return null;
        string? SongText(int ordinal) => songReader.IsDBNull(ordinal) ? null : songReader.GetString(ordinal);
        var song = new TimingReviewQueueItem(SongText(0)!, SongText(1), null, null, SongText(2)!, SongText(3),
            songReader.IsDBNull(4) ? null : songReader.GetGuid(4), null, SongText(5), SongText(6),
            songReader.IsDBNull(7) ? null : songReader.GetDateTime(7), songReader.IsDBNull(8) ? null : songReader.GetDateTime(8));
        if (!string.Equals(song.SongUid, itemId, StringComparison.Ordinal)) return null;
        return new(kind, itemId, song.Title, song.Reason, song.SongUid, ManagementWork.Evidence(song));
    }
}
