using System.Data.SqlClient;
using LearnMore.Controllers;
using LearnMore.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LearnMore.Tests;

public sealed class ErrorReportSqlTests : IAsyncLifetime
{
    private readonly string database = "LearnMoreErrorReportAudit_" + Guid.NewGuid().ToString("N");
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
            CREATE TABLE dbo.Users (Email nvarchar(255), Manager bit NULL);
            CREATE TABLE dbo.Songs (SongUid nvarchar(100) PRIMARY KEY, Title nvarchar(255), YouTubeVideoUrl nvarchar(255));
            CREATE TABLE dbo.ErrorReports (AuditReportNumber int NOT NULL PRIMARY KEY,
                UserEmail nvarchar(255), ErrorDescription nvarchar(max), ErrorSongUid nvarchar(100),
                ErrorLyricID int, ReportDate datetime2, Status int NULL);
            INSERT dbo.Users VALUES ('manager@example.invalid',1),('member@example.invalid',0),('unknown@example.invalid',NULL);
            INSERT dbo.Songs VALUES ('version-a','same title','video-a'),('version-b','same title','video-b');
            INSERT dbo.ErrorReports VALUES
              (1,'member@example.invalid','first','version-a',10,'2026-11-04',0),
              (2,'member@example.invalid','second','version-b',11,'2026-11-04',1),
              (3,'member@example.invalid','orphan','missing-song',12,'2026-11-04',NULL);
            """);
    }

    public async Task DisposeAsync()
    {
        if (master == null) return;
        SqlConnection.ClearAllPools();
        await Execute(master, $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
    }

    [SqlAuditFact]
    public async Task ActualSqlPagesUseDiscoveredKeyAndPreserveEveryStatusVersionAndOrphan()
    {
        var reader = new ErrorReportReader(Configuration);
        var items = new List<ErrorReportItem>();
        for (var pageNumber = 1; pageNumber <= 3; pageNumber++)
        {
            var page = await reader.ReadAsync(pageNumber, 1, default);
            Assert.Equal(3, page.TotalCount);
            Assert.Equal("AuditReportNumber", page.ReportIdColumn);
            items.Add(Assert.Single(page.Items));
        }
        Assert.Equal(new[] { "3", "2", "1" }, items.Select(item => item.ReportId));
        Assert.Null(items[0].VideoSource);
        Assert.Null(items[0].Status);
        Assert.Equal("video-b", items[1].VideoSource);
        Assert.Equal("video-a", items[2].VideoSource);
        Assert.Equal("0", items[2].Status);
        var beyond = await reader.ReadAsync(4, 1, default);
        Assert.Empty(beyond.Items);
        Assert.Equal(3, beyond.TotalCount);
    }

    [SqlAuditFact]
    public async Task RealManagerFlagNotAccountNameOrLoginGrantsAccess()
    {
        var access = new ErrorReportManagerAccess(Configuration);
        foreach (var email in new[] { "member@example.invalid", "unknown@example.invalid", "missing@example.invalid" })
        {
            var result = await access.AuthorizeAsync(Controller(email), default);
            Assert.Equal(403, Assert.IsType<StatusCodeResult>(result).StatusCode);
        }
        Assert.Null(await access.AuthorizeAsync(Controller("manager@example.invalid"), default));
    }

    [SqlAuditFact]
    public async Task MissingUniqueKeyFailsClosedInsteadOfInventingReportIds()
    {
        await Execute(connection!, "ALTER TABLE dbo.ErrorReports DROP CONSTRAINT " + await PrimaryKeyName());
        await Assert.ThrowsAsync<ErrorReportSchemaException>(() => new ErrorReportReader(Configuration).ReadAsync(1, 25, default));
    }

    private async Task<string> PrimaryKeyName()
    {
        await using var sql = new SqlConnection(connection);
        await sql.OpenAsync();
        await using var command = new SqlCommand("SELECT name FROM sys.key_constraints WHERE parent_object_id=OBJECT_ID('dbo.ErrorReports') AND type='PK'", sql);
        return ErrorReportReader.QuoteIdentifier((string)(await command.ExecuteScalarAsync())!);
    }

    private static Controller Controller(string email)
    {
        var context = new DefaultHttpContext { Session = new EmailSession(email) };
        return new UserStub { ControllerContext = new ControllerContext { HttpContext = context } };
    }

    private static async Task Execute(string configuration, string sql)
    {
        await using var databaseConnection = new SqlConnection(configuration);
        await databaseConnection.OpenAsync();
        await using var command = new SqlCommand(sql, databaseConnection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class UserStub : Controller { }
    private sealed class EmailSession(string email) : ISession
    {
        public bool IsAvailable => true;
        public string Id => "isolated-audit";
        public IEnumerable<string> Keys => ["Email"];
        public void Clear() { }
        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Remove(string key) { }
        public void Set(string key, byte[] value) { }
        public bool TryGetValue(string key, out byte[] value)
        {
            value = System.Text.Encoding.UTF8.GetBytes(email);
            return key == "Email";
        }
    }
}
