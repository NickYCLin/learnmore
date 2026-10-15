namespace LearnMore.Services;

public static class AudioStemPublicationGuard
{
    public static async Task AcquireTransactionLockAsync(System.Data.SqlClient.SqlConnection connection,
        System.Data.SqlClient.SqlTransaction transaction, CancellationToken cancellationToken)
    {
        using var command = new System.Data.SqlClient.SqlCommand("""
            DECLARE @Result int;
            EXEC @Result = sys.sp_getapplock @Resource = N'LearnMore.AudioStemPublication',
                @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 15000;
            IF @Result < 0 THROW 51000, 'Audio stem publication lock unavailable', 1;
            """, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task<bool> PublishAsync(Func<Task<bool>> songExists, Action publishFiles, CancellationToken cancellationToken = default)
    {
        if (!await songExists()) return false;
        cancellationToken.ThrowIfCancellationRequested();
        publishFiles();
        return true;
    }
}
