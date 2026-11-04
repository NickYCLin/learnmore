using System.Data;
using System.Data.SqlClient;

namespace LearnMore.Services;

public sealed record ErrorReportRawTarget(int ReportId, int ReportLyricId, int FirstLyricId, int SecondLyricId)
{
    public static ErrorReportRawTarget? Find(int reportId) => reportId switch
    {
        47 => new(47, 7, 7, 8),
        50 => new(50, 22, 21, 22),
        _ => null
    };
}

public sealed record ErrorReportRawSnapshot(int ReportId, string SongUid, string VideoSource,
    IReadOnlyList<SortedDictionary<string, object?>> Lyrics);

public interface IErrorReportRawReader
{
    Task<ErrorReportRawSnapshot> ReadAsync(int reportId, CancellationToken cancellationToken);
}

public sealed class ErrorReportRawReader(IConfiguration configuration) : IErrorReportRawReader
{
    public static ErrorReportRawSnapshot Validate(int reportId, SortedDictionary<string, object?> report,
        SortedDictionary<string, object?> song, IReadOnlyList<SortedDictionary<string, object?>> lyrics)
    {
        var target = ErrorReportRawTarget.Find(reportId) ?? throw new RubyRepairConflictException();
        if (Convert.ToString(report["ReportID"]) != reportId.ToString()
            || !Guid.TryParse(Convert.ToString(report["ErrorSongUid"]), out var songUid)
            || songUid.ToString() != RubyRepairTarget.SongUid
            || Convert.ToString(report["ErrorLyricID"]) != target.ReportLyricId.ToString()
            || !Guid.TryParse(Convert.ToString(song["SongUid"]), out var actualSongUid)
            || actualSongUid != songUid
            || !string.Equals(Convert.ToString(song["YouTubeVideoUrl"]), RubyRepairTarget.VideoSource, StringComparison.Ordinal)
            || lyrics.Count != 2)
            throw new RubyRepairConflictException();
        var expectedIds = new[] { target.FirstLyricId, target.SecondLyricId };
        for (var index = 0; index < expectedIds.Length; index++)
        {
            var lyric = lyrics[index];
            if (Convert.ToString(lyric["LyricID"]) != expectedIds[index].ToString()) throw new RubyRepairConflictException();
            foreach (var field in new[] { "Japanese", "JapaneseRuby", "Roman", "Chinese", "TimeStamp" })
                if (!lyric.ContainsKey(field)) throw new RubyRepairConflictException();
        }
        return new(reportId, RubyRepairTarget.SongUid, RubyRepairTarget.VideoSource, lyrics);
    }

    public async Task<ErrorReportRawSnapshot> ReadAsync(int reportId, CancellationToken cancellationToken)
    {
        var target = ErrorReportRawTarget.Find(reportId) ?? throw new RubyRepairConflictException();
        await using var connection = new SqlConnection(configuration.GetConnectionString("DefaultConnection"));
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(IsolationLevel.RepeatableRead);
        var report = await ReadRowAsync(connection, transaction,
            "SELECT ReportID,ErrorSongUid,ErrorLyricID FROM dbo.ErrorReports WHERE ReportID=@Id", reportId, cancellationToken);
        var song = await ReadRowAsync(connection, transaction,
            "SELECT SongUid,YouTubeVideoUrl FROM dbo.Songs WHERE SongUid=@Id", RubyRepairTarget.SongUid, cancellationToken);
        var lyrics = new List<SortedDictionary<string, object?>>();
        foreach (var lyricId in new[] { target.FirstLyricId, target.SecondLyricId })
            lyrics.Add(await ReadRowAsync(connection, transaction,
                $"SELECT LyricID,Japanese,JapaneseRuby,Roman,Chinese,TimeStamp FROM dbo.[Songs_{RubyRepairTarget.SongUid}] WHERE LyricID=@Id",
                lyricId, cancellationToken));
        var snapshot = Validate(reportId, report, song, lyrics);
        await transaction.CommitAsync(cancellationToken);
        return snapshot;
    }

    private static async Task<SortedDictionary<string, object?>> ReadRowAsync(SqlConnection connection,
        SqlTransaction transaction, string sql, object identifier, CancellationToken cancellationToken)
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
