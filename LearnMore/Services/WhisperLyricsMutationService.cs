using System.Data.SqlClient;
using System.Text.RegularExpressions;
using LearnMore.Models;

namespace LearnMore.Services;

public class WhisperLyricsMutationService : IWhisperLyricsMutationService
{
    private static readonly Regex SongUidPattern = new("^[A-Za-z0-9_-]+$", RegexOptions.Compiled);
    private readonly IConfiguration _configuration;

    public WhisperLyricsMutationService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task UpdateLyricsAsync(string songUid, IReadOnlyCollection<LyricSegment> lyrics, CancellationToken cancellationToken = default)
    {
        ValidateSongUid(songUid);

        if (lyrics == null)
        {
            throw new ArgumentNullException(nameof(lyrics));
        }

        string tableName = $"[Songs_{songUid}]";
        string updateQuery = $@"
UPDATE {tableName}
SET [TimeStamp] = @TimeStamp,
    [Japanese] = @Japanese,
    [Chinese] = @Chinese,
    [Roman] = @Roman
WHERE [LyricID] = @LyricID";

        await using var connection = new SqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        await connection.OpenAsync(cancellationToken);

        foreach (var lyric in lyrics)
        {
            await using var command = new SqlCommand(updateQuery, connection);
            command.Parameters.AddWithValue("@TimeStamp", lyric.TimeStamp);
            command.Parameters.AddWithValue("@Japanese", string.IsNullOrWhiteSpace(lyric.Japanese) ? (object)DBNull.Value : lyric.Japanese);
            command.Parameters.AddWithValue("@Chinese", string.IsNullOrWhiteSpace(lyric.Chinese) ? (object)DBNull.Value : lyric.Chinese);
            command.Parameters.AddWithValue("@Roman", string.IsNullOrWhiteSpace(lyric.Roman) ? (object)DBNull.Value : lyric.Roman);
            command.Parameters.AddWithValue("@LyricID", lyric.LyricID);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task UpdateOrderAsync(string songUid, IReadOnlyList<int> newOrder, CancellationToken cancellationToken = default)
    {
        ValidateSongUid(songUid);

        if (newOrder == null || newOrder.Count == 0)
        {
            throw new ArgumentException("Invalid order data.", nameof(newOrder));
        }

        var lyricsData = new List<LyricRow>();
        string tableName = $"[Songs_{songUid}]";
        string selectQuery = $@"
SELECT [LyricID], [TimeStamp], [Japanese], [Chinese], [JapaneseRuby], [Roman]
FROM {tableName}
ORDER BY [TimeStamp] ASC";

        await using var connection = new SqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        await connection.OpenAsync(cancellationToken);

        await using (var selectCommand = new SqlCommand(selectQuery, connection))
        await using (var reader = await selectCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                lyricsData.Add(new LyricRow(
                    reader.GetInt32(0),
                    reader.GetDouble(1),
                    reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    reader.IsDBNull(5) ? string.Empty : reader.GetString(5)));
            }
        }

        if (lyricsData.Count != newOrder.Count)
        {
            throw new InvalidOperationException("Data mismatch: number of lyrics and new order count do not match.");
        }

        string updateQuery = $@"
UPDATE {tableName}
SET [TimeStamp] = @NewTimeStamp,
    [Japanese] = @Japanese,
    [Chinese] = @Chinese,
    [JapaneseRuby] = @JapaneseRuby,
    [Roman] = @Roman
WHERE [LyricID] = @LyricID";

        var reorderedLyrics = newOrder
            .Select((lyricId, index) => (LyricID: lyricId, Data: lyricsData[index]))
            .ToList();

        foreach (var lyric in reorderedLyrics)
        {
            await using var command = new SqlCommand(updateQuery, connection);
            command.Parameters.AddWithValue("@NewTimeStamp", lyric.Data.TimeStamp);
            command.Parameters.AddWithValue("@Japanese", string.IsNullOrWhiteSpace(lyric.Data.Japanese) ? (object)DBNull.Value : lyric.Data.Japanese);
            command.Parameters.AddWithValue("@Chinese", string.IsNullOrWhiteSpace(lyric.Data.Chinese) ? (object)DBNull.Value : lyric.Data.Chinese);
            command.Parameters.AddWithValue("@JapaneseRuby", string.IsNullOrWhiteSpace(lyric.Data.JapaneseRuby) ? (object)DBNull.Value : lyric.Data.JapaneseRuby);
            command.Parameters.AddWithValue("@Roman", string.IsNullOrWhiteSpace(lyric.Data.Roman) ? (object)DBNull.Value : lyric.Data.Roman);
            command.Parameters.AddWithValue("@LyricID", lyric.LyricID);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<bool> DeleteLyricAsync(string songUid, int lyricId, CancellationToken cancellationToken = default)
    {
        ValidateSongUid(songUid);

        string deleteQuery = $@"
DELETE FROM [language].[dbo].[Songs_{songUid}]
WHERE [LyricID] = @LyricID";

        await using var connection = new SqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(deleteQuery, connection);
        command.Parameters.AddWithValue("@LyricID", lyricId);
        int rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);
        return rowsAffected > 0;
    }

    private static void ValidateSongUid(string songUid)
    {
        if (string.IsNullOrWhiteSpace(songUid) || !SongUidPattern.IsMatch(songUid))
        {
            throw new ArgumentException("Invalid songUid.", nameof(songUid));
        }
    }

    private sealed record LyricRow(int LyricID, double TimeStamp, string Japanese, string Chinese, string JapaneseRuby, string Roman);
}
