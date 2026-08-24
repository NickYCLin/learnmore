using System.Data.SqlClient;
using LearnMore.Models;

namespace LearnMore.Services;

public class WhisperEditQueryService : IWhisperEditQueryService
{
    private readonly IConfiguration _configuration;

    public WhisperEditQueryService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task<EditSongViewModel?> GetEditSongViewModelAsync(string userEmail, string songUid, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        await connection.OpenAsync(cancellationToken);

        if (!await HasProducerPermissionAsync(connection, userEmail, songUid, cancellationToken))
        {
            return null;
        }

        var song = await LoadSongAsync(connection, songUid, cancellationToken);
        if (song is null)
        {
            return null;
        }

        var collaborators = await LoadCollaboratorsAsync(connection, songUid, cancellationToken);
        return new EditSongViewModel
        {
            Song = song,
            Collaborators = collaborators
        };
    }

    private static async Task<bool> HasProducerPermissionAsync(
        SqlConnection connection,
        string userEmail,
        string songUid,
        CancellationToken cancellationToken)
    {
        const string query = "SELECT [Producer] FROM [Users] WHERE [Email] = @Email";
        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@Email", userEmail);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        string producerList = result == null || result == DBNull.Value ? string.Empty : result.ToString() ?? string.Empty;
        return producerList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(songUid, StringComparer.Ordinal);
    }

    private static async Task<Songs?> LoadSongAsync(
        SqlConnection connection,
        string songUid,
        CancellationToken cancellationToken)
    {
        const string query = @"
SELECT [Title], [Artist],
       CASE WHEN COL_LENGTH('dbo.Songs', 'Performer') IS NOT NULL THEN [Performer] ELSE NULL END AS [Performer],
       [Translator], [YouTubeVideoUrl], [SongUid],
       CASE WHEN COL_LENGTH('dbo.Songs', 'HighAccuracyStatus') IS NOT NULL THEN [HighAccuracyStatus] ELSE NULL END AS [HighAccuracyStatus],
       CASE WHEN COL_LENGTH('dbo.Songs', 'HighAccuracyStatusReason') IS NOT NULL THEN [HighAccuracyStatusReason] ELSE NULL END AS [HighAccuracyStatusReason]
FROM [Songs]
WHERE [SongUid] = @SongUid";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@SongUid", songUid);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new Songs
        {
            Title = reader["Title"].ToString() ?? string.Empty,
            Artist = reader["Artist"].ToString() ?? string.Empty,
            Performer = reader["Performer"].ToString(),
            Translator = reader["Translator"].ToString(),
            YouTubeVideoUrl = reader["YouTubeVideoUrl"].ToString() ?? string.Empty,
            SongUid = reader["SongUid"].ToString() ?? string.Empty,
            HighAccuracyStatus = reader["HighAccuracyStatus"].ToString(),
            HighAccuracyStatusReason = reader["HighAccuracyStatusReason"].ToString()
        };
    }

    private static async Task<List<string>> LoadCollaboratorsAsync(
        SqlConnection connection,
        string songUid,
        CancellationToken cancellationToken)
    {
        const string query = @"
SELECT [Email], [Collaboration]
FROM [Users]";

        await using var command = new SqlCommand(query, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var collaborators = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            string email = reader["Email"].ToString() ?? string.Empty;
            string collaboration = reader["Collaboration"].ToString() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(collaboration))
            {
                continue;
            }

            bool isCollaborator = collaboration
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains(songUid, StringComparer.Ordinal);

            if (isCollaborator)
            {
                collaborators.Add(email);
            }
        }

        return collaborators;
    }
}
