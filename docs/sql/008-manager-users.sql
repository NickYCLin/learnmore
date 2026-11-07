SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF COL_LENGTH('dbo.Users', 'IsDisabled') IS NULL
    ALTER TABLE dbo.Users ADD IsDisabled bit NOT NULL CONSTRAINT DF_Users_IsDisabled DEFAULT 0;
IF COL_LENGTH('dbo.Users', 'DeletedAt') IS NULL
    ALTER TABLE dbo.Users ADD DeletedAt datetime2(7) NULL;
IF COL_LENGTH('dbo.Users', 'AccountRevision') IS NULL
    ALTER TABLE dbo.Users ADD AccountRevision bigint NOT NULL CONSTRAINT DF_Users_AccountRevision DEFAULT 0;
IF OBJECT_ID('dbo.UserManagementEvents', 'U') IS NULL
    CREATE TABLE dbo.UserManagementEvents (
        Id bigint IDENTITY PRIMARY KEY,
        TargetUserId int NOT NULL,
        ActorUserId int NOT NULL,
        Action varchar(16) NOT NULL,
        Reason nvarchar(500) NOT NULL,
        CreatedAt datetime2(7) NOT NULL DEFAULT SYSUTCDATETIME()
    );
COMMIT TRANSACTION;
