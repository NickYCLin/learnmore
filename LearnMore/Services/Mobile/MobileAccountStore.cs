using System.Data;
using System.Data.SqlClient;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace LearnMore.Services.Mobile;

public sealed partial class MobileAccountStore(IConfiguration configuration, IDataProtectionProvider protection,
    IMobileIdentityVerifier verifier) : IMobileAccountStore
{
    private readonly string connectionString = configuration.GetConnectionString("DefaultConnection") ?? "";
    private readonly IDataProtector protector = protection.CreateProtector("LearnMore.Mobile.AppleRefresh.v1");
    public static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public async Task<MobileSession> SignInAsync(ProviderIdentity identity, CancellationToken ct)
    {
        await using var db = new SqlConnection(connectionString);
        await db.OpenAsync(ct);
        using var tx = db.BeginTransaction(IsolationLevel.Serializable);
        var userId = await FindIdentityAsync(db, tx, identity, ct);
        if (userId is null)
        {
            using var find = Command(db, tx, "SELECT Id FROM dbo.Users WITH (UPDLOCK, HOLDLOCK) WHERE Email = @Email", ("@Email", identity.Email));
            var existing = await find.ExecuteScalarAsync(ct);
            if (existing is not null && (identity.Provider != "google" || !identity.CanMatchLegacyEmail))
                throw new MobileAuthException("此電子郵件已有帳號，請先以原本的 Google 帳號登入，再於帳號頁連結 Apple。", 409);
            if (existing is not null) userId = Convert.ToInt32(existing);
            else
            {
                if (string.IsNullOrWhiteSpace(identity.Email)) throw new MobileAuthException("首次登入需要提供電子郵件。", 400);
                using var create = Command(db, tx, """
                    INSERT INTO dbo.Users (Name, Email, EnableRoman)
                    OUTPUT INSERTED.Id VALUES (@Name, @Email, 1)
                    """, ("@Name", identity.Name), ("@Email", identity.Email));
                userId = Convert.ToInt32(await create.ExecuteScalarAsync(ct));
            }
            await InsertIdentityAsync(db, tx, userId.Value, identity, ct);
        }
        else if (identity.RefreshToken is not null)
        {
            using var update = Command(db, tx, """
                UPDATE dbo.MobileIdentities SET ProtectedRefreshToken = @Token
                WHERE Provider = @Provider AND Subject = @Subject
                """, ("@Token", protector.Protect(identity.RefreshToken)), ("@Provider", identity.Provider), ("@Subject", identity.Subject));
            await update.ExecuteNonQueryAsync(ct);
        }
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using var session = Command(db, tx, """
            DELETE FROM dbo.MobileSessions WHERE ExpiresAt <= SYSUTCDATETIME();
            INSERT INTO dbo.MobileSessions (TokenHash, UserId, ExpiresAt)
            VALUES (@Hash, @UserId, DATEADD(day, 30, SYSUTCDATETIME()));
            """, ("@Hash", HashToken(token)), ("@UserId", userId.Value));
        await session.ExecuteNonQueryAsync(ct);
        var user = await ReadUserAsync(db, tx, userId.Value, ct);
        await tx.CommitAsync(ct);
        return new(token, user!);
    }

    public async Task<MobileUser?> AuthenticateAsync(string token, CancellationToken ct)
    {
        if (token.Length != 64 || !token.All(Uri.IsHexDigit)) return null;
        await using var db = new SqlConnection(connectionString);
        await db.OpenAsync(ct);
        using var cmd = Command(db, null, """
            SELECT S.UserId FROM dbo.MobileSessions S INNER JOIN dbo.Users U ON U.Id = S.UserId
            WHERE S.TokenHash = @Hash AND S.ExpiresAt > SYSUTCDATETIME()
            """, ("@Hash", HashToken(token)));
        var id = await cmd.ExecuteScalarAsync(ct);
        return id is null ? null : await ReadUserAsync(db, null, Convert.ToInt32(id), ct);
    }

    public async Task SignOutAsync(string token, CancellationToken ct)
    {
        await using var db = new SqlConnection(connectionString);
        await db.OpenAsync(ct);
        using var cmd = Command(db, null, "DELETE FROM dbo.MobileSessions WHERE TokenHash = @Hash", ("@Hash", HashToken(token)));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task LinkAsync(int userId, ProviderIdentity identity, CancellationToken ct)
    {
        await using var db = new SqlConnection(connectionString);
        await db.OpenAsync(ct);
        using var tx = db.BeginTransaction(IsolationLevel.Serializable);
        var owner = await FindIdentityAsync(db, tx, identity, ct);
        if (owner.HasValue && owner != userId)
            throw new MobileAuthException("這個登入方式已連結其他帳號，無法自動合併。", 409);
        using var existing = Command(db, tx, "SELECT Subject FROM dbo.MobileIdentities WITH (UPDLOCK, HOLDLOCK) WHERE UserId = @Id AND Provider = @Provider",
            ("@Id", userId), ("@Provider", identity.Provider));
        var subject = await existing.ExecuteScalarAsync(ct) as string;
        if (subject is not null && subject != identity.Subject)
            throw new MobileAuthException("帳號已連結另一個相同服務的帳號。", 409);
        if (!owner.HasValue) await InsertIdentityAsync(db, tx, userId, identity, ct);
        await tx.CommitAsync(ct);
    }

    public async Task DeleteAsync(int userId, ProviderIdentity proof, CancellationToken ct)
    {
        await using var db = new SqlConnection(connectionString);
        await db.OpenAsync(ct);
        using var tx = db.BeginTransaction(IsolationLevel.Serializable);
        // Fresh provider proof must belong to this account. Never accept an arbitrary email as confirmation.
        if (await FindIdentityAsync(db, tx, proof, ct) != userId)
            throw new MobileAuthException("請使用此帳號已連結的登入方式確認刪除。", 403);
        using (var apple = Command(db, tx, "SELECT ProtectedRefreshToken FROM dbo.MobileIdentities WITH (UPDLOCK, HOLDLOCK) WHERE UserId = @Id AND Provider = 'apple'", ("@Id", userId)))
        {
            if (await apple.ExecuteScalarAsync(ct) is string protectedToken)
                await verifier.RevokeAppleAsync(protector.Unprotect(protectedToken), ct);
        }
        // A fresh Apple proof may have issued another refresh token; revoke that as well.
        if (proof.Provider == "apple" && proof.RefreshToken is not null)
            await verifier.RevokeAppleAsync(proof.RefreshToken, ct);

        using var userQuery = Command(db, tx, "SELECT Email, Avatar FROM dbo.Users WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", ("@Id", userId));
        string email;
        string? avatar;
        using (var reader = await userQuery.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) { await tx.CommitAsync(ct); return; }
            email = reader.GetString(0);
            avatar = reader.IsDBNull(1) ? null : reader.GetString(1);
        }
        await DeleteOwnedSongsAsync(db, tx, userId, ct);
        // Known website content is removed in the same transaction. Unknown FK dependencies fail closed.
        using var delete = Command(db, tx, """
            DELETE R FROM dbo.CommentReplies R INNER JOIN dbo.Comments C ON C.CommentId = R.CommentId WHERE C.UserEmail = @Email;
            DELETE FROM dbo.CommentReplies WHERE AdminEmail = @Email;
            DELETE FROM dbo.Comments WHERE UserEmail = @Email;
            DELETE FROM dbo.WishReply WHERE UserId = @Id OR WishId IN (SELECT Id FROM dbo.Wish WHERE UserId = @Id);
            DELETE FROM dbo.Wish WHERE UserId = @Id;
            DELETE FROM dbo.Feedbacks WHERE Email = @Email;
            DELETE FROM dbo.ErrorReports WHERE UserEmail = @Email;
            DELETE M FROM dbo.SongGroupMapping M INNER JOIN dbo.SongGroup G ON G.GroupId = M.GroupId WHERE G.UserId = @TextId;
            DELETE FROM dbo.SongGroup WHERE UserId = @TextId;
            DELETE FROM dbo.MobileSessions WHERE UserId = @Id;
            DELETE FROM dbo.MobileIdentities WHERE UserId = @Id;
            DELETE FROM dbo.Users WHERE Id = @Id;
            """, ("@Email", email), ("@Id", userId), ("@TextId", userId.ToString()));
        await delete.ExecuteNonQueryAsync(ct);
        if (avatar?.StartsWith("/uploads/", StringComparison.Ordinal) == true)
        {
            var name = avatar["/uploads/".Length..];
            if (name == Path.GetFileName(name) && Guid.TryParse(Path.GetFileNameWithoutExtension(name), out _) &&
                Path.GetExtension(name).Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                using var job = Command(db, tx, "INSERT INTO dbo.MobileFileDeletionJobs (FileName) VALUES (@Name)", ("@Name", name));
                await job.ExecuteNonQueryAsync(ct);
            }
        }
        await tx.CommitAsync(ct);
    }

    private static async Task<int?> FindIdentityAsync(SqlConnection db, SqlTransaction? tx, ProviderIdentity identity, CancellationToken ct)
    {
        using var cmd = Command(db, tx, "SELECT UserId FROM dbo.MobileIdentities WITH (UPDLOCK, HOLDLOCK) WHERE Provider = @Provider AND Subject = @Subject",
            ("@Provider", identity.Provider), ("@Subject", identity.Subject));
        var id = await cmd.ExecuteScalarAsync(ct);
        return id is null ? null : Convert.ToInt32(id);
    }

    private async Task InsertIdentityAsync(SqlConnection db, SqlTransaction tx, int id, ProviderIdentity identity, CancellationToken ct)
    {
        using var cmd = Command(db, tx, """
            INSERT INTO dbo.MobileIdentities (Provider, Subject, UserId, ProtectedRefreshToken)
            VALUES (@Provider, @Subject, @Id, @Token)
            """, ("@Provider", identity.Provider), ("@Subject", identity.Subject), ("@Id", id),
            ("@Token", identity.RefreshToken is null ? DBNull.Value : protector.Protect(identity.RefreshToken)));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<MobileUser?> ReadUserAsync(SqlConnection db, SqlTransaction? tx, int id, CancellationToken ct)
    {
        using var cmd = Command(db, tx, "SELECT COALESCE(NULLIF(NickName,''), Name, N'LearnMore 使用者'), Email FROM dbo.Users WHERE Id = @Id", ("@Id", id));
        string name, email;
        using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return null;
            name = reader.GetString(0); email = reader.GetString(1);
        }
        using var providers = Command(db, tx, "SELECT Provider FROM dbo.MobileIdentities WHERE UserId = @Id ORDER BY Provider", ("@Id", id));
        using var rows = await providers.ExecuteReaderAsync(ct);
        var names = new List<string>();
        while (await rows.ReadAsync(ct)) names.Add(rows.GetString(0));
        return new(id, name, email, names.ToArray());
    }

    internal static SqlCommand Command(SqlConnection db, SqlTransaction? tx, string sql, params (string Name, object Value)[] parameters)
    {
        var cmd = new SqlCommand(sql, db, tx);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return cmd;
    }
}
