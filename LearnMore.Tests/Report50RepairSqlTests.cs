using System.Data.SqlClient;
using LearnMore.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LearnMore.Tests;

public sealed class Report50RepairSqlTests : IAsyncLifetime
{
    private readonly string database = "LearnMoreReport50Audit_" + Guid.NewGuid().ToString("N");
    private string? master;
    private string? connection;
    private readonly Audit audit = new();
    private Report50Repair Store => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:DefaultConnection"] = connection }).Build(), audit);

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
        await Execute(connection, $"""
            CREATE TABLE dbo.Songs (SongUid nvarchar(100) PRIMARY KEY, YouTubeVideoUrl nvarchar(255));
            CREATE TABLE dbo.ErrorReports (ReportID int PRIMARY KEY, ErrorSongUid nvarchar(100), ErrorLyricID int, Status int);
            CREATE TABLE dbo.[Songs_{RubyRepairTarget.SongUid}] (LyricID int PRIMARY KEY, Japanese nvarchar(max), JapaneseRuby nvarchar(max), Roman nvarchar(max), Chinese nvarchar(max), TimeStamp float, OtherColumn nvarchar(100));
            INSERT dbo.Songs VALUES ('{RubyRepairTarget.SongUid}','{RubyRepairTarget.VideoSource}');
            INSERT dbo.ErrorReports VALUES (50,'{RubyRepairTarget.SongUid}',22,0);
            """);
        foreach (var row in Report50RepairTests.Before().Lyrics)
        {
            await using var sql = new SqlConnection(connection);
            await sql.OpenAsync();
            await using var command = new SqlCommand($"INSERT dbo.[Songs_{RubyRepairTarget.SongUid}] VALUES (@LyricID,@Japanese,@JapaneseRuby,@Roman,@Chinese,@TimeStamp,@OtherColumn)", sql);
            foreach (var field in row.Keys) command.Parameters.AddWithValue("@" + field, row[field]!);
            await command.ExecuteNonQueryAsync();
        }
    }

    public async Task DisposeAsync()
    {
        if (master == null) return;
        SqlConnection.ClearAllPools();
        await Execute(master, $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]");
    }

    [SqlAuditFact]
    public async Task TwoRowsCommitTogetherPreserveAllOtherFieldsAndRejectReplay()
    {
        var before = (await Store.ReadAsync(50, default)).Snapshot;
        var receipt = await Store.ApplyAsync(50, before.Fingerprint, "manager@example.invalid", default);
        var after = await Store.ReadAsync(50, default);
        Assert.True(receipt.CompletionRecorded);
        Assert.True(Report50RepairPlan.Same(Report50RepairPlan.Prepare(before, before.Fingerprint), after.Snapshot));
        Assert.True(Report50RepairPlan.Same(before, audit.Before!));
        Assert.True(Report50RepairPlan.Same(after.Snapshot, audit.After!));
        Assert.False(after.CanApply);
        Assert.True(after.AlreadyApplied);
        await Assert.ThrowsAsync<RubyRepairConflictException>(() => Store.ApplyAsync(50, before.Fingerprint, "manager@example.invalid", default));
        await Assert.ThrowsAsync<RubyRepairConflictException>(() => Store.ApplyAsync(50, after.Snapshot.Fingerprint, "manager@example.invalid", default));
        Assert.Equal(1, audit.Prepared);
    }

    [SqlAuditFact]
    public async Task SecondRowFailureRollsBackFirstRow()
    {
        var before = (await Store.ReadAsync(50, default)).Snapshot;
        await Execute(connection!, $"ALTER TABLE dbo.[Songs_{RubyRepairTarget.SongUid}] ADD CONSTRAINT RejectSecondRow CHECK (LyricID<>22 OR LEFT(Japanese,1)=N'る')");
        await Assert.ThrowsAsync<SqlException>(() => Store.ApplyAsync(50, before.Fingerprint, "manager@example.invalid", default));
        Assert.True(Report50RepairPlan.Same(before, (await Store.ReadAsync(50, default)).Snapshot));
        Assert.Null(audit.After);
    }

    [SqlAuditFact]
    public async Task AuditFailureAndStaleFingerprintCannotWrite()
    {
        var before = (await Store.ReadAsync(50, default)).Snapshot;
        audit.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => Store.ApplyAsync(50, before.Fingerprint, "manager@example.invalid", default));
        Assert.True(Report50RepairPlan.Same(before, (await Store.ReadAsync(50, default)).Snapshot));
        audit.Fail = false;
        await Execute(connection!, $"UPDATE dbo.[Songs_{RubyRepairTarget.SongUid}] SET OtherColumn=N'parallel edit' WHERE LyricID=22");
        var parallel = (await Store.ReadAsync(50, default)).Snapshot;
        await Assert.ThrowsAsync<RubyRepairConflictException>(() => Store.ApplyAsync(50, before.Fingerprint, "manager@example.invalid", default));
        Assert.True(Report50RepairPlan.Same(parallel, (await Store.ReadAsync(50, default)).Snapshot));
        Assert.Equal(0, audit.Prepared);
    }

    [SqlAuditFact]
    public async Task TriggerSideEffectOnStatusRollsBackBothRows()
    {
        var before = (await Store.ReadAsync(50, default)).Snapshot;
        await Execute(connection!, $"CREATE TRIGGER UnexpectedStatus ON dbo.[Songs_{RubyRepairTarget.SongUid}] AFTER UPDATE AS UPDATE dbo.ErrorReports SET Status=1 WHERE ReportID=50");
        await Assert.ThrowsAsync<RubyRepairConflictException>(() => Store.ApplyAsync(50, before.Fingerprint, "manager@example.invalid", default));
        Assert.True(Report50RepairPlan.Same(before, (await Store.ReadAsync(50, default)).Snapshot));
        Assert.Null(audit.After);
    }

    [SqlAuditFact]
    public async Task BindingMismatchRejectsBeforeAudit()
    {
        var before = (await Store.ReadAsync(50, default)).Snapshot;
        await Execute(connection!, "UPDATE dbo.ErrorReports SET ErrorLyricID=7 WHERE ReportID=50");
        await Assert.ThrowsAsync<RubyRepairConflictException>(() => Store.ReadAsync(50, default));
        await Assert.ThrowsAsync<RubyRepairConflictException>(() => Store.ApplyAsync(50, before.Fingerprint, "manager@example.invalid", default));
        Assert.Equal(0, audit.Prepared);
    }

    private static async Task Execute(string connectionString, string text)
    {
        await using var sql = new SqlConnection(connectionString);
        await sql.OpenAsync();
        await using var command = new SqlCommand(text, sql);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class Audit : IReport50RepairAudit
    {
        public bool Fail { get; set; }
        public int Prepared { get; private set; }
        public Report50RepairSnapshot? Before { get; private set; }
        public Report50RepairSnapshot? After { get; private set; }
        public string Prepare(Report50RepairSnapshot before, Report50RepairSnapshot after, string actor)
        {
            if (Fail) throw new IOException("synthetic audit failure");
            Prepared++;
            Before = before;
            return "test";
        }
        public bool Complete(string auditId, Report50RepairSnapshot after)
        {
            After = after;
            return true;
        }
    }
}
