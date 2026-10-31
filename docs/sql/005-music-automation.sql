IF OBJECT_ID(N'dbo.MusicAlignmentJobs', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MusicAlignmentJobs (
        JobId uniqueidentifier NOT NULL PRIMARY KEY,
        RequestKey char(64) NOT NULL UNIQUE,
        SongUid nvarchar(64) NOT NULL,
        SnapshotHash char(64) NOT NULL,
        SnapshotJson nvarchar(max) NOT NULL,
        State varchar(32) NOT NULL,
        ReportJson nvarchar(max) NULL,
        FailureCode varchar(100) NULL,
        CreatedAt datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
        UpdatedAt datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
        LeaseUntil datetime2 NULL,
        AppliedAt datetime2 NULL
    );
    CREATE INDEX IX_MusicAlignmentJobs_Queue ON dbo.MusicAlignmentJobs(State, CreatedAt);
END;
IF OBJECT_ID(N'dbo.MusicAlignmentRequests', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MusicAlignmentRequests (
        RequestKey char(64) NOT NULL PRIMARY KEY,
        JobId uniqueidentifier NOT NULL REFERENCES dbo.MusicAlignmentJobs(JobId)
    );
END;
IF OBJECT_ID(N'dbo.MusicAlignmentBackups', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MusicAlignmentBackups (
        JobId uniqueidentifier NOT NULL PRIMARY KEY REFERENCES dbo.MusicAlignmentJobs(JobId),
        BeforeJson nvarchar(max) NOT NULL,
        AfterHash char(64) NOT NULL,
        AppliedAt datetime2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END;
