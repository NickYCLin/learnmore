SET XACT_ABORT ON;
DECLARE @UserId int = NULL;
DECLARE @Provider varchar(16) = 'google';
DECLARE @Subject varchar(255) = '';
DECLARE @EvidenceReference nvarchar(255) = '';
IF @UserId IS NULL OR @Subject = '' OR @EvidenceReference = '' OR @Provider NOT IN ('google', 'apple')
    THROW 51000, 'Fill in the reviewed user ID, stable provider subject, and ownership evidence reference.', 1;
BEGIN TRANSACTION;
IF NOT EXISTS (SELECT 1 FROM dbo.Users WITH (UPDLOCK, HOLDLOCK) WHERE Id = @UserId)
    THROW 51000, 'Reviewed account no longer exists.', 1;
IF EXISTS (SELECT 1 FROM dbo.MobileIdentities WITH (UPDLOCK, HOLDLOCK)
    WHERE (Provider = @Provider AND Subject = @Subject) OR (UserId = @UserId AND Provider = @Provider))
    THROW 51000, 'An identity is already linked; never overwrite it.', 1;
INSERT dbo.MobileIdentityMigrationApprovals(Provider, Subject, UserId, EvidenceReference, ExpiresAt)
VALUES (@Provider, @Subject, @UserId, @EvidenceReference, DATEADD(minute, 10, SYSUTCDATETIME()));
COMMIT;
