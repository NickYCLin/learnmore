using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LearnMore.Services;

public sealed record Report50RepairRow(int LyricID, string Japanese, string JapaneseRuby, string Roman, string Chinese, string TimeStamp);
public sealed record Report50RepairSource(string url, string supports);
public sealed record Report50RepairTarget(string sourceSha256, Report50RepairSource[] sources, Report50RepairRow[] before, Report50RepairRow[] after);

public sealed record Report50RepairSnapshot(int ReportId, SortedDictionary<string, object?> Report,
    SortedDictionary<string, object?> Song, IReadOnlyList<SortedDictionary<string, object?>> Lyrics)
{
    [JsonIgnore]
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this))).ToLowerInvariant();
}

public sealed record Report50RepairPreview(Report50RepairSnapshot Snapshot, bool CanApply, bool AlreadyApplied,
    IReadOnlyList<Report50RepairRow> Candidate, IReadOnlyList<Report50RepairSource> Sources);

public static class Report50RepairPlan
{
    private static readonly Report50RepairTarget Target = LoadTarget();

    private static Report50RepairTarget LoadTarget()
    {
        using var stream = typeof(Report50RepairPlan).Assembly.GetManifestResourceStream("LearnMore.Report50RepairTarget.json")!;
        return JsonSerializer.Deserialize<Report50RepairTarget>(stream)!;
    }

    public static Report50RepairPreview Preview(Report50RepairSnapshot snapshot)
    {
        ValidateBinding(snapshot);
        return new(snapshot, Matches(snapshot.Lyrics, Target.before), Matches(snapshot.Lyrics, Target.after),
            Array.AsReadOnly(Target.after), Array.AsReadOnly(Target.sources));
    }

    public static Report50RepairSnapshot Prepare(Report50RepairSnapshot before, string fingerprint)
    {
        if (!Preview(before).CanApply || !string.Equals(before.Fingerprint, fingerprint, StringComparison.Ordinal))
            throw new RubyRepairConflictException();
        var lyrics = before.Lyrics.Select((row, index) => new SortedDictionary<string, object?>(row, StringComparer.Ordinal)
        {
            ["Japanese"] = Target.after[index].Japanese,
            ["JapaneseRuby"] = Target.after[index].JapaneseRuby,
            ["Roman"] = Target.after[index].Roman
        }).ToArray();
        return before with { Lyrics = lyrics };
    }

    public static void ValidateBinding(Report50RepairSnapshot snapshot)
    {
        if (snapshot.ReportId != 50) throw new RubyRepairConflictException();
        ErrorReportRawReader.Validate(snapshot.ReportId, snapshot.Report, snapshot.Song, snapshot.Lyrics);
    }

    public static bool Same(Report50RepairSnapshot expected, Report50RepairSnapshot actual)
        => JsonSerializer.Serialize(expected) == JsonSerializer.Serialize(actual);

    private static bool Matches(IReadOnlyList<SortedDictionary<string, object?>> actual, Report50RepairRow[] expected)
    {
        for (var index = 0; index < expected.Length; index++)
        {
            var row = actual[index];
            var target = expected[index];
            if (!Equals(row["Japanese"], target.Japanese) || !Equals(row["JapaneseRuby"], target.JapaneseRuby)
                || !Equals(row["Roman"], target.Roman) || !Equals(row["Chinese"], target.Chinese)
                || row["TimeStamp"] == null
                || !decimal.TryParse(Convert.ToString(row["TimeStamp"], CultureInfo.InvariantCulture), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var timestamp)
                || timestamp != decimal.Parse(target.TimeStamp, CultureInfo.InvariantCulture)) return false;
        }
        return true;
    }
}

public interface IReport50Repair
{
    Task<Report50RepairPreview> ReadAsync(int reportId, CancellationToken cancellationToken);
    Task<RubyRepairReceipt> ApplyAsync(int reportId, string fingerprint, string actor, CancellationToken cancellationToken);
}

public interface IReport50RepairAudit
{
    string Prepare(Report50RepairSnapshot before, Report50RepairSnapshot after, string actor);
    bool Complete(string auditId, Report50RepairSnapshot after);
}

public sealed class FileReport50RepairAudit : IReport50RepairAudit
{
    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LearnMore", "Report50RepairAudit");

    public string Prepare(Report50RepairSnapshot before, Report50RepairSnapshot after, string actor)
    {
        Directory.CreateDirectory(Root);
        var auditId = Guid.NewGuid().ToString("N");
        Write(Path.Combine(Root, auditId + ".prepared.json"), new
        {
            auditId, actor, preparedAtUtc = DateTime.UtcNow, before, after,
            beforeFingerprint = before.Fingerprint, afterFingerprint = after.Fingerprint,
            changedFields = new[] { "Japanese", "JapaneseRuby", "Roman" }, statusChanged = false,
            outcome = "prepared_only_not_proof_of_commit"
        });
        return auditId;
    }

    public bool Complete(string auditId, Report50RepairSnapshot after)
    {
        try
        {
            Write(Path.Combine(Root, auditId + ".committed.json"), new
            { auditId, committedAtUtc = DateTime.UtcNow, after, afterFingerprint = after.Fingerprint });
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static void Write(string path, object record)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(JsonSerializer.SerializeToUtf8Bytes(record));
        stream.Flush(true);
    }
}

public sealed class Report50Repair(IConfiguration configuration, IReport50RepairAudit audit) : IReport50Repair
{
    public async Task<Report50RepairPreview> ReadAsync(int reportId, CancellationToken cancellationToken)
    {
        if (reportId != 50) throw new RubyRepairConflictException();
        await using var connection = new SqlConnection(configuration.GetConnectionString("DefaultConnection"));
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(IsolationLevel.RepeatableRead);
        var snapshot = await ReadSnapshotAsync(connection, transaction, false, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Report50RepairPlan.Preview(snapshot);
    }

    public async Task<RubyRepairReceipt> ApplyAsync(int reportId, string fingerprint, string actor, CancellationToken cancellationToken)
    {
        if (reportId != 50 || string.IsNullOrWhiteSpace(actor)) throw new RubyRepairConflictException();
        await using var connection = new SqlConnection(configuration.GetConnectionString("DefaultConnection"));
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var before = await ReadSnapshotAsync(connection, transaction, true, cancellationToken);
        var expected = Report50RepairPlan.Prepare(before, fingerprint);
        var auditId = audit.Prepare(before, expected, actor);
        foreach (var row in expected.Lyrics)
        {
            await using var command = new SqlCommand($"UPDATE dbo.[Songs_{RubyRepairTarget.SongUid}] SET Japanese=@Japanese,JapaneseRuby=@JapaneseRuby,Roman=@Roman WHERE LyricID=@LyricId", connection, transaction);
            foreach (var field in new[] { "Japanese", "JapaneseRuby", "Roman" })
                command.Parameters.Add("@" + field, SqlDbType.NVarChar, -1).Value = row[field]!;
            command.Parameters.Add("@LyricId", SqlDbType.Int).Value = row["LyricID"]!;
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new RubyRepairConflictException();
        }
        var after = await ReadSnapshotAsync(connection, transaction, true, cancellationToken);
        if (!Report50RepairPlan.Same(expected, after)) throw new RubyRepairConflictException();
        await transaction.CommitAsync(cancellationToken);
        return new(auditId, audit.Complete(auditId, after));
    }

    private static async Task<Report50RepairSnapshot> ReadSnapshotAsync(SqlConnection connection, SqlTransaction transaction,
        bool forUpdate, CancellationToken cancellationToken)
    {
        var locking = forUpdate ? " WITH (UPDLOCK,HOLDLOCK)" : "";
        var report = await ReadRowAsync(connection, transaction, $"SELECT * FROM dbo.ErrorReports{locking} WHERE ReportID=@Id", 50, cancellationToken);
        var song = await ReadRowAsync(connection, transaction, $"SELECT * FROM dbo.Songs{locking} WHERE SongUid=@Id", RubyRepairTarget.SongUid, cancellationToken);
        var lyrics = new List<SortedDictionary<string, object?>>();
        foreach (var lyricId in new[] { 21, 22 })
            lyrics.Add(await ReadRowAsync(connection, transaction, $"SELECT * FROM dbo.[Songs_{RubyRepairTarget.SongUid}]{locking} WHERE LyricID=@Id", lyricId, cancellationToken));
        var snapshot = new Report50RepairSnapshot(50, report, song, lyrics);
        Report50RepairPlan.ValidateBinding(snapshot);
        return snapshot;
    }

    private static async Task<SortedDictionary<string, object?>> ReadRowAsync(SqlConnection connection, SqlTransaction transaction,
        string sql, object identifier, CancellationToken cancellationToken)
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
