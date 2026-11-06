using System.Data.SqlClient;
using LearnMore.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LearnMore.Tests;

public sealed class ManagementWorkSqlTests : IAsyncLifetime
{
    private readonly string database = "LearnMoreWorkAudit_" + Guid.NewGuid().ToString("N");
    private string? master;
    private string? connection;
    private IConfiguration Configuration => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = connection }).Build();

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
            CREATE TABLE dbo.Songs (SongID int PRIMARY KEY, SongUid nvarchar(100) UNIQUE,
                Title nvarchar(255), YouTubeVideoUrl nvarchar(255),HighAccuracyStatus nvarchar(50),HighAccuracyStatusReason nvarchar(max));
            CREATE TABLE dbo.ErrorReports (ReportId int PRIMARY KEY, UserEmail nvarchar(255),ErrorDescription nvarchar(max),
                ErrorSongUid nvarchar(100),ErrorLyricID int,ReportDate datetime2,Status int);
            INSERT dbo.Songs VALUES(1,'song','title','url','high_accuracy_needs_review','reason');
            INSERT dbo.ErrorReports VALUES(1,'user','description','song',7,'2026-11-07',0);
            """);
        await Execute(connection, await File.ReadAllTextAsync(Path.Combine(SourceRoot(), "docs/sql/007-management-work-events.sql")));
    }

    public async Task DisposeAsync()
    {
        if (master == null) return;
        SqlConnection.ClearAllPools();
        await Execute(master, $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
    }

    [SqlAuditFact]
    public async Task SavesAuditAndReopensWithoutChangingOriginalReport()
    {
        var store = new ManagementWorkStore(Configuration);
        var initial = (await store.ReadAsync("report", "1", default))!;
        var page = await new ErrorReportReader(Configuration).ReadAsync(1, 25, default);
        Assert.Equal(ManagementWork.Evidence(Assert.Single(page.Items), page.ReportIdColumn), initial.Target.EvidenceHash);
        await store.SaveAsync("report", "1", 0, initial.Target.EvidenceHash, "resolved", "已修正並實播核對", "one@example.invalid", default);
        var saved = (await new ManagementWorkStore(Configuration).ReadAsync("report", "1", default))!;
        Assert.Equal("resolved", saved.Current!.Status);
        Assert.Equal("one@example.invalid", saved.Current.ActorEmail);
        Assert.Equal(DateTimeKind.Utc, saved.Current.UpdatedAtUtc.Kind);
        Assert.Equal("0", Assert.Single((await new ErrorReportReader(Configuration).ReadAsync(1, 25, default)).Items).Status);
        await store.SaveAsync("report", "1", saved.Latest!.EventId, initial.Target.EvidenceHash, "pending", "重新檢查", "two@example.invalid", default);
        var reopened = (await store.ReadAsync("report", "1", default))!;
        Assert.Equal(2, reopened.History.Count);
        Assert.Equal("pending", reopened.Current!.Status);
        Assert.Equal("resolved", reopened.History[1].Status);
        Assert.Single(await store.ReadLatestAsync("report", ["1"], default));
        Assert.Empty(await store.ReadLatestAsync("review", ["1"], default));
    }

    [SqlAuditFact]
    public async Task StaleRevisionOrChangedSourceCannotOverwriteHistory()
    {
        var store = new ManagementWorkStore(Configuration);
        var initial = (await store.ReadAsync("report", "1", default))!;
        await store.SaveAsync("report", "1", 0, initial.Target.EvidenceHash, "in_progress", "核對中", "one", default);
        await Assert.ThrowsAsync<ManagementWorkConflictException>(() => store.SaveAsync("report", "1", 0, initial.Target.EvidenceHash, "resolved", "舊頁面", "two", default));
        var saved = (await store.ReadAsync("report", "1", default))!;
        await Execute(connection!, "UPDATE dbo.ErrorReports SET ErrorDescription=N'changed' WHERE ReportId=1;");
        await Assert.ThrowsAsync<ManagementWorkConflictException>(() => store.SaveAsync("report", "1", saved.Latest!.EventId, initial.Target.EvidenceHash, "resolved", "舊內容", "two", default));
        var changed = (await store.ReadAsync("report", "1", default))!;
        Assert.Null(changed.Current);
        Assert.Single(changed.History);
    }

    [SqlAuditFact]
    public async Task ConcurrentSubmissionsSaveExactlyOneEvent()
    {
        var store = new ManagementWorkStore(Configuration);
        var initial = (await store.ReadAsync("report", "1", default))!;
        async Task<bool> Save(string actor)
        {
            try { await store.SaveAsync("report", "1", 0, initial.Target.EvidenceHash, "resolved", "verified", actor, default); return true; }
            catch (ManagementWorkConflictException) { return false; }
        }
        var results = await Task.WhenAll(Save("one"), Save("two"));
        Assert.Single(results.Where(result => result));
        Assert.Single((await store.ReadAsync("report", "1", default))!.History);
    }

    [SqlAuditFact]
    public async Task NewAlignmentJobResetsBadgeAndCompletedSongKeepsHistory()
    {
        var store = new ManagementWorkStore(Configuration);
        var initial = (await store.ReadAsync("review", "song", default))!;
        var page = await new TimingReviewQueueReader(Configuration).ReadAsync(1, 25, default);
        Assert.Equal(ManagementWork.Evidence(Assert.Single(page.Items)), initial.Target.EvidenceHash);
        await store.SaveAsync("review", "song", 0, initial.Target.EvidenceHash, "resolved", "checked", "one", default);
        await Execute(connection!, """
            CREATE TABLE dbo.MusicAlignmentJobs(JobId uniqueidentifier,SongUid nvarchar(100),RequestKey char(64),
                State varchar(32),FailureCode varchar(100),CreatedAt datetime2,UpdatedAt datetime2,AppliedAt datetime2);
            INSERT dbo.MusicAlignmentJobs VALUES(NEWID(),'song','hash','needs_review',NULL,SYSUTCDATETIME(),SYSUTCDATETIME(),NULL);
            """);
        var changed = (await store.ReadAsync("review", "song", default))!;
        Assert.Null(changed.Current);
        Assert.Single(changed.History);
        var updatedPage = await new TimingReviewQueueReader(Configuration).ReadAsync(1, 25, default);
        Assert.Equal(ManagementWork.Evidence(Assert.Single(updatedPage.Items)), changed.Target.EvidenceHash);
        await Execute(connection!, "UPDATE dbo.Songs SET HighAccuracyStatus='high_accuracy_completed';");
        Assert.Empty((await new TimingReviewQueueReader(Configuration).ReadAsync(1, 25, default)).Items);
        Assert.Single((await store.ReadAsync("review", "song", default))!.History);
    }

    [SqlAuditFact]
    public async Task RejectsUnknownTargetsAndMissingSchemaWithoutCreatingTables()
    {
        var store = new ManagementWorkStore(Configuration);
        Assert.Null(await store.ReadAsync("report", "999", default));
        Assert.Null(await store.ReadAsync("report", "1 ", default));
        Assert.Null(await store.ReadAsync("review", "SONG", default));
        await Assert.ThrowsAsync<ManagementWorkConflictException>(() => store.SaveAsync("report", "999", 0, new string('A', 64), "resolved", "note", "one", default));
        await Execute(connection!, "DROP TABLE dbo.ManagementWorkEvents;");
        await Assert.ThrowsAsync<ManagementWorkUnavailableException>(() => store.ReadLatestAsync("report", ["1"], default));
    }

    [SqlAuditFact]
    public async Task MigrationIsRepeatableAndInvalidStatusOrEmptyNoteCannotBeStored()
    {
        await Execute(connection!, await File.ReadAllTextAsync(Path.Combine(SourceRoot(), "docs/sql/007-management-work-events.sql")));
        var store = new ManagementWorkStore(Configuration);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync("report", "1", 0, new string('A', 64), "delete", "note", "one", default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync("report", "1", 0, new string('A', 64), "resolved", " ", "one", default));
        Assert.Empty((await store.ReadAsync("report", "1", default))!.History);
    }

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "LearnMore.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Source root unavailable.");
    }

    private static async Task Execute(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
