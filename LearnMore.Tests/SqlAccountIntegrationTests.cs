using System.Data.SqlClient;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using LearnMore.Options;
using LearnMore.Services;
using LearnMore.Services.Mobile;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace LearnMore.Tests;

public sealed class SqlAuditFactAttribute : FactAttribute
{
    public SqlAuditFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LEARNMORE_SQL_AUDIT")))
            Skip = "Requires explicit isolated SQL Server; synthetic unit tests do not replace this audit.";
    }
}

public sealed class SqlAccountIntegrationTests : IAsyncLifetime
{
    private string? connection;
    private string? master;
    private readonly string database = "LearnMoreAudit_" + Guid.NewGuid().ToString("N");
    private readonly string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private readonly string files = Path.Combine(Path.GetTempPath(), "learnmore-sql-audit-" + Guid.NewGuid().ToString("N"));
    private IConfiguration Configuration => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:DefaultConnection"] = connection }).Build();
    private MobileAccountStore Store => new(Configuration, new EphemeralDataProtectionProvider(), new NoNetworkVerifier());
    private AudioStemJobService Stems => new(Configuration, Microsoft.Extensions.Options.Options.Create(new AudioStemProcessingOptions()));
    private static ProviderIdentity Proof(string subject = "owner") => new("google", subject, "test@example.invalid", "Test", true,
        AuthenticatedAt: DateTimeOffset.UtcNow);

    public async Task InitializeAsync()
    {
        var value = Environment.GetEnvironmentVariable("LEARNMORE_SQL_AUDIT");
        if (string.IsNullOrEmpty(value)) return;
        var builder = new SqlConnectionStringBuilder(value);
        if (!builder.DataSource.StartsWith("127.0.0.1,", StringComparison.Ordinal) || builder.InitialCatalog != "master")
            throw new InvalidOperationException("SQL audit only accepts loopback and master; never use a production connection.");
        master = builder.ConnectionString;
        await ExecuteAsync(master, $"CREATE DATABASE [{database}]");
        builder.InitialCatalog = database;
        connection = builder.ConnectionString;
        await SqlAsync(await File.ReadAllTextAsync(Path.Combine(root, "LearnMore.Tests", "SqlAuditSchema.sql")));
        var migration = await File.ReadAllTextAsync(Path.Combine(root, "docs", "sql", "001-mobile-accounts.sql"));
        await SqlAsync(migration);
        await SqlAsync(migration);
        await SqlAsync("INSERT dbo.Users (Email,Name,EnableRoman) VALUES ('test@example.invalid','Test',1); INSERT dbo.MobileIdentities (Provider,Subject,UserId) VALUES ('google','owner',1); INSERT dbo.Songs (SongUid,AddedByUserId) VALUES ('audit-song',1);");
        await Stems.EnsureSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        if (master is not null)
        {
            SqlConnection.ClearAllPools();
            await ExecuteAsync(master, $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]");
        }
        if (Directory.Exists(files)) Directory.Delete(files, true);
    }

    [SqlAuditFact]
    public async Task MigrationIsIdempotentAndForeignKeysEnforceOwnership()
    {
        await using var db = new SqlConnection(connection);
        await db.OpenAsync();
        using var preflight = new SqlCommand(await File.ReadAllTextAsync(Path.Combine(root, "docs", "sql", "000-mobile-preflight.sql")), db);
        using var rows = await preflight.ExecuteReaderAsync();
        Assert.True(await rows.NextResultAsync());
        var columns = 0;
        while (await rows.ReadAsync()) { Assert.Equal("OK", rows.GetString(2)); columns++; }
        Assert.True(columns > 20);
        await rows.CloseAsync();
        Assert.Equal(547, (await Assert.ThrowsAsync<SqlException>(() => SqlAsync("INSERT dbo.MobileSessions(TokenHash,UserId,ExpiresAt) VALUES (REPLICATE('A',64),999,DATEADD(day,1,SYSUTCDATETIME()))"))).Number);
    }

    [SqlAuditFact]
    public async Task UnknownForeignKeyRollsBackDeletionAndRetryCommitsOnce()
    {
        await SqlAsync("CREATE TABLE dbo.UnknownDependency (UserId int REFERENCES dbo.Users(Id)); INSERT dbo.UnknownDependency VALUES(1);");
        Assert.Equal(547, (await Assert.ThrowsAsync<SqlException>(() => CoreAsync("DeleteCoreAsync", 1, Proof(), CancellationToken.None))).Number);
        Assert.Equal(1, await CountAsync("Users"));
        Assert.Equal(1, await CountAsync("Songs"));
        Assert.Equal(0, await CountAsync("MobileFileDeletionJobs"));
        await SqlAsync("DELETE dbo.UnknownDependency;");
        await CoreAsync("DeleteCoreAsync", 1, Proof(), CancellationToken.None);
        await CoreAsync("DeleteCoreAsync", 1, Proof(), CancellationToken.None);
        Assert.Equal(0, await CountAsync("Users"));
        Assert.Equal(0, await CountAsync("MobileIdentities"));
        Assert.Equal(1, await CountAsync("MobileFileDeletionJobs"));
    }

    [SqlAuditFact]
    public async Task CrossAccountProofAndMissingFreshProofCannotMutateSql()
    {
        await Assert.ThrowsAsync<MobileAuthException>(() => CoreAsync("DeleteCoreAsync", 1, Proof("other"), CancellationToken.None));
        await Assert.ThrowsAsync<MobileAuthException>(() => CoreAsync("LinkCoreAsync", 1, Proof("other"), Proof("target"), CancellationToken.None));
        await Assert.ThrowsAsync<MobileAuthException>(() => CoreAsync("LinkCoreAsync", 1, Proof() with { AuthenticatedAt = null }, Proof("target"), CancellationToken.None));
        await Assert.ThrowsAsync<MobileAuthException>(() => CoreAsync("LinkCoreAsync", 1, Proof(), Proof("target") with { AuthenticatedAt = DateTimeOffset.UtcNow.AddMinutes(-6) }, CancellationToken.None));
        await SqlAsync("INSERT dbo.Users (Email,Name,EnableRoman) VALUES ('other@example.invalid','Other',1); INSERT dbo.MobileIdentities (Provider,Subject,UserId) VALUES ('apple','taken',2);");
        await Assert.ThrowsAsync<MobileAuthException>(() => CoreAsync("LinkCoreAsync", 1, Proof(), Proof("taken") with { Provider = "apple" }, CancellationToken.None));
        await CoreAsync("LinkCoreAsync", 1, Proof(), Proof("apple-target") with { Provider = "apple" }, CancellationToken.None);
        Assert.Equal(3, await CountAsync("MobileIdentities"));
        Assert.Equal(2, await CountAsync("Users"));
    }

    [SqlAuditFact]
    public async Task CommittedDeleteInvalidatesCapacitorCodesTokensAndSwiftSessions()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var sessions = new MobileSessionService(cache, new AccountSessionValidator(Configuration));
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var first = sessions.Exchange(sessions.IssueCode(new(1, "Test"), challenge), verifier)!.Value.Token;
        var second = sessions.Exchange(sessions.IssueCode(new(1, "Test"), challenge), verifier)!.Value.Token;
        var pending = sessions.IssueCode(new(1, "Test"), challenge);
        await SqlAsync("INSERT dbo.MobileSessions(TokenHash,UserId,ExpiresAt) VALUES (REPLICATE('A',64),1,DATEADD(day,1,SYSUTCDATETIME()))");
        await CoreAsync("DeleteCoreAsync", 1, Proof(), CancellationToken.None);
        Assert.Null(sessions.Read("Bearer " + first));
        Assert.Null(sessions.Read("Bearer " + second));
        Assert.Null(sessions.Exchange(pending, verifier));
        Assert.Null(await Store.AuthenticateAsync(new string('A',64), default));
        Assert.False(new AccountSessionValidator(Configuration).Exists(1, "test@example.invalid"));
    }

    [SqlAuditFact]
    public async Task DeletedWebsiteSessionCannotRestoreWhenEmailIsReused()
    {
        var website = new PersistentLoginSessionService(new EphemeralDataProtectionProvider(), new AccountSessionValidator(Configuration));
        var login = new DefaultHttpContext { Session = new AuditSession() };
        await website.PersistAsync(login, "test@example.invalid", "1", "Test");
        var cookie = login.Response.Headers.SetCookie.ToString().Split(';')[0];
        await CoreAsync("DeleteCoreAsync", 1, Proof(), CancellationToken.None);
        await SqlAsync("INSERT dbo.Users (Email,Name,EnableRoman) VALUES ('test@example.invalid','New owner',1);");
        foreach (var active in new[] { true, false })
        {
            var next = new DefaultHttpContext { Session = new AuditSession() };
            next.Request.Headers.Cookie = cookie;
            if (active)
            {
                next.Session.SetString("Email", "test@example.invalid");
                next.Session.SetString("UserId", "1");
            }
            await website.RestoreSessionAsync(next);
            Assert.Null(next.Session.GetString("Email"));
            Assert.Contains("LearnMore.PersistentSession=;", next.Response.Headers.SetCookie.ToString());
        }
    }

    [SqlAuditFact]
    public async Task DeletionBeforeLateWorkerNeverPublishesFiles()
    {
        await CoreAsync("DeleteCoreAsync", 1, Proof(), CancellationToken.None);
        await PublishAsync(() => throw new InvalidOperationException("Must not publish missing song"));
        Assert.False(Directory.Exists(files));
        Assert.Equal(0, await CountAsync("SongAudioStems"));
    }

    [SqlAuditFact]
    public async Task PublicationLockBlocksConcurrentAccountDeleteThenOutboxRemovesFiles()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var publication = Task.Run(() => PublishAsync(() =>
        {
            Directory.CreateDirectory(files);
            File.WriteAllText(Path.Combine(files,"instrumental.flac"), "synthetic");
            File.WriteAllText(Path.Combine(files,"vocals.flac"), "synthetic");
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Audit lock release timed out");
        }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        var deletion = CoreAsync("DeleteCoreAsync", 1, Proof(), CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            var blocked = false;
            while (DateTime.UtcNow < deadline && !blocked)
            {
                blocked = await ScalarAsync("SELECT COUNT(*) FROM sys.dm_exec_requests WHERE database_id = DB_ID() AND blocking_session_id > 0") > 0;
                if (!blocked) await Task.Delay(25);
            }
            Assert.True(blocked, "SQL Server must report real lock contention, not a source assertion");
            Assert.False(deletion.IsCompleted);
        }
        finally { release.Set(); }
        await publication;
        await deletion;
        Assert.Equal(0, await CountAsync("SongAudioStems"));
        Assert.True(Directory.Exists(files));
        await MobileFileCleanupService.CompleteDeletionAsync(() => Directory.Delete(files,true), () => SqlAsync("DELETE dbo.MobileFileDeletionJobs"), default);
        Assert.False(Directory.Exists(files));
        Assert.Equal(0, await CountAsync("MobileFileDeletionJobs"));
    }

    [SqlAuditFact]
    public async Task FileFailureKeepsSqlOutboxForRetry()
    {
        await CoreAsync("DeleteCoreAsync", 1, Proof(), CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(() => MobileFileCleanupService.CompleteDeletionAsync(() => throw new IOException("synthetic failure"), () => SqlAsync("DELETE dbo.MobileFileDeletionJobs"), default));
        Assert.Equal(1, await CountAsync("MobileFileDeletionJobs"));
        await MobileFileCleanupService.CompleteDeletionAsync(() => {}, () => SqlAsync("DELETE dbo.MobileFileDeletionJobs"), default);
        Assert.Equal(0, await CountAsync("MobileFileDeletionJobs"));
    }

    private Task PublishAsync(Action publish) => Stems.RegisterCompletedStemsAsync("audit-song", Path.Combine(files,"instrumental.flac"), Path.Combine(files,"vocals.flac"), "synthetic", "audit", publish);
    private Task CoreAsync(string name, params object[] arguments)
        => (Task)typeof(MobileAccountStore).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Store, arguments)!;
    private Task SqlAsync(string sql) => ExecuteAsync(connection!, sql);
    private Task<int> CountAsync(string table) => ScalarAsync($"SELECT COUNT(*) FROM dbo.[{table}]");
    private async Task<int> ScalarAsync(string sql)
    {
        await using var db = new SqlConnection(connection);
        await db.OpenAsync();
        using var command = new SqlCommand(sql, db);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
    private static async Task ExecuteAsync(string connection, string sql)
    {
        await using var db = new SqlConnection(connection);
        await db.OpenAsync();
        using var command = new SqlCommand(sql,db);
        await command.ExecuteNonQueryAsync();
    }
    private sealed class NoNetworkVerifier : IMobileIdentityVerifier
    {
        public bool GoogleEnabled => false;
        public bool AppleEnabled => false;
        public Task<ProviderIdentity> VerifyAsync(MobileLoginRequest request, CancellationToken ct) => throw new InvalidOperationException("No real OAuth");
        public Task RevokeAppleAsync(string refreshToken, CancellationToken ct) => throw new InvalidOperationException("No real Apple revocation");
    }

    private sealed class AuditSession : ISession
    {
        private readonly Dictionary<string, byte[]> values = new();
        public bool IsAvailable => true;
        public string Id => "sql-audit";
        public IEnumerable<string> Keys => values.Keys;
        public void Clear() => values.Clear();
        public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Remove(string key) => values.Remove(key);
        public void Set(string key, byte[] value) => values[key] = value;
        public bool TryGetValue(string key, out byte[] value) => values.TryGetValue(key, out value!);
    }
}
