SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF COL_LENGTH('dbo.Songs', 'HighAccuracyStatus') IS NULL
    ALTER TABLE dbo.Songs ADD HighAccuracyStatus nvarchar(50) NULL;
IF COL_LENGTH('dbo.Songs', 'HighAccuracyStatusReason') IS NULL
    ALTER TABLE dbo.Songs ADD HighAccuracyStatusReason nvarchar(200) NULL;
IF COL_LENGTH('dbo.Songs', 'IsPublished') IS NULL
    ALTER TABLE dbo.Songs ADD IsPublished bit NOT NULL CONSTRAINT DF_Songs_IsPublished DEFAULT (1);

COMMIT TRANSACTION;
