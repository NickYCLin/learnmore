-- Run against the SAME database as DefaultConnection after backing it up.
-- Additive migration. Existing Users/SongGroup tables and IDs remain authoritative.
SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF OBJECT_ID('dbo.MobileIdentities', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.MobileIdentities (
        Provider varchar(16) COLLATE Latin1_General_100_BIN2 NOT NULL,
        Subject varchar(255) COLLATE Latin1_General_100_BIN2 NOT NULL,
        UserId int NOT NULL REFERENCES dbo.Users(Id),
        ProtectedRefreshToken nvarchar(max) NULL,
        CONSTRAINT PK_MobileIdentities PRIMARY KEY (Provider, Subject),
        CONSTRAINT UQ_MobileIdentityUser UNIQUE (UserId, Provider)
    );
END;
IF OBJECT_ID('dbo.MobileSessions', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.MobileSessions (
        TokenHash char(64) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
        UserId int NOT NULL REFERENCES dbo.Users(Id),
        CreatedAt datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
        ExpiresAt datetime2 NOT NULL
    );
    CREATE INDEX IX_MobileSessions_UserId ON dbo.MobileSessions(UserId);
    CREATE INDEX IX_MobileSessions_ExpiresAt ON dbo.MobileSessions(ExpiresAt);
END;
IF OBJECT_ID('dbo.MobileFileDeletionJobs', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.MobileFileDeletionJobs (
        Id bigint IDENTITY PRIMARY KEY,
        FileName nvarchar(255) NOT NULL,
        Kind varchar(16) NOT NULL DEFAULT 'avatar',
        CreatedAt datetime2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END;
IF COL_LENGTH('dbo.MobileFileDeletionJobs', 'Kind') IS NULL
    ALTER TABLE dbo.MobileFileDeletionJobs ADD Kind varchar(16) NOT NULL DEFAULT 'avatar';
COMMIT;
