using System.Data.SqlClient;
using LearnMore.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LearnMore.Tests;

public sealed class TimingReviewQueueSqlTests : IAsyncLifetime
{
    private readonly string database = "LearnMoreQueueAudit_" + Guid.NewGuid().ToString("N");
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
            CREATE TABLE dbo.Songs(SongID int PRIMARY KEY,SongUid nvarchar(64),Title nvarchar(255),YouTubeVideoUrl nvarchar(255),
              HighAccuracyStatus nvarchar(50),HighAccuracyStatusReason nvarchar(255));
            INSERT dbo.Songs VALUES
              (1,'version-a','title','https://youtu.be/j_NHrPC3ij8','high_accuracy_partial','partial'),
              (2,'version-b','same title','https://youtu.be/Y8HeOA95UzQ','high_accuracy_failed','failed'),
              (3,'version-c','title','https://youtu.be/YOYeJn4mz8M','high_accuracy_needs_review','review'),
              (4,'version-a','title','https://youtu.be/j_NHrPC3ij8','high_accuracy_partial','partial'),
              (5,'complete','title','https://youtu.be/q9iozQDEO_U','high_accuracy_completed',NULL);
            """);
    }

    public async Task DisposeAsync()
    {
        if (master == null) return;
        SqlConnection.ClearAllPools();
        await Execute(master, $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
    }

    [SqlAuditFact]
    public async Task ActualSqlDeduplicatesOnlyUidAndPreservesVideoVersionsAcrossAllPages()
    {
        var reader = new TimingReviewQueueReader(Configuration);
        var items = new List<TimingReviewQueueItem>();
        for (var pageNumber = 1; pageNumber <= 3; pageNumber++)
        {
            var page = await reader.ReadAsync(pageNumber, 1, default);
            Assert.Equal(3, page.TotalCount);
            Assert.Equal(3, page.TotalPages);
            Assert.False(page.JobsAvailable);
            items.Add(Assert.Single(page.Items));
        }
        Assert.Equal(new[] { "version-a", "version-c", "version-b" }, items.Select(item => item.SongUid));
        Assert.Equal(3, items.Select(item => item.YoutubeId).Distinct().Count());
        Assert.All(items, item => Assert.True(TimingReviewQueueReader.NeedsReview(item.Status)));
        Assert.Empty((await reader.ReadAsync(4, 1, default)).Items);
    }

    [SqlAuditFact]
    public async Task LatestJobIsReadWithoutStartingOrMutatingJobsAndOriginalRequestIsNotInvented()
    {
        await Execute(connection!, """
            CREATE TABLE dbo.MusicAlignmentJobs(JobId uniqueidentifier PRIMARY KEY,RequestKey char(64),SongUid nvarchar(64),
              State varchar(32),FailureCode varchar(100),CreatedAt datetime2,UpdatedAt datetime2,AppliedAt datetime2);
            INSERT dbo.MusicAlignmentJobs VALUES
              ('00000000-0000-0000-0000-000000000001',REPLICATE('a',64),'version-a','needs_review','old','2026-01-01','2026-01-01',NULL),
              ('00000000-0000-0000-0000-000000000002',REPLICATE('b',64),'version-a','needs_review','new','2026-01-02','2026-01-02',NULL);
            """);
        var page = await new TimingReviewQueueReader(Configuration).ReadAsync(1, 100, default);
        Assert.True(page.JobsAvailable);
        var item = page.Items.Single(item => item.SongUid == "version-a");
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000002"), item.JobId);
        Assert.Equal(new string('b', 64), item.RequestKeyHash);
        Assert.Equal("new", item.FailureCode);
        Assert.Null(item.AppliedAt);
        Assert.Null(page.Items.Single(item => item.SongUid == "version-b").JobId);
        using var db = new SqlConnection(connection);
        await db.OpenAsync();
        using var count = new SqlCommand("SELECT COUNT(*) FROM dbo.MusicAlignmentJobs WHERE State='needs_review' AND AppliedAt IS NULL", db);
        Assert.Equal(2, (int)(await count.ExecuteScalarAsync())!);
    }

    [SqlAuditFact]
    public async Task MissingStatusColumnFailsClosedInsteadOfReturningEmptyQueue()
    {
        await Execute(connection!, "ALTER TABLE dbo.Songs DROP COLUMN HighAccuracyStatusReason");
        await Assert.ThrowsAsync<TimingReviewQueueSchemaException>(() => new TimingReviewQueueReader(Configuration).ReadAsync(1, 25, default));
    }

    private static async Task Execute(string connectionString, string sql)
    {
        using var db = new SqlConnection(connectionString);
        await db.OpenAsync();
        using var command = new SqlCommand(sql, db);
        await command.ExecuteNonQueryAsync();
    }
}
