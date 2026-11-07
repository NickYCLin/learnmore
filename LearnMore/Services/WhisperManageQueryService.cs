using System.Data.SqlClient;
using System.Data;
using System.Xml.Linq;
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
        => await GetViewModelAsync(userEmail, null, null, cancellationToken);

    public async Task<ManageViewModel> GetPagedManageViewModelAsync(string userEmail, int producerPage,
        int collaborationPage, CancellationToken cancellationToken = default)
    {
        if (producerPage < 1) throw new ArgumentOutOfRangeException(nameof(producerPage));
        if (collaborationPage < 1) throw new ArgumentOutOfRangeException(nameof(collaborationPage));
        return await GetViewModelAsync(userEmail, producerPage, collaborationPage, cancellationToken);
    }

    private async Task<ManageViewModel> GetViewModelAsync(string userEmail, int? producerPage,
        int? collaborationPage, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        await connection.OpenAsync(cancellationToken);

        await using var managerCommand = new SqlCommand("SELECT Manager FROM Users WHERE Email = @Email", connection);
        managerCommand.Parameters.AddWithValue("@Email", userEmail);
        object? managerValue = await managerCommand.ExecuteScalarAsync(cancellationToken);
        bool isManager = managerValue != null && managerValue != DBNull.Value && Convert.ToBoolean(managerValue);

        var (producerUids, collaborationUids) = await LoadUserSongLinksAsync(connection, userEmail, cancellationToken);

        var producer = await GetSongsByUidsAsync(connection, producerUids, producerPage, cancellationToken);

        var collaboration = await GetSongsByUidsAsync(connection, collaborationUids, collaborationPage, cancellationToken);

        return new ManageViewModel
        {
            IsManager = isManager,
            ProducerSongs = producer.Songs,
            CollaborationSongs = collaboration.Songs,
            ProducerPagination = producerPage.HasValue
                ? new("producer", producer.Page, producer.TotalCount, collaboration.Page) : null,
            CollaborationPagination = collaborationPage.HasValue
                ? new("collaboration", collaboration.Page, collaboration.TotalCount, producer.Page) : null
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

    private static async Task<(List<Songs> Songs, int Page, long TotalCount)> GetSongsByUidsAsync(
        SqlConnection connection,
        IReadOnlyList<string> songUids,
        int? requestedPage,
        CancellationToken cancellationToken)
    {
        if (songUids.Count == 0) return ([], 1, 0);
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
        const string songFilter = "FROM dbo.Songs WHERE SongUid IN (SELECT item.value('.', 'nvarchar(450)') FROM @SongUids.nodes('/songs/uid') AS links(item))";
        await using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        await using var command = new SqlCommand("SELECT COUNT_BIG(*) " + songFilter, connection, transaction);
        command.Parameters.Add("@SongUids", SqlDbType.Xml).Value = new XElement("songs",
            songUids.Distinct(StringComparer.Ordinal).Select(uid => new XElement("uid", uid))).ToString(SaveOptions.DisableFormatting);
        var totalCount = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        var page = (int)Math.Min(requestedPage ?? 1, Math.Max(1, (totalCount + ManageSongPagination.PageSize - 1) / ManageSongPagination.PageSize));
        command.CommandText = $"SELECT Title, Artist{performerSelect}, YouTubeVideoUrl, SongUid, SongType{highAccuracyStatusSelect}{highAccuracyStatusReasonSelect}{audioStemSelect} "
            + songFilter + " ORDER BY SongID DESC, SongUid";
        if (requestedPage.HasValue)
        {
            command.CommandText += " OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";
            command.Parameters.Add("@Offset", SqlDbType.BigInt).Value = ((long)page - 1) * ManageSongPagination.PageSize;
            command.Parameters.Add("@PageSize", SqlDbType.Int).Value = ManageSongPagination.PageSize;
        }

        var songs = new List<Songs>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
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
        }

        await transaction.CommitAsync(cancellationToken);
        return (songs, page, totalCount);
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
