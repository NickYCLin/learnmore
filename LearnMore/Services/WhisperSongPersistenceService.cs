using System.Data.SqlClient;
using System.Text.RegularExpressions;
using LearnMore.Models;

namespace LearnMore.Services;

public class WhisperSongPersistenceService : IWhisperSongPersistenceService
{
    private static readonly Regex SongUidPattern = new("^[A-Za-z0-9_-]+$", RegexOptions.Compiled);
    private readonly IConfiguration _configuration;
    private readonly WhisperTranscriptionPersistenceService _transcriptionPersistence;
    private readonly IAudioStemJobService _audioStemJobService;
    private readonly JapaneseRubyGeneratorService _rubyGenerator;
    private readonly ILogger<WhisperSongPersistenceService> _logger;

    public WhisperSongPersistenceService(
        IConfiguration configuration,
        WhisperTranscriptionPersistenceService transcriptionPersistence,
        IAudioStemJobService audioStemJobService,
        JapaneseRubyGeneratorService rubyGenerator,
        ILogger<WhisperSongPersistenceService> logger)
    {
        _configuration = configuration;
        _transcriptionPersistence = transcriptionPersistence;
        _audioStemJobService = audioStemJobService;
        _rubyGenerator = rubyGenerator;
        _logger = logger;
    }

    public async Task<string> AddSongToDatabaseAsync(TranscribeRequest request, string userEmail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userEmail);
        var songUid = Guid.NewGuid().ToString();
        var query = @"
        INSERT INTO [Songs] ([SongUid], [Title], [Artist], [Performer], [YouTubeVideoUrl], [AddedByUserId])
        OUTPUT INSERTED.[SongUid]
        VALUES (@SongUid, @Title, @Artist, @Performer, @YouTubeVideoUrl, @AddedByUserId)";

        using var connection = new SqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        await connection.OpenAsync();
        await EnsurePerformerColumnExistsAsync(connection);
        using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
        var uploaderId = await RecordUploaderAsync(connection, transaction, userEmail, songUid, CancellationToken.None);
        await ThrowIfDuplicateYouTubeSongAsync(connection, transaction, request.YouTubeUrl, CancellationToken.None);

        using var command = new SqlCommand(query, connection, transaction);
        command.Parameters.AddWithValue("@AddedByUserId", uploaderId);
        command.Parameters.AddWithValue("@SongUid", songUid);
        command.Parameters.AddWithValue("@Title", request.Title);
        command.Parameters.AddWithValue("@Artist", request.Artist);
        var performer = ResolvePerformer(request.Performer, request.Cover, request.Artist);
        command.Parameters.AddWithValue("@Performer", string.IsNullOrWhiteSpace(performer) ? (object)DBNull.Value : performer);
        command.Parameters.AddWithValue("@YouTubeVideoUrl", request.YouTubeUrl);

        var insertedSongUid = await command.ExecuteScalarAsync();
        if (insertedSongUid is string persistedSongUid)
        {
            await InsertSearchAliasesAsync(connection, transaction, persistedSongUid, request.Title, request.ChineseTitleAlias);
            await transaction.CommitAsync();
            await EnqueueAudioStemJobSafeAsync(persistedSongUid, request.YouTubeUrl, CancellationToken.None);
            return persistedSongUid;
        }

        throw new Exception("Failed to retrieve SongUid after insertion.");
    }

    public async Task CreateDynamicSongTableAsync(string songUid)
    {
        var tableName = BuildSongTableName(songUid);
        var query = $@"
        CREATE TABLE [dbo].[{tableName}] (
            [LyricID] INT IDENTITY(1,1) PRIMARY KEY,
            [TimeStamp] FLOAT NOT NULL,
            [Japanese] NVARCHAR(MAX),
            [Chinese] NVARCHAR(MAX),
            [JapaneseRuby] NVARCHAR(MAX),
            [Roman] NVARCHAR(MAX)
        )";

        using var connection = new SqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        await connection.OpenAsync();

        using var command = new SqlCommand(query, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task InsertTranscriptionToDynamicTableAsync(string songUid, string transcriptionJson)
    {
        var tableName = BuildSongTableName(songUid);
        var query = $@"
INSERT INTO [{tableName}] ([TimeStamp], [Japanese], [Chinese], [JapaneseRuby])
VALUES (@TimeStamp, @Japanese, @Chinese, @JapaneseRuby)";

        using var connection = new SqlConnection(_configuration.GetConnectionString("DefaultConnection"));
        await connection.OpenAsync();

        var segments = await _transcriptionPersistence.ParseTranscriptionToSegmentsAsync(transcriptionJson);
        foreach (var segment in segments)
        {
            using var command = new SqlCommand(query, connection);
            command.Parameters.AddWithValue("@TimeStamp", segment.TimeStamp);
            command.Parameters.AddWithValue("@Japanese", segment.Japanese ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@Chinese", segment.Chinese ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@JapaneseRuby", segment.JapaneseRuby ?? (object)DBNull.Value);
            await command.ExecuteNonQueryAsync();
        }
    }

    public async Task InsertManualSegmentsAsync(string songUid, IReadOnlyCollection<TranscriptionSegment> segments, CancellationToken cancellationToken = default)
    {
        string tableName = BuildSongTableName(songUid);
        var connectionString = _configuration.GetConnectionString("DefaultConnection");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsurePerformerColumnExistsAsync(connection, cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            string insertQuery = $@"
INSERT INTO [dbo].[{tableName}] (TimeStamp, Japanese, Chinese)
VALUES (@Start, @Text, @Chinese)";

            foreach (var segment in segments)
            {
                await using var command = new SqlCommand(insertQuery, connection, transaction);
                command.Parameters.AddWithValue("@Start", segment.Start);
                command.Parameters.AddWithValue("@Text", string.IsNullOrWhiteSpace(segment.Text) ? (object)DBNull.Value : segment.Text);
                command.Parameters.AddWithValue("@Chinese", string.IsNullOrWhiteSpace(segment.Chinese) ? (object)DBNull.Value : segment.Chinese);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<string> CreateSummonedSongAsync(SummonRequest request, IReadOnlyCollection<LyricEntry> lyrics, string userEmail, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userEmail);
        var songUid = Guid.NewGuid().ToString();
        string tableName = BuildSongTableName(songUid);
        var connectionString = _configuration.GetConnectionString("DefaultConnection");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsurePerformerColumnExistsAsync(connection, cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);

        try
        {
            var uploaderId = await RecordUploaderAsync(connection, transaction, userEmail, songUid, cancellationToken);
            await ThrowIfDuplicateYouTubeSongAsync(connection, transaction, request.YouTubeLink, cancellationToken);

            const string insertSongQuery = @"
INSERT INTO [dbo].[Songs] (SongUid, Title, Artist, Performer, Translator, YouTubeVideoUrl, AddedByUserId)
VALUES (@SongUid, @Title, @Artist, @Performer, @Translator, @YouTubeVideoUrl, @AddedByUserId)";

            await using (var command = new SqlCommand(insertSongQuery, connection, transaction))
            {
                command.Parameters.AddWithValue("@AddedByUserId", uploaderId);
                command.Parameters.AddWithValue("@SongUid", songUid);
                command.Parameters.AddWithValue("@Title", request.SongTitle);
                command.Parameters.AddWithValue("@Artist", request.SongArtist);
                var performer = ResolvePerformer(request.SongPerformer, request.SongCover, request.SongArtist);
                command.Parameters.AddWithValue("@Performer", string.IsNullOrWhiteSpace(performer) ? (object)DBNull.Value : performer);
                command.Parameters.AddWithValue("@Translator", string.IsNullOrWhiteSpace(request.SongTranslator) ? (object)DBNull.Value : request.SongTranslator);
                command.Parameters.AddWithValue("@YouTubeVideoUrl", request.YouTubeLink);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertSearchAliasesAsync(connection, transaction, songUid, request.SongTitle, request.ChineseTitleAlias, cancellationToken);

            string createTableQuery = $@"
CREATE TABLE [dbo].[{tableName}] (
    LyricID INT IDENTITY(1,1) PRIMARY KEY,
    TimeStamp FLOAT NOT NULL,
    Japanese NVARCHAR(MAX),
    Chinese NVARCHAR(MAX),
    JapaneseRuby NVARCHAR(MAX),
    Roman NVARCHAR(MAX)
)";

            await using (var command = new SqlCommand(createTableQuery, connection, transaction))
            {
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            string insertLyricsQuery = $@"
INSERT INTO [dbo].[{tableName}] (TimeStamp, Japanese, Chinese, JapaneseRuby, Roman)
VALUES (@TimeStamp, @Japanese, @Chinese, @JapaneseRuby, @Roman)";

            foreach (var lyric in lyrics)
            {
                await using var command = new SqlCommand(insertLyricsQuery, connection, transaction);
                command.Parameters.AddWithValue("@TimeStamp", lyric.Time);
                command.Parameters.AddWithValue("@Japanese", string.IsNullOrWhiteSpace(lyric.Japanese) ? (object)DBNull.Value : lyric.Japanese);
                command.Parameters.AddWithValue("@Chinese", string.IsNullOrWhiteSpace(lyric.Chinese) ? (object)DBNull.Value : lyric.Chinese);
                command.Parameters.AddWithValue("@JapaneseRuby", string.IsNullOrWhiteSpace(lyric.JapaneseRuby) ? (object)DBNull.Value : lyric.JapaneseRuby);
                command.Parameters.AddWithValue("@Roman", string.IsNullOrWhiteSpace(lyric.Roman) ? (object)DBNull.Value : lyric.Roman);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            await EnqueueAudioStemJobSafeAsync(songUid, request.YouTubeLink, CancellationToken.None);
            return songUid;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<SongPlaceholderCreationResult> CreateSongWithPlaceholdersAsync(TranscribeRequest request, IReadOnlyCollection<LyricSegment> segments, string userEmail, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userEmail);
        if (segments.Count == 0 || segments.Any(segment => string.IsNullOrWhiteSpace(segment.Japanese)))
            throw new ArgumentException("自動建檔需要完整的日文歌詞。", nameof(segments));

        var songUid = Guid.NewGuid().ToString();
        var lyricIds = new List<int>();
        var connectionString = _configuration.GetConnectionString("DefaultConnection");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsurePerformerColumnExistsAsync(connection, cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);

        try
        {
            var uploaderId = await RecordUploaderAsync(connection, transaction, userEmail, songUid, cancellationToken);
            await ThrowIfDuplicateYouTubeSongAsync(connection, transaction, request.YouTubeUrl, cancellationToken);

            const string insertSongQuery = @"
INSERT INTO [dbo].[Songs]
    ([SongUid], [Title], [Artist], [Performer], [Translator], [TranslationSource], [YouTubeVideoUrl], [ChannelThumbnailUrl], [SongType], [AddedDate], [AddedByUserId])
VALUES
    (@SongUid, @Title, @Artist, @Performer, NULL, NULL, @YouTubeVideoUrl, NULL, NULL, GETDATE(), @AddedByUserId)";

            await using (var insertSongCommand = new SqlCommand(insertSongQuery, connection, transaction))
            {
                insertSongCommand.Parameters.AddWithValue("@AddedByUserId", uploaderId);
                insertSongCommand.Parameters.AddWithValue("@SongUid", songUid);
                insertSongCommand.Parameters.AddWithValue("@Title", string.IsNullOrWhiteSpace(request.Title) ? (object)DBNull.Value : request.Title);
                insertSongCommand.Parameters.AddWithValue("@Artist", string.IsNullOrWhiteSpace(request.Artist) ? (object)DBNull.Value : request.Artist);
                var performer = ResolvePerformer(request.Performer, request.Cover, request.Artist);
                insertSongCommand.Parameters.AddWithValue("@Performer", string.IsNullOrWhiteSpace(performer) ? (object)DBNull.Value : performer);
                insertSongCommand.Parameters.AddWithValue("@YouTubeVideoUrl", string.IsNullOrWhiteSpace(request.YouTubeUrl) ? (object)DBNull.Value : request.YouTubeUrl);
                await insertSongCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertSearchAliasesAsync(connection, transaction, songUid, request.Title, request.ChineseTitleAlias, cancellationToken);

            string tableName = BuildSongTableName(songUid);
            string createTableQuery = $@"
        CREATE TABLE [dbo].[{tableName}] (
            [LyricID] INT IDENTITY(1,1) PRIMARY KEY,
            [TimeStamp] FLOAT NOT NULL,
            [Japanese] NVARCHAR(MAX),
            [Chinese] NVARCHAR(MAX),
            [JapaneseRuby] NVARCHAR(MAX),
            [Roman] NVARCHAR(MAX)
        )";

            await using (var createTableCommand = new SqlCommand(createTableQuery, connection, transaction))
            {
                await createTableCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var segment in segments)
            {
                string insertLyricQuery = $@"
INSERT INTO [dbo].[{tableName}] (TimeStamp, Japanese, Chinese)
OUTPUT INSERTED.LyricID
VALUES (@Start, @Text, @Chinese)";
                await using var insertLyricCommand = new SqlCommand(insertLyricQuery, connection, transaction);
                insertLyricCommand.Parameters.AddWithValue("@Start", segment.TimeStamp);
                insertLyricCommand.Parameters.AddWithValue("@Text", string.IsNullOrWhiteSpace(segment.Japanese) ? (object)DBNull.Value : segment.Japanese);
                var chinese = !string.IsNullOrWhiteSpace(segment.Chinese) ? segment.Chinese : "翻譯中...";
                insertLyricCommand.Parameters.AddWithValue("@Chinese", chinese);
                var lyricId = Convert.ToInt32(await insertLyricCommand.ExecuteScalarAsync(cancellationToken) ?? 0);
                lyricIds.Add(lyricId);
            }

            await transaction.CommitAsync(cancellationToken);
            await EnqueueAudioStemJobSafeAsync(songUid, request.YouTubeUrl, CancellationToken.None);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        return new SongPlaceholderCreationResult
        {
            SongUid = songUid,
            LyricIds = lyricIds
        };
    }

    public async Task<List<int>> UpdateSongTranslationsAsync(string songUid, IReadOnlyList<LyricSegment> finalSegments, IReadOnlyList<int> existingLyricIds, CancellationToken cancellationToken = default)
    {
        string tableName = BuildSongTableName(songUid);
        var lyricIds = existingLyricIds.ToList();
        var connectionString = _configuration.GetConnectionString("DefaultConnection");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            if (finalSegments.Count != lyricIds.Count)
            {
                string deleteQuery = $"DELETE FROM [dbo].[{tableName}]";
                await using (var deleteCommand = new SqlCommand(deleteQuery, connection, transaction))
                {
                    await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
                }

                lyricIds.Clear();
                foreach (var segment in finalSegments)
                {
                    string insertQuery = $@"
INSERT INTO [dbo].[{tableName}] (TimeStamp, Japanese, Chinese)
VALUES (@Start, @Text, @Chinese);
SELECT CAST(SCOPE_IDENTITY() AS INT);";
                    await using var insertCommand = new SqlCommand(insertQuery, connection, transaction);
                    insertCommand.Parameters.AddWithValue("@Start", segment.TimeStamp);
                    insertCommand.Parameters.AddWithValue("@Text", string.IsNullOrWhiteSpace(segment.Japanese) ? (object)DBNull.Value : segment.Japanese);
                    insertCommand.Parameters.AddWithValue("@Chinese", string.IsNullOrWhiteSpace(segment.Chinese) ? (object)DBNull.Value : segment.Chinese);
                    var newId = await insertCommand.ExecuteScalarAsync(cancellationToken);
                    lyricIds.Add(Convert.ToInt32(newId));
                }
            }
            else
            {
                for (int i = 0; i < finalSegments.Count; i++)
                {
                    string updateQuery = $"UPDATE [dbo].[{tableName}] SET Chinese = @Chinese, Japanese = @Japanese, TimeStamp = @TimeStamp WHERE LyricID = @LyricID";
                    await using var updateCommand = new SqlCommand(updateQuery, connection, transaction);
                    updateCommand.Parameters.AddWithValue("@Chinese", string.IsNullOrWhiteSpace(finalSegments[i].Chinese) ? (object)DBNull.Value : finalSegments[i].Chinese);
                    updateCommand.Parameters.AddWithValue("@Japanese", string.IsNullOrWhiteSpace(finalSegments[i].Japanese) ? (object)DBNull.Value : finalSegments[i].Japanese);
                    updateCommand.Parameters.AddWithValue("@TimeStamp", finalSegments[i].TimeStamp);
                    updateCommand.Parameters.AddWithValue("@LyricID", lyricIds[i]);
                    await updateCommand.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            await transaction.CommitAsync(cancellationToken);
            return lyricIds;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task UpdateHighAccuracyStatusAsync(string songUid, string? highAccuracyStatus, string? highAccuracyStatusReason = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(songUid) || !SongUidPattern.IsMatch(songUid))
        {
            throw new ArgumentException("Invalid songUid.", nameof(songUid));
        }

        var connectionString = _configuration.GetConnectionString("DefaultConnection");
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        const string query = @"
IF COL_LENGTH('dbo.Songs', 'HighAccuracyStatus') IS NOT NULL
BEGIN
    UPDATE [dbo].[Songs]
    SET [HighAccuracyStatus] = @HighAccuracyStatus,
        [HighAccuracyStatusReason] = CASE
            WHEN COL_LENGTH('dbo.Songs', 'HighAccuracyStatusReason') IS NOT NULL THEN @HighAccuracyStatusReason
            ELSE [HighAccuracyStatusReason]
        END
    WHERE [SongUid] = @SongUid
END";
        await using var command = new SqlCommand(query, connection);
        command.Parameters.AddWithValue("@SongUid", songUid);
        command.Parameters.AddWithValue("@HighAccuracyStatus", string.IsNullOrWhiteSpace(highAccuracyStatus) ? (object)DBNull.Value : highAccuracyStatus);
        command.Parameters.AddWithValue("@HighAccuracyStatusReason", string.IsNullOrWhiteSpace(highAccuracyStatusReason) ? (object)DBNull.Value : highAccuracyStatusReason);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> RecordUploaderAsync(SqlConnection connection, SqlTransaction transaction, string userEmail, string songUid, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userEmail);
        int userId;
        await using (var command = new SqlCommand("SELECT Id FROM dbo.Users WITH (UPDLOCK, HOLDLOCK) WHERE Email = @Email", connection, transaction))
        {
            command.Parameters.AddWithValue("@Email", userEmail);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("找不到上傳者帳號，無法新增歌曲。");
            userId = reader.GetInt32(0);
            if (await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("上傳者帳號不唯一，無法新增歌曲。");
        }
        await using var update = new SqlCommand(@"
UPDATE dbo.Users SET Producer = CASE
    WHEN NULLIF(LTRIM(RTRIM(Producer)), '') IS NULL THEN @SongUid
    ELSE Producer + ',' + @SongUid END
WHERE Id = @UserId", connection, transaction);
        update.Parameters.AddWithValue("@SongUid", songUid);
        update.Parameters.AddWithValue("@UserId", userId);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("無法記錄上傳者，歌曲未新增。");
        return userId;
    }

    private async Task EnqueueAudioStemJobSafeAsync(string songUid, string? youTubeVideoUrl, CancellationToken cancellationToken)
    {
        try
        {
            await _audioStemJobService.EnqueueSongAsync(songUid, youTubeVideoUrl, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "伴奏/人聲背景隊列入隊失敗 songUid={SongUid}", songUid);
        }
    }

    private static async Task EnsurePerformerColumnExistsAsync(SqlConnection connection, CancellationToken cancellationToken = default)
    {
        const string sql = @"
IF COL_LENGTH('dbo.Songs', 'Performer') IS NULL
BEGIN
    ALTER TABLE [dbo].[Songs] ADD [Performer] NVARCHAR(255) NULL;
END";
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureSongAliasesTableAsync(SqlConnection connection, SqlTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        const string sql = @"
IF OBJECT_ID('dbo.SongAliases', 'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[SongAliases] (
        [AliasID] INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        [SongUid] NVARCHAR(50) NOT NULL,
        [AliasText] NVARCHAR(255) NOT NULL,
        [AliasType] NVARCHAR(50) NOT NULL,
        [Source] NVARCHAR(100) NULL,
        [CreatedAt] DATETIME NOT NULL CONSTRAINT [DF_SongAliases_CreatedAt] DEFAULT GETDATE()
    );
END;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SongAliases_AliasText' AND object_id = OBJECT_ID('dbo.SongAliases'))
BEGIN
    CREATE INDEX [IX_SongAliases_AliasText] ON [dbo].[SongAliases] ([AliasText]) INCLUDE ([SongUid], [AliasType]);
END;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SongAliases_SongUid' AND object_id = OBJECT_ID('dbo.SongAliases'))
BEGIN
    CREATE INDEX [IX_SongAliases_SongUid] ON [dbo].[SongAliases] ([SongUid]);
END;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_SongAliases_SongUid_AliasText' AND object_id = OBJECT_ID('dbo.SongAliases'))
BEGIN
    ;WITH DuplicateAliases AS (
        SELECT [AliasID],
               ROW_NUMBER() OVER (PARTITION BY [SongUid], [AliasText] ORDER BY [AliasID]) AS [RowNumber]
        FROM [dbo].[SongAliases]
    )
    DELETE FROM DuplicateAliases WHERE [RowNumber] > 1;

    CREATE UNIQUE INDEX [UX_SongAliases_SongUid_AliasText]
    ON [dbo].[SongAliases] ([SongUid], [AliasText])
    WITH (IGNORE_DUP_KEY = ON);
END;";

        await using var command = new SqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task InsertSearchAliasesAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string songUid,
        string? title,
        string? chineseTitleAlias,
        CancellationToken cancellationToken = default)
    {
        await InsertSongAliasIfPresentAsync(
            connection,
            transaction,
            songUid,
            string.IsNullOrWhiteSpace(chineseTitleAlias) ? BuildFallbackCjkSearchAlias(title) : chineseTitleAlias,
            "traditional_chinese_title",
            "auto_title_translation",
            cancellationToken);

        string? romanizedTitle = BuildRomanizedTitleAlias(title);
        await InsertSongAliasIfPresentAsync(
            connection,
            transaction,
            songUid,
            romanizedTitle,
            "romanized_title",
            "auto_title_romanization",
            cancellationToken);
    }

    private string? BuildRomanizedTitleAlias(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        try
        {
            return NormalizeAliasText(JapaneseRomanSanitizer.NormalizeWithContext(title, string.Empty, _rubyGenerator));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Romanized title alias generation failed for {Title}", title);
            return null;
        }
    }

    private static string? BuildFallbackCjkSearchAlias(string? title)
    {
        var normalizedTitle = NormalizeAliasText(title);
        return string.IsNullOrWhiteSpace(normalizedTitle) ? null : NormalizeAliasText(normalizedTitle + " 中文歌詞");
    }

    private static async Task InsertSongAliasIfPresentAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string songUid,
        string? aliasText,
        string aliasType,
        string source,
        CancellationToken cancellationToken = default)
    {
        var normalizedAlias = NormalizeAliasText(aliasText);
        if (string.IsNullOrWhiteSpace(normalizedAlias))
        {
            return;
        }

        await EnsureSongAliasesTableAsync(connection, transaction, cancellationToken);
        const string sql = @"
IF NOT EXISTS (
    SELECT 1 FROM [dbo].[SongAliases]
    WHERE [SongUid] = @SongUid AND [AliasText] = @AliasText
)
BEGIN
    INSERT INTO [dbo].[SongAliases] ([SongUid], [AliasText], [AliasType], [Source])
    VALUES (@SongUid, @AliasText, @AliasType, @Source)
END";

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@SongUid", songUid);
        command.Parameters.AddWithValue("@AliasText", normalizedAlias);
        command.Parameters.AddWithValue("@AliasType", aliasType);
        command.Parameters.AddWithValue("@Source", source);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string? NormalizeAliasText(string? aliasText)
    {
        if (string.IsNullOrWhiteSpace(aliasText))
        {
            return null;
        }

        var normalized = aliasText.Trim();
        return normalized.Length is > 0 and <= 255 ? normalized : null;
    }

    private static string? ResolvePerformer(string? performer, string? cover, string? artist)
    {
        if (!string.IsNullOrWhiteSpace(performer))
        {
            return performer.Trim();
        }

        if (!string.IsNullOrWhiteSpace(cover))
        {
            return cover.Trim();
        }

        return string.IsNullOrWhiteSpace(artist) ? null : artist.Trim();
    }

    private static string? NormalizeNameForComparison(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : Regex.Replace(value.Trim(), @"\s+", string.Empty);
    }

    private static async Task ThrowIfDuplicateYouTubeSongAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string? youTubeUrl,
        CancellationToken cancellationToken)
    {
        string? requestedVideoId = YouTubeVideoIdExtractor.Extract(youTubeUrl);
        if (string.IsNullOrWhiteSpace(requestedVideoId))
        {
            return;
        }

        const string query = @"
SELECT SongUid, YouTubeVideoUrl
FROM [dbo].[Songs] WITH (UPDLOCK, HOLDLOCK)
WHERE YouTubeVideoUrl IS NOT NULL AND LTRIM(RTRIM(YouTubeVideoUrl)) <> ''";

        await using var command = new SqlCommand(query, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            string existingSongUid = reader.GetString(0);
            string storedUrl = reader.GetString(1);
            if (string.Equals(YouTubeVideoIdExtractor.Extract(storedUrl), requestedVideoId, StringComparison.Ordinal))
            {
                throw new DuplicateYouTubeSongException(existingSongUid, requestedVideoId);
            }
        }
    }

    private static string BuildSongTableName(string songUid)
    {
        if (string.IsNullOrWhiteSpace(songUid) || !SongUidPattern.IsMatch(songUid))
        {
            throw new ArgumentException("Invalid songUid.", nameof(songUid));
        }

        return $"Songs_{songUid}";
    }
}
