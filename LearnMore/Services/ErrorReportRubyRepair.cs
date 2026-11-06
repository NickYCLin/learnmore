using System.Data;
using System.Data.SqlClient;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LearnMore.Services;

public sealed record RubyRepairTarget(int ReportId, int LyricId, string Before, string After)
{
    public const string SongUid = "69e99493-ac81-4f17-941e-834df328555e";
    public const string VideoSource = "https://www.youtube.com/watch?v=iqEr3P78fz8";
    public static RubyRepairTarget? Find(int reportId) => reportId switch
    {
        38 => new(38, 10, "<ruby>呟<rt>げん</rt></ruby>", "<ruby>呟<rt>つぶや</rt></ruby>"),
        46 => new(46, 3, "<ruby>人<rt>にん</rt></ruby>", "<ruby>人<rt>ひと</rt></ruby>"),
        48 => new(48, 13, "<ruby>人<rt>にん</rt></ruby>", "<ruby>人<rt>ひと</rt></ruby>"),
        49 => new(49, 15, "<ruby>重な<rt>おもな</rt></ruby>", "<ruby>重な<rt>かさな</rt></ruby>"),
        _ => null
    };

    public string Propose(string original)
    {
        var position = original.IndexOf(Before, StringComparison.Ordinal);
        if (position < 0 || original.IndexOf(Before, position + Before.Length, StringComparison.Ordinal) >= 0)
            throw new RubyRepairConflictException();
        return original[..position] + After + original[(position + Before.Length)..];
    }
}

public sealed class RubyRepairConflictException : Exception;

public sealed record RubyRepairSnapshot(int ReportId, SortedDictionary<string, object?> Report,
    SortedDictionary<string, object?> Song, SortedDictionary<string, object?> Lyric)
{
    [JsonIgnore]
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this)))).ToLowerInvariant();
}

public sealed record RubyRepairPreview(RubyRepairSnapshot Snapshot, string Fingerprint, string? ProposedRuby)
{
    public static RubyRepairPreview ForRead(RubyRepairSnapshot snapshot)
    {
        var target = RubyRepairTarget.Find(snapshot.ReportId) ?? throw new RubyRepairConflictException();
        string? proposedRuby;
        try { proposedRuby = target.Propose(Convert.ToString(snapshot.Lyric["JapaneseRuby"]) ?? ""); }
        catch (RubyRepairConflictException) { proposedRuby = null; }
        return new(snapshot, snapshot.Fingerprint, proposedRuby);
    }
}
public sealed record RubyRepairReceipt(string AuditId, bool CompletionRecorded);

public interface IErrorReportRubyRepair
{
    Task<RubyRepairPreview> ReadAsync(int reportId, CancellationToken cancellationToken);
    Task<RubyRepairReceipt> ApplyAsync(int reportId, string fingerprint, string actor, CancellationToken cancellationToken);
}

public interface IRubyRepairAudit
{
    string Prepare(RubyRepairSnapshot before, string afterRuby, string actor);
    bool Complete(string auditId);
}

public sealed class FileRubyRepairAudit : IRubyRepairAudit
{
    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LearnMore", "RubyRepairAudit");

    public string Prepare(RubyRepairSnapshot before, string afterRuby, string actor)
    {
        Directory.CreateDirectory(Root);
        var auditId = Guid.NewGuid().ToString("N");
        Write(Path.Combine(Root, auditId + ".prepared.json"), new
        {
            auditId, actor, preparedAtUtc = DateTime.UtcNow, before, afterRuby,
            changedField = "JapaneseRuby", statusChanged = false,
            outcome = "prepared_only_not_proof_of_commit"
        });
        return auditId;
    }

    public bool Complete(string auditId)
    {
        try
        {
            Write(Path.Combine(Root, auditId + ".committed.json"), new { auditId, committedAtUtc = DateTime.UtcNow });
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static void Write(string path, object record)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record);
        stream.Write(bytes);
        stream.Flush(true);
    }
}

public sealed class ErrorReportRubyRepair(IConfiguration configuration, IRubyRepairAudit audit) : IErrorReportRubyRepair
{
    public async Task<RubyRepairPreview> ReadAsync(int reportId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(configuration.GetConnectionString("DefaultConnection"));
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var result = await ReadLockedAsync(connection, transaction, reportId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return RubyRepairPreview.ForRead(result);
    }

    public async Task<RubyRepairReceipt> ApplyAsync(int reportId, string fingerprint, string actor, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actor)) throw new RubyRepairConflictException();
        await using var connection = new SqlConnection(configuration.GetConnectionString("DefaultConnection"));
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var snapshot = await ReadLockedAsync(connection, transaction, reportId, cancellationToken);
        var proposedRuby = RubyRepairTarget.Find(reportId)!.Propose(Convert.ToString(snapshot.Lyric["JapaneseRuby"]) ?? "");
        var preview = new RubyRepairPreview(snapshot, snapshot.Fingerprint, proposedRuby);
        if (!string.Equals(preview.Fingerprint, fingerprint, StringComparison.Ordinal)) throw new RubyRepairConflictException();
        var auditId = audit.Prepare(preview.Snapshot, proposedRuby, actor);
        await using var command = new SqlCommand($"UPDATE dbo.[Songs_{RubyRepairTarget.SongUid}] SET JapaneseRuby=@Ruby WHERE LyricID=@LyricId", connection, transaction);
        command.Parameters.Add("@Ruby", SqlDbType.NVarChar, -1).Value = proposedRuby;
        command.Parameters.Add("@LyricId", SqlDbType.Int).Value = RubyRepairTarget.Find(reportId)!.LyricId;
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new RubyRepairConflictException();
        var after = await ReadRowAsync(connection, transaction,
            $"SELECT * FROM dbo.[Songs_{RubyRepairTarget.SongUid}] WITH (UPDLOCK,HOLDLOCK) WHERE LyricID=@Id", RubyRepairTarget.Find(reportId)!.LyricId, cancellationToken);
        var expected = new SortedDictionary<string, object?>(preview.Snapshot.Lyric, StringComparer.Ordinal) { ["JapaneseRuby"] = proposedRuby };
        if (JsonSerializer.Serialize(after) != JsonSerializer.Serialize(expected)) throw new RubyRepairConflictException();
        var reportAfter = await ReadRowAsync(connection, transaction, "SELECT * FROM dbo.ErrorReports WITH (UPDLOCK,HOLDLOCK) WHERE ReportID=@Id", reportId, cancellationToken);
        var songAfter = await ReadRowAsync(connection, transaction, "SELECT * FROM dbo.Songs WITH (UPDLOCK,HOLDLOCK) WHERE SongUid=@Id", RubyRepairTarget.SongUid, cancellationToken);
        if (JsonSerializer.Serialize(reportAfter) != JsonSerializer.Serialize(preview.Snapshot.Report)
            || JsonSerializer.Serialize(songAfter) != JsonSerializer.Serialize(preview.Snapshot.Song)) throw new RubyRepairConflictException();
        await transaction.CommitAsync(cancellationToken);
        return new RubyRepairReceipt(auditId, audit.Complete(auditId));
    }

    private static async Task<RubyRepairSnapshot> ReadLockedAsync(SqlConnection connection, SqlTransaction transaction, int reportId, CancellationToken cancellationToken)
    {
        var target = RubyRepairTarget.Find(reportId) ?? throw new RubyRepairConflictException();
        var report = await ReadRowAsync(connection, transaction, "SELECT * FROM dbo.ErrorReports WITH (UPDLOCK,HOLDLOCK) WHERE ReportID=@Id", reportId, cancellationToken);
        if (!Guid.TryParse(Convert.ToString(report["ErrorSongUid"]), out var songUid) || songUid.ToString() != RubyRepairTarget.SongUid
            || Convert.ToString(report["ErrorLyricID"]) != target.LyricId.ToString()) throw new RubyRepairConflictException();
        var song = await ReadRowAsync(connection, transaction, "SELECT * FROM dbo.Songs WITH (UPDLOCK,HOLDLOCK) WHERE SongUid=@Id", RubyRepairTarget.SongUid, cancellationToken);
        if (!string.Equals(Convert.ToString(song["YouTubeVideoUrl"]), RubyRepairTarget.VideoSource, StringComparison.Ordinal)) throw new RubyRepairConflictException();
        var lyric = await ReadRowAsync(connection, transaction, $"SELECT * FROM dbo.[Songs_{RubyRepairTarget.SongUid}] WITH (UPDLOCK,HOLDLOCK) WHERE LyricID=@Id", target.LyricId, cancellationToken);
        foreach (var required in new[] { "Japanese", "JapaneseRuby", "Roman", "Chinese", "TimeStamp", "LyricID" })
            if (!lyric.ContainsKey(required)) throw new RubyRepairConflictException();
        return new RubyRepairSnapshot(reportId, report, song, lyric);
    }

    private static async Task<SortedDictionary<string, object?>> ReadRowAsync(SqlConnection connection, SqlTransaction transaction, string sql, object identifier, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@Id", identifier);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new RubyRepairConflictException();
        var row = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
            row.Add(reader.GetName(ordinal), reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal));
        if (await reader.ReadAsync(cancellationToken)) throw new RubyRepairConflictException();
        return row;
    }
}
