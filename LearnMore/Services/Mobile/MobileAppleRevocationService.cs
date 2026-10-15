using System.Data.SqlClient;
using Microsoft.AspNetCore.DataProtection;

namespace LearnMore.Services.Mobile;

public sealed class MobileAppleRevocationService(IConfiguration configuration, IDataProtectionProvider protection,
    IServiceScopeFactory scopes, ILogger<MobileAppleRevocationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue<bool>("MobileAuth:Enabled")) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var verifier = scope.ServiceProvider.GetRequiredService<IMobileIdentityVerifier>();
                for (var count = 0; count < 100 && await ProcessNextAsync(verifier, stoppingToken); count++) { }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception) { logger.LogWarning("Apple revocation queue unavailable; retrying next minute."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<bool> ProcessNextAsync(IMobileIdentityVerifier verifier, CancellationToken ct)
    {
        await using var db = new SqlConnection(configuration.GetConnectionString("DefaultConnection"));
        await db.OpenAsync(ct);
        var lease = Guid.NewGuid();
        using var transaction = db.BeginTransaction(System.Data.IsolationLevel.ReadCommitted);
        using var claim = MobileAccountStore.Command(db, transaction, """
            ;WITH Pending AS (
                SELECT TOP (1) * FROM dbo.MobileAppleRevocationJobs WITH (UPDLOCK, READPAST, READCOMMITTEDLOCK)
                WHERE NextAttemptAt <= SYSUTCDATETIME() AND (LeaseUntil IS NULL OR LeaseUntil <= SYSUTCDATETIME())
                ORDER BY Id
            )
            UPDATE Pending SET LeaseId = @Lease, LeaseUntil = DATEADD(minute, 5, SYSUTCDATETIME()), Attempts = Attempts + 1
            OUTPUT INSERTED.Id, INSERTED.ProtectedRefreshToken;
            """, ("@Lease", lease));
        long jobId;
        string encrypted;
        using (var rows = await claim.ExecuteReaderAsync(ct))
        {
            if (!await rows.ReadAsync(ct)) return false;
            jobId = rows.GetInt64(0);
            encrypted = rows.GetString(1);
        }
        await transaction.CommitAsync(ct);
        try
        {
            var protector = protection.CreateProtector("LearnMore.Mobile.AppleRefresh.v1");
            await verifier.RevokeAppleAsync(protector.Unprotect(encrypted), ct);
            using var done = MobileAccountStore.Command(db, null,
                "DELETE dbo.MobileAppleRevocationJobs WHERE Id = @Id AND LeaseId = @Lease", ("@Id", jobId), ("@Lease", lease));
            await done.ExecuteNonQueryAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            using var retry = MobileAccountStore.Command(db, null, """
                UPDATE dbo.MobileAppleRevocationJobs SET LeaseId = NULL, LeaseUntil = NULL,
                    NextAttemptAt = DATEADD(minute, CASE WHEN Attempts < 60 THEN Attempts ELSE 60 END, SYSUTCDATETIME())
                WHERE Id = @Id AND LeaseId = @Lease
                """, ("@Id", jobId), ("@Lease", lease));
            await retry.ExecuteNonQueryAsync(ct);
            logger.LogWarning("Apple revocation job {JobId} will retry; account deletion is already committed.", jobId);
        }
        return true;
    }
}
