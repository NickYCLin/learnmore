SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF OBJECT_ID(N'dbo.ManagementWorkEvents', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ManagementWorkEvents (
        EventId bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
        Kind varchar(16) COLLATE Latin1_General_100_BIN2 NOT NULL,
        ItemKey char(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
        ItemId nvarchar(450) NOT NULL,
        EvidenceHash char(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
        Status varchar(16) NOT NULL,
        Note nvarchar(2000) NOT NULL,
        ActorEmail nvarchar(320) NOT NULL,
        UpdatedAtUtc datetime2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT CK_ManagementWorkEvents_Kind CHECK (Kind IN ('report','review')),
        CONSTRAINT CK_ManagementWorkEvents_Status CHECK (Status IN ('pending','in_progress','resolved')),
        CONSTRAINT CK_ManagementWorkEvents_Note CHECK (LEN(LTRIM(RTRIM(Note))) > 0)
    );
    CREATE INDEX IX_ManagementWorkEvents_Item
        ON dbo.ManagementWorkEvents(Kind,ItemKey,EventId DESC);
END;
COMMIT TRANSACTION;
