using System.Data.SqlClient;
using LearnMore.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LearnMore.Tests;

public sealed class ErrorReportRubySqlTests : IAsyncLifetime
{
    private readonly string database = "LearnMoreRubyAudit_" + Guid.NewGuid().ToString("N");
    private string? master;
    private string? connection;
    private readonly Audit audit = new();
    private ErrorReportRubyRepair Store => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
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
            CREATE TABLE dbo.Songs (SongUid nvarchar(100) PRIMARY KEY, Title nvarchar(255), YouTubeVideoUrl nvarchar(255));
            CREATE TABLE dbo.ErrorReports (ReportID int PRIMARY KEY, ErrorSongUid nvarchar(100), ErrorLyricID int, Status int);
            CREATE TABLE dbo.[Songs_{RubyRepairTarget.SongUid}] (LyricID int PRIMARY KEY, Japanese nvarchar(max), JapaneseRuby nvarchar(max), Roman nvarchar(max), Chinese nvarchar(max), TimeStamp float);
            INSERT dbo.Songs VALUES ('{RubyRepairTarget.SongUid}',N'水平線','{RubyRepairTarget.VideoSource}');
            INSERT dbo.ErrorReports VALUES (46,'{RubyRepairTarget.SongUid}',3,0),(48,'{RubyRepairTarget.SongUid}',13,0),(49,'{RubyRepairTarget.SongUid}',15,0);
            INSERT dbo.[Songs_{RubyRepairTarget.SongUid}] VALUES (3,N'人',N'{RubyRepairTarget.Find(46)!.Before}','hito',N'原值',73.14);
            """);
    }

    public async Task DisposeAsync()
    {
        if (master == null) return;
        SqlConnection.ClearAllPools();
        await Execute(master, $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]");
    }

    [SqlAuditFact]
    public async Task OnlyRubyChangesAndReplayCannotWriteAgain()
    {
        var before = await Store.ReadAsync(46, default);
        var receipt = await Store.ApplyAsync(46, before.Fingerprint, "manager@example.invalid", default);
        Assert.True(receipt.CompletionRecorded);
        Assert.Equal(before.Snapshot.Lyric, audit.Before!.Lyric);
        Assert.Equal(RubyRepairTarget.Find(46)!.After, await Scalar($"SELECT JapaneseRuby FROM dbo.[Songs_{RubyRepairTarget.SongUid}] WHERE LyricID=3"));
        Assert.Equal("hito", await Scalar($"SELECT Roman FROM dbo.[Songs_{RubyRepairTarget.SongUid}] WHERE LyricID=3"));
        Assert.Equal(0, await Scalar("SELECT Status FROM dbo.ErrorReports WHERE ReportID=46"));
        var readback = await Store.ReadAsync(46, default);
        Assert.Null(readback.ProposedRuby);
        foreach (var field in before.Snapshot.Lyric.Keys)
            Assert.Equal(field == "JapaneseRuby" ? RubyRepairTarget.Find(46)!.After : before.Snapshot.Lyric[field], readback.Snapshot.Lyric[field]);
        await Assert.ThrowsAsync<RubyRepairConflictException>(() => Store.ApplyAsync(46, before.Fingerprint, "manager@example.invalid", default));
        await Assert.ThrowsAsync<RubyRepairConflictException>(() => Store.ApplyAsync(46, readback.Fingerprint, "manager@example.invalid", default));
        Assert.Equal(1, audit.Prepared);
    }

    [SqlAuditFact]
    public async Task AuditFailureOrConcurrentChangeDoesNotOverwrite()
    {
        var before = await Store.ReadAsync(46, default);
        audit.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => Store.ApplyAsync(46, before.Fingerprint, "manager@example.invalid", default));
        Assert.Equal(RubyRepairTarget.Find(46)!.Before, await Scalar($"SELECT JapaneseRuby FROM dbo.[Songs_{RubyRepairTarget.SongUid}] WHERE LyricID=3"));
        audit.Fail = false;
        await Execute(connection!, $"UPDATE dbo.[Songs_{RubyRepairTarget.SongUid}] SET Roman='someone else' WHERE LyricID=3");
        await Assert.ThrowsAsync<RubyRepairConflictException>(() => Store.ApplyAsync(46, before.Fingerprint, "manager@example.invalid", default));
        Assert.Equal("someone else", await Scalar($"SELECT Roman FROM dbo.[Songs_{RubyRepairTarget.SongUid}] WHERE LyricID=3"));
    }

    [SqlAuditFact]
    public async Task WrongSongVersionOrLyricBindingRejectsBeforeAudit()
    {
        await Execute(connection!, "UPDATE dbo.ErrorReports SET ErrorLyricID=13 WHERE ReportID=46");
        await Assert.ThrowsAsync<RubyRepairConflictException>(() => Store.ReadAsync(46, default));
        await Execute(connection!, "UPDATE dbo.ErrorReports SET ErrorLyricID=3 WHERE ReportID=46; UPDATE dbo.Songs SET YouTubeVideoUrl='another-video'");
        await Assert.ThrowsAsync<RubyRepairConflictException>(() => Store.ReadAsync(46, default));
        Assert.Equal(0, audit.Prepared);
    }

    [SqlAuditFact]
    public async Task UnexpectedTriggerSideEffectsRollBackRubyAndStatus()
    {
        var before = await Store.ReadAsync(46, default);
        await Execute(connection!, $"CREATE TRIGGER RubySideEffect ON dbo.[Songs_{RubyRepairTarget.SongUid}] AFTER UPDATE AS UPDATE dbo.ErrorReports SET Status=1 WHERE ReportID=46");
        await Assert.ThrowsAsync<RubyRepairConflictException>(() => Store.ApplyAsync(46, before.Fingerprint, "manager@example.invalid", default));
        Assert.Equal(0, await Scalar("SELECT Status FROM dbo.ErrorReports WHERE ReportID=46"));
        Assert.Equal(RubyRepairTarget.Find(46)!.Before, await Scalar($"SELECT JapaneseRuby FROM dbo.[Songs_{RubyRepairTarget.SongUid}] WHERE LyricID=3"));
    }

    private async Task<object?> Scalar(string text)
    {
        await using var sql = new SqlConnection(connection);
        await sql.OpenAsync();
        await using var command = new SqlCommand(text, sql);
        return await command.ExecuteScalarAsync();
    }

    private static async Task Execute(string connectionString, string text)
    {
        await using var sql = new SqlConnection(connectionString);
        await sql.OpenAsync();
        await using var command = new SqlCommand(text, sql);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class Audit : IRubyRepairAudit
    {
        public bool Fail { get; set; }
        public int Prepared { get; private set; }
        public RubyRepairSnapshot? Before { get; private set; }
        public string Prepare(RubyRepairSnapshot before, string afterRuby, string actor)
        {
            if (Fail) throw new IOException("synthetic audit failure");
            Prepared++;
            Before = before;
            return "test";
        }
        public bool Complete(string auditId) => true;
    }
}
