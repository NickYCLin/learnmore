using System.Data.SqlClient;
using System.Text.RegularExpressions;
using LearnMore.Models;

namespace LearnMore.Services;

public class WhisperLyricsQueryService : IWhisperLyricsQueryService
{
    private static readonly Regex SongUidPattern = new("^[A-Za-z0-9_-]+$", RegexOptions.Compiled);
    private readonly IConfiguration _configuration;

    public WhisperLyricsQueryService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task<EditLyricsViewModel?> GetEditLyricsViewModelAsync(string userEmail, string songUid, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userEmail) || string.IsNullOrWhiteSpace(songUid) || !SongUidPattern.IsMatch(songUid))
        {
            return null;
        }

        await using var connection = new SqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        await connection.OpenAsync(cancellationToken);

        if (!await HasPermissionAsync(connection, userEmail, songUid, cancellationToken))
        {
            return null;
        }

        var (songTitle, youtubeVideoUrl, highAccuracyStatus, highAccuracyStatusReason) = await LoadSongMetadataAsync(connection, songUid, cancellationToken);
        if (string.IsNullOrEmpty(songTitle) && string.IsNullOrEmpty(youtubeVideoUrl))
        {
            return null;
        }

        var lyrics = await LoadLyricsAsync(connection, songUid, cancellationToken);
        return new EditLyricsViewModel
        {
            SongUid = songUid,
            MNmae = songTitle,
            YoutubeVideoUrl = youtubeVideoUrl,
            HighAccuracyStatus = highAccuracyStatus,
            HighAccuracyStatusReason = highAccuracyStatusReason,
            Lyrics = lyrics
        };
    }

    public async Task<SongLyricsProcessingSnapshot?> GetSongProcessingSnapshotAsync(string songUid, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(songUid) || !SongUidPattern.IsMatch(songUid))
        {
            return null;
        }

        await using var connection = new SqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        await connection.OpenAsync(cancellationToken);

        var snapshot = await LoadSongProcessingSnapshotAsync(connection, songUid, cancellationToken);
        if (snapshot == null)
        {
            return null;
        }

        snapshot.Lyrics = await LoadLyricsAsync(connection, songUid, cancellationToken);
        return snapshot;
    }

    public async Task<List<Songs>> GetRetryableHighAccuracySongsAsync(CancellationToken cancellationToken = default, bool includeNeedsReview = true)
    {
        var songs = new List<Songs>();
        await using var connection = new SqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        await connection.OpenAsync(cancellationToken);

        var reviewStatusFilter = includeNeedsReview
            ? @",
    N'high_accuracy_partial',
    N'high_accuracy_needs_review'"
            : string.Empty;
        var query = $@"
SELECT [Title], [Artist], [YouTubeVideoUrl], [SongUid], [HighAccuracyStatus], [HighAccuracyStatusReason]
FROM [Songs]
WHERE [HighAccuracyStatus] IN (
    N'high_accuracy_pending',
    N'high_accuracy_processing'{reviewStatusFilter},
    N'high_accuracy_failed'
)
AND NOT (
    [HighAccuracyStatus] = N'high_accuracy_failed'
    AND [HighAccuracyStatusReason] LIKE N'%已停止自動補跑%'
)
ORDER BY [SongID]";
        await using var command = new SqlCommand(query, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            songs.Add(new Songs
            {
                Title = reader["Title"].ToString() ?? string.Empty,
                Artist = reader["Artist"].ToString() ?? string.Empty,
                YouTubeVideoUrl = reader["YouTubeVideoUrl"].ToString() ?? string.Empty,
                SongUid = reader["SongUid"].ToString() ?? string.Empty,
                HighAccuracyStatus = reader["HighAccuracyStatus"].ToString(),
                HighAccuracyStatusReason = reader["HighAccuracyStatusReason"].ToString()
            });
        }

        return songs;
    }

    private static async Task<bool> HasPermissionAsync(SqlConnection connection, string userEmail, string songUid, CancellationToken cancellationToken)
    {
        const string query = "SELECT [Producer], [Collaboration] FROM [Users] WHERE [Email] = @Email";
        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@Email", userEmail);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return false;
        }

        string producerList = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
        string collaborationList = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
        return producerList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(songUid, StringComparer.Ordinal)
            || collaborationList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(songUid, StringComparer.Ordinal);
    }

    private static async Task<(string SongTitle, string YoutubeVideoUrl, string? HighAccuracyStatus, string? HighAccuracyStatusReason)> LoadSongMetadataAsync(SqlConnection connection, string songUid, CancellationToken cancellationToken)
    {
        const string query = @"
SELECT [Title], [YouTubeVideoUrl],
       CASE WHEN COL_LENGTH('dbo.Songs', 'HighAccuracyStatus') IS NOT NULL THEN [HighAccuracyStatus] ELSE NULL END AS [HighAccuracyStatus],
       CASE WHEN COL_LENGTH('dbo.Songs', 'HighAccuracyStatusReason') IS NOT NULL THEN [HighAccuracyStatusReason] ELSE NULL END AS [HighAccuracyStatusReason]
FROM [Songs]
WHERE [SongUid] = @SongUid";
        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@SongUid", songUid);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return (string.Empty, string.Empty, null, null);
        }

        return (
            reader["Title"].ToString() ?? string.Empty,
            reader["YouTubeVideoUrl"].ToString() ?? string.Empty,
            reader["HighAccuracyStatus"].ToString(),
            reader["HighAccuracyStatusReason"].ToString());
    }

    private static async Task<SongLyricsProcessingSnapshot?> LoadSongProcessingSnapshotAsync(SqlConnection connection, string songUid, CancellationToken cancellationToken)
    {
        const string query = "SELECT [Title], [Artist], [YouTubeVideoUrl] FROM [Songs] WHERE [SongUid] = @SongUid";
        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@SongUid", songUid);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new SongLyricsProcessingSnapshot
        {
            SongUid = songUid,
            Title = reader["Title"].ToString() ?? string.Empty,
            Artist = reader["Artist"].ToString() ?? string.Empty,
            YouTubeUrl = reader["YouTubeVideoUrl"].ToString() ?? string.Empty
        };
    }

    private static async Task<List<LyricSegment>> LoadLyricsAsync(SqlConnection connection, string songUid, CancellationToken cancellationToken)
    {
        var lyrics = new List<LyricSegment>();
        string query = $@"
SELECT [LyricID], [TimeStamp], [Japanese], [Chinese], [Roman]
FROM [Songs_{songUid}]
ORDER BY [TimeStamp] ASC";

        await using var command = new SqlCommand(query, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lyrics.Add(new LyricSegment
            {
                LyricID = reader.GetInt32(0),
                TimeStamp = reader.GetDouble(1),
                Japanese = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                Chinese = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                Roman = reader.IsDBNull(4) ? string.Empty : reader.GetString(4)
            });
        }

        return lyrics;
    }
}
