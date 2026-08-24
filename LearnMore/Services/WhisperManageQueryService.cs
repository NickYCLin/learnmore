using System.Data.SqlClient;
using LearnMore.Models;

namespace LearnMore.Services;

public class WhisperManageQueryService : IWhisperManageQueryService
{
    private readonly IConfiguration _configuration;

    public WhisperManageQueryService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task<ManageViewModel> GetManageViewModelAsync(string userEmail, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        await connection.OpenAsync(cancellationToken);

        var (producerUids, collaborationUids) = await LoadUserSongLinksAsync(connection, userEmail, cancellationToken);

        var producerSongs = producerUids.Count == 0
            ? new List<Songs>()
            : await GetSongsByUidsAsync(connection, producerUids, cancellationToken);

        var collaborationSongs = collaborationUids.Count == 0
            ? new List<Songs>()
            : await GetSongsByUidsAsync(connection, collaborationUids, cancellationToken);

        return new ManageViewModel
        {
            ProducerSongs = producerSongs,
            CollaborationSongs = collaborationSongs
        };
    }

    private static async Task<(List<string> ProducerUids, List<string> CollaborationUids)> LoadUserSongLinksAsync(
        SqlConnection connection,
        string userEmail,
        CancellationToken cancellationToken)
    {
        const string query = "SELECT Producer, Collaboration FROM Users WHERE Email = @Email";
        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@Email", userEmail);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (new List<string>(), new List<string>());
        }

        string producerUids = reader["Producer"] as string ?? string.Empty;
        string collaborationUids = reader["Collaboration"] as string ?? string.Empty;

        return (
            producerUids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            collaborationUids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList());
    }

    private static async Task<List<Songs>> GetSongsByUidsAsync(
        SqlConnection connection,
        IReadOnlyList<string> songUids,
        CancellationToken cancellationToken)
    {
        bool hasPerformerColumn = await HasColumnAsync(connection, "Performer", cancellationToken);
        bool hasHighAccuracyStatusColumn = await HasColumnAsync(connection, "HighAccuracyStatus", cancellationToken);
        bool hasHighAccuracyStatusReasonColumn = await HasColumnAsync(connection, "HighAccuracyStatusReason", cancellationToken);
        bool hasSongAudioStemsTable = await HasTableAsync(connection, "SongAudioStems", cancellationToken);
        string performerSelect = hasPerformerColumn
            ? ", Performer"
            : ", CAST(NULL AS nvarchar(255)) AS Performer";
        string highAccuracyStatusSelect = hasHighAccuracyStatusColumn
            ? ", HighAccuracyStatus"
            : ", CAST(NULL AS nvarchar(50)) AS HighAccuracyStatus";
        string highAccuracyStatusReasonSelect = hasHighAccuracyStatusReasonColumn
            ? ", HighAccuracyStatusReason"
            : ", CAST(NULL AS nvarchar(200)) AS HighAccuracyStatusReason";
        string audioStemSelect = hasSongAudioStemsTable
            ? @",
                CAST(CASE WHEN EXISTS (
                    SELECT 1 FROM dbo.SongAudioStems stems
                    WHERE stems.SongUid = Songs.SongUid AND stems.StemKind = N'instrumental'
                ) THEN 1 ELSE 0 END AS bit) AS HasInstrumentalAudioStem,
                CAST(CASE WHEN EXISTS (
                    SELECT 1 FROM dbo.SongAudioStems stems
                    WHERE stems.SongUid = Songs.SongUid AND stems.StemKind = N'vocals'
                ) THEN 1 ELSE 0 END AS bit) AS HasVocalsAudioStem"
            : @",
                CAST(0 AS bit) AS HasInstrumentalAudioStem,
                CAST(0 AS bit) AS HasVocalsAudioStem";
        string query = $"SELECT Title, Artist{performerSelect}, YouTubeVideoUrl, SongUid, SongType{highAccuracyStatusSelect}{highAccuracyStatusReasonSelect}{audioStemSelect} FROM Songs WHERE SongUid IN ({string.Join(",", songUids.Select((_, i) => $"@uid{i}"))})";
        await using var command = new SqlCommand(query, connection);

        for (int i = 0; i < songUids.Count; i++)
        {
            command.Parameters.AddWithValue($"@uid{i}", songUids[i]);
        }

        var songs = new List<Songs>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            songs.Add(new Songs
            {
                Title = reader["Title"].ToString() ?? string.Empty,
                Artist = reader["Artist"].ToString() ?? string.Empty,
                Performer = reader["Performer"].ToString(),
                YouTubeVideoUrl = reader["YouTubeVideoUrl"].ToString() ?? string.Empty,
                SongUid = reader["SongUid"].ToString() ?? string.Empty,
                SongType = reader["SongType"].ToString(),
                HighAccuracyStatus = reader["HighAccuracyStatus"].ToString(),
                HighAccuracyStatusReason = reader["HighAccuracyStatusReason"].ToString(),
                HasInstrumentalAudioStem = reader["HasInstrumentalAudioStem"] is bool hasInstrumental && hasInstrumental,
                HasVocalsAudioStem = reader["HasVocalsAudioStem"] is bool hasVocals && hasVocals
            });
        }

        return songs;
    }

    private static async Task<bool> HasColumnAsync(SqlConnection connection, string columnName, CancellationToken cancellationToken)
    {
        const string query = "SELECT CASE WHEN COL_LENGTH('dbo.Songs', @ColumnName) IS NULL THEN 0 ELSE 1 END";
        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@ColumnName", columnName);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null && result != DBNull.Value && Convert.ToInt32(result) == 1;
    }

    private static async Task<bool> HasTableAsync(SqlConnection connection, string tableName, CancellationToken cancellationToken)
    {
        const string query = "SELECT CASE WHEN OBJECT_ID(N'dbo.' + @TableName, N'U') IS NULL THEN 0 ELSE 1 END";
        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@TableName", tableName);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null && result != DBNull.Value && Convert.ToInt32(result) == 1;
    }
}
