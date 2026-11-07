using System.Data;
using System.Data.SqlClient;

namespace LearnMore.Services;

public sealed record ManagedUser(int Id, string Name, string NickName, string Email, bool Manager,
    bool IsDisabled, DateTime? DeletedAt, DateTime? LastLoginAt, long Revision, DateTime? CreatedAt,
    string? Avatar = null, string? Picture = null);
public sealed record ManagedUserEvent(string Action, string Reason, int ActorUserId, DateTime CreatedAt);
public sealed record ManagedUserDetail(ManagedUser User, IReadOnlyDictionary<string, long?> Activity,
    IReadOnlyList<string> Providers, IReadOnlyList<ManagedUserEvent> Events);
public sealed record ManagedUsersPage(string Search, string Status, int Page, long TotalCount,
    IReadOnlyList<ManagedUser> Items)
{
    public const int PageSize = 25;
    public long TotalPages => Math.Max(1, (TotalCount + PageSize - 1) / PageSize);
}
public sealed class UserManagementConflictException(string message) : Exception(message);

public sealed class ManagerUserStore(IConfiguration configuration)
{
    private const string Columns = "Id, COALESCE(Name,''), COALESCE(NickName,''), Email, ISNULL(Manager,0), IsDisabled, DeletedAt, LastLoginAt, AccountRevision, CreatedAt, Avatar, Picture";
    private SqlConnection Connection() => new(configuration.GetConnectionString("DefaultConnection"));

    public async Task<bool> IsManagerAsync(string? email, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;
        await using var db = Connection();
        await db.OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT COUNT(*) FROM dbo.Users WHERE Email=@Email AND Manager=1 AND IsDisabled=0 AND DeletedAt IS NULL", db);
        cmd.Parameters.AddWithValue("@Email", email);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) == 1;
    }

    public async Task<ManagedUsersPage> ReadAsync(string search, string status, int page, CancellationToken ct)
    {
        await using var db = Connection();
        await db.OpenAsync(ct);
        using var transaction = db.BeginTransaction(IsolationLevel.Serializable);
        const string where = " WHERE (@Search='' OR CHARINDEX(@Search,Email)>0 OR CHARINDEX(@Search,Name)>0 OR CHARINDEX(@Search,NickName)>0 OR CONVERT(varchar(20),Id)=@Search) AND (@Status='all' OR (@Status='active' AND IsDisabled=0 AND DeletedAt IS NULL) OR (@Status='disabled' AND IsDisabled=1 AND DeletedAt IS NULL) OR (@Status='deleted' AND DeletedAt IS NOT NULL) OR (@Status='manager' AND Manager=1))";
        await using var cmd = new SqlCommand("SELECT COUNT_BIG(*) FROM dbo.Users" + where, db, transaction);
        cmd.Parameters.AddWithValue("@Search", search);
        cmd.Parameters.AddWithValue("@Status", status);
        long count = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        page = (int)Math.Min(page, Math.Max(1, (count + ManagedUsersPage.PageSize - 1) / ManagedUsersPage.PageSize));
        cmd.CommandText = "SELECT " + Columns + " FROM dbo.Users" + where + " ORDER BY Id DESC OFFSET @Offset ROWS FETCH NEXT 25 ROWS ONLY";
        cmd.Parameters.AddWithValue("@Offset", ((long)page - 1) * ManagedUsersPage.PageSize);
        var users = new List<ManagedUser>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) users.Add(ReadUser(reader));
        await transaction.CommitAsync(ct);
        return new(search, status, page, count, users);
    }

    public async Task<ManagedUserDetail?> DetailAsync(int id, CancellationToken ct)
    {
        await using var db = Connection();
        await db.OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT " + Columns + " FROM dbo.Users WHERE Id=@Id", db);
        cmd.Parameters.AddWithValue("@Id", id);
        ManagedUser user;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return null;
            user = ReadUser(reader);
        }
        cmd.Parameters.AddWithValue("@Email", user.Email);
        cmd.Parameters.AddWithValue("@TextId", id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var activity = new Dictionary<string, long?>();
        foreach (var (label, table, column, query) in new[] {
            ("上傳歌曲", "Songs", "AddedByUserId", "SELECT COUNT_BIG(*) FROM dbo.Songs WHERE AddedByUserId=@Id"),
            ("歌曲留言", "Comments", "UserEmail", "SELECT COUNT_BIG(*) FROM dbo.Comments WHERE UserEmail=@Email"),
            ("許願", "Wish", "UserId", "SELECT COUNT_BIG(*) FROM dbo.Wish WHERE UserId=@Id"),
            ("錯誤回報", "ErrorReports", "UserEmail", "SELECT COUNT_BIG(*) FROM dbo.ErrorReports WHERE UserEmail=@Email"),
            ("合輯", "SongGroup", "UserId", "SELECT COUNT_BIG(*) FROM dbo.SongGroup WHERE UserId=@TextId"),
            ("有效 App 工作階段", "MobileSessions", "UserId", "SELECT COUNT_BIG(*) FROM dbo.MobileSessions WHERE UserId=@Id AND ExpiresAt>SYSUTCDATETIME()") })
        {
            cmd.CommandText = $"SELECT COL_LENGTH('dbo.{table}','{column}')";
            if (await cmd.ExecuteScalarAsync(ct) is null or DBNull) { activity[label] = null; continue; }
            cmd.CommandText = query;
            activity[label] = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        }
        var providers = new List<string>();
        cmd.CommandText = "SELECT OBJECT_ID('dbo.MobileIdentities','U')";
        if (await cmd.ExecuteScalarAsync(ct) is not null and not DBNull)
        {
            cmd.CommandText = "SELECT DISTINCT Provider FROM dbo.MobileIdentities WHERE UserId=@Id ORDER BY Provider";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) providers.Add(reader.GetString(0));
        }
        var events = new List<ManagedUserEvent>();
        cmd.CommandText = "SELECT TOP(50) Action,Reason,ActorUserId,CreatedAt FROM dbo.UserManagementEvents WHERE TargetUserId=@Id ORDER BY Id DESC";
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) events.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetDateTime(3)));
        return new(user, activity, providers, events);
    }

    public async Task ChangeAsync(string actorEmail, int targetId, long revision, string action, string confirmation, string reason, CancellationToken ct)
    {
        if (action is not ("disable" or "enable" or "delete") || string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
            throw new UserManagementConflictException("請選擇有效操作並填寫 1 至 500 字的原因。");
        await using var db = Connection();
        await db.OpenAsync(ct);
        using var transaction = db.BeginTransaction(IsolationLevel.Serializable);
        await using var cmd = new SqlCommand("SELECT Id FROM dbo.Users WITH(UPDLOCK,HOLDLOCK) WHERE Email=@Actor AND Manager=1 AND IsDisabled=0 AND DeletedAt IS NULL", db, transaction);
        cmd.Parameters.AddWithValue("@Actor", actorEmail);
        var actor = await cmd.ExecuteScalarAsync(ct);
        if (actor is null) throw new UserManagementConflictException("目前帳號已無管理權限。");
        cmd.CommandText = "SELECT " + Columns + " FROM dbo.Users WITH(UPDLOCK,HOLDLOCK) WHERE Id=@Id";
        cmd.Parameters.AddWithValue("@Id", targetId);
        ManagedUser target;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) throw new UserManagementConflictException("帳號不存在。");
            target = ReadUser(reader);
        }
        if (target.Id == Convert.ToInt32(actor) || target.Manager)
            throw new UserManagementConflictException("不可停用或刪除自己與 Manager 帳號。");
        if (target.Revision != revision || target.DeletedAt.HasValue)
            throw new UserManagementConflictException("帳號狀態已變更，請重新載入後確認。");
        if (!string.Equals(confirmation, target.Email, StringComparison.Ordinal))
            throw new UserManagementConflictException("確認 Email 與目標帳號不符。");
        if ((action == "disable" && target.IsDisabled) || (action == "enable" && !target.IsDisabled))
            throw new UserManagementConflictException("帳號已是指定狀態，沒有重複執行。");
        cmd.CommandText = "UPDATE dbo.Users SET IsDisabled=@Disabled, DeletedAt=CASE WHEN @Action='delete' THEN SYSUTCDATETIME() ELSE NULL END, AccountRevision=AccountRevision+1 WHERE Id=@Id; INSERT dbo.UserManagementEvents(TargetUserId,ActorUserId,Action,Reason) VALUES(@Id,@ActorId,@Action,@Reason);";
        cmd.Parameters.AddWithValue("@Disabled", action != "enable");
        cmd.Parameters.AddWithValue("@Action", action);
        cmd.Parameters.AddWithValue("@ActorId", actor);
        cmd.Parameters.AddWithValue("@Reason", reason.Trim());
        await cmd.ExecuteNonQueryAsync(ct);
        cmd.CommandText = "IF OBJECT_ID('dbo.MobileSessions','U') IS NOT NULL DELETE FROM dbo.MobileSessions WHERE UserId=@Id";
        await cmd.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private static ManagedUser ReadUser(SqlDataReader reader) => new(reader.GetInt32(0), reader.GetString(1),
        reader.GetString(2), reader.GetString(3), reader.GetBoolean(4), reader.GetBoolean(5),
        reader.IsDBNull(6) ? null : reader.GetDateTime(6), reader.IsDBNull(7) ? null : reader.GetDateTime(7), reader.GetInt64(8),
        reader.IsDBNull(9) ? null : reader.GetDateTime(9),
        reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11));
}
