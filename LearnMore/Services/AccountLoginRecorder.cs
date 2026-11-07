using System.Data.SqlClient;

namespace LearnMore.Services;

public static class AccountLoginRecorder
{
    public static Task RecordAsync(SqlConnection connection, SqlTransaction? transaction, int userId, string email, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        return RecordCoreAsync(connection, transaction, userId, email, cancellationToken);
    }

    public static Task RecordAsync(SqlConnection connection, SqlTransaction? transaction, int userId, CancellationToken cancellationToken = default)
        => RecordCoreAsync(connection, transaction, userId, null, cancellationToken);

    private static async Task RecordCoreAsync(SqlConnection connection, SqlTransaction? transaction, int userId, string? email, CancellationToken cancellationToken)
    {
        if (userId <= 0) throw new ArgumentOutOfRangeException(nameof(userId));
        await using var command = new SqlCommand(@"
UPDATE dbo.Users SET LastLoginAt = SYSUTCDATETIME()
WHERE Id = @UserId AND (@Email IS NULL OR Email = @Email) AND IsDisabled=0 AND DeletedAt IS NULL", connection, transaction);
        command.Parameters.AddWithValue("@UserId", userId);
        command.Parameters.AddWithValue("@Email", (object?)email ?? DBNull.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("登入帳號不存在或已變更，無法完成登入。");
    }
}
