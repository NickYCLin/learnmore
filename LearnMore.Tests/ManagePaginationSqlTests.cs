using System.Data.SqlClient;
using LearnMore.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LearnMore.Tests;

public sealed class ManagePaginationSqlTests : IAsyncLifetime
{
    private readonly string database = "LearnMorePageAudit_" + Guid.NewGuid().ToString("N");
    private string? master;
    private string? connection;
    private WhisperManageQueryService Service => new(new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = connection }).Build());

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable("LEARNMORE_SQL_AUDIT");
        if (string.IsNullOrEmpty(configured)) return;
        var builder = new SqlConnectionStringBuilder(configured);
        if (!builder.DataSource.StartsWith("127.0.0.1,", StringComparison.Ordinal) || builder.InitialCatalog != "master")
            throw new InvalidOperationException("Only an explicitly configured loopback master SQL audit is allowed.");
        master = builder.ConnectionString;
        await Execute(master, $"CREATE DATABASE [{database}]");
        builder.InitialCatalog = database;
        connection = builder.ConnectionString;
        await Execute(connection, """
            CREATE TABLE dbo.Users(Email nvarchar(255),Producer nvarchar(max),Collaboration nvarchar(max),Manager bit NULL,IsDisabled bit DEFAULT 0,DeletedAt datetime2 NULL);
            CREATE TABLE dbo.Songs(SongID int PRIMARY KEY,SongUid nvarchar(100) UNIQUE,
                Title nvarchar(255),Artist nvarchar(255),YouTubeVideoUrl nvarchar(255),SongType nvarchar(50));
            DECLARE @number int=1;
            WHILE @number<=80 BEGIN
                INSERT dbo.Songs VALUES(@number,CONCAT('song',@number),CONCAT('title',@number),'artist','url','type');
                SET @number+=1;
            END;
            INSERT dbo.Users(Email,Producer,Collaboration,Manager) VALUES('owner',
                (SELECT STRING_AGG(SongUid,',') FROM dbo.Songs WHERE SongID<=45)+',song1,missing',
                (SELECT STRING_AGG(SongUid,',') FROM dbo.Songs WHERE SongID>45),0);
            """);
    }

    public async Task DisposeAsync()
    {
        if (master == null) return;
        SqlConnection.ClearAllPools();
        await Execute(master, $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
    }

    [SqlAuditFact]
    public async Task ManagementLinksFollowCurrentDatabasePermission()
    {
        Assert.False((await Service.GetPagedManageViewModelAsync("owner", 1, 1)).IsManager);
        await Execute(connection!, "UPDATE dbo.Users SET Manager=1 WHERE Email='owner';");
        Assert.True((await Service.GetPagedManageViewModelAsync("owner", 1, 1)).IsManager);
        await Execute(connection!, "UPDATE dbo.Users SET Manager=NULL WHERE Email='owner';");
        Assert.False((await Service.GetPagedManageViewModelAsync("owner", 1, 1)).IsManager);
        Assert.False((await Service.GetPagedManageViewModelAsync("unknown", 1, 1)).IsManager);
    }

    [SqlAuditFact]
    public async Task PagesAreIndependentStableAndDoNotCountMissingOrDuplicateLinks()
    {
        var first = await Service.GetPagedManageViewModelAsync("owner", 1, 1);
        var second = await Service.GetPagedManageViewModelAsync("owner", 2, 1);
        Assert.Equal(20, first.ProducerSongs.Count);
        Assert.Equal("song45", first.ProducerSongs[0].SongUid);
        Assert.Equal("song25", second.ProducerSongs[0].SongUid);
        Assert.Empty(first.ProducerSongs.Select(song => song.SongUid).Intersect(second.ProducerSongs.Select(song => song.SongUid)));
        Assert.Equal(first.CollaborationSongs.Select(song => song.SongUid), second.CollaborationSongs.Select(song => song.SongUid));
        Assert.Equal(45L, second.ProducerPagination!.TotalCount);
        Assert.Equal(35L, second.CollaborationPagination!.TotalCount);
        Assert.Equal(2, second.CollaborationPagination.OtherPage);
        Assert.Equal(1, second.ProducerPagination.OtherPage);
        var last = await Service.GetPagedManageViewModelAsync("owner", int.MaxValue, int.MaxValue);
        Assert.Equal(3, last.ProducerPagination!.Page);
        Assert.Equal(5, last.ProducerSongs.Count);
        Assert.Equal(2, last.CollaborationPagination!.Page);
        Assert.Equal(15, last.CollaborationSongs.Count);
        var full = await Service.GetManageViewModelAsync("owner");
        Assert.Equal(45, full.ProducerSongs.Count);
        Assert.Equal(35, full.CollaborationSongs.Count);
        Assert.Null(full.ProducerPagination);
        Assert.Null(full.CollaborationPagination);
        Assert.False(first.ProducerSongs[0].HasVocalsAudioStem);
    }

    [SqlAuditFact]
    public async Task UnknownUserIsEmptyAndInvalidPagesAreRejected()
    {
        var result = await Service.GetPagedManageViewModelAsync("stranger", 3, 4);
        Assert.Empty(result.ProducerSongs);
        Assert.Empty(result.CollaborationSongs);
        Assert.Equal(0L, result.ProducerPagination!.TotalCount);
        Assert.Equal(1, result.ProducerPagination.Page);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Service.GetPagedManageViewModelAsync("owner", 0, 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Service.GetPagedManageViewModelAsync("owner", 1, -1));
    }

    [SqlAuditFact]
    public async Task LargeLinkCollectionsAndXmlCharactersWorkWithOptionalEvidenceColumns()
    {
        await Execute(connection!, """
            ALTER TABLE dbo.Songs ADD Performer nvarchar(255),HighAccuracyStatus nvarchar(50),HighAccuracyStatusReason nvarchar(200);
            CREATE TABLE dbo.SongAudioStems(SongUid nvarchar(100),StemKind nvarchar(50));
            INSERT dbo.SongAudioStems VALUES('song45','vocals');
            """);
        await Execute(connection!, "UPDATE dbo.Songs SET Performer='performer',HighAccuracyStatus='high_accuracy_needs_review',HighAccuracyStatusReason='reason';");
        await using var sql = new SqlConnection(connection);
        await sql.OpenAsync();
        await using var command = new SqlCommand("UPDATE dbo.Users SET Producer=@links WHERE Email='owner'", sql);
        command.Parameters.AddWithValue("@links", string.Join(',', Enumerable.Range(1, 2200).Select(number => "song" + number)) + ",<&>\"'");
        await command.ExecuteNonQueryAsync();
        var result = await Service.GetPagedManageViewModelAsync("owner", 1, 1);
        Assert.Equal(80L, result.ProducerPagination!.TotalCount);
        Assert.Equal(20, result.ProducerSongs.Count);
        Assert.Equal("performer", result.ProducerSongs[0].Performer);
        Assert.Equal("high_accuracy_needs_review", result.ProducerSongs[0].HighAccuracyStatus);
        Assert.Equal("reason", result.ProducerSongs[0].HighAccuracyStatusReason);
        var last = await Service.GetPagedManageViewModelAsync("owner", 2, 1);
        Assert.True(last.ProducerSongs.Single(song => song.SongUid == "song45").HasVocalsAudioStem);
    }

    private static async Task Execute(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
