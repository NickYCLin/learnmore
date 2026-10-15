param(
    [Parameter(Mandatory = $true)]
    [string]$ConnectionString,

    [string]$SongUid,
    [ValidateSet("instrumental", "vocals", "other")]
    [string]$StemKind = "instrumental",
    [string]$PublicUrl,
    [string]$StoragePath,
    [string]$ModelName,
    [string]$Source = "UVR"
)

$ErrorActionPreference = "Stop"

$schemaSql = @"
IF OBJECT_ID('dbo.SongAudioStems', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.SongAudioStems
    (
        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_SongAudioStems PRIMARY KEY,
        SongUid nvarchar(500) NOT NULL,
        StemKind nvarchar(50) NOT NULL,
        PublicUrl nvarchar(2048) NOT NULL,
        StoragePath nvarchar(2048) NULL,
        ModelName nvarchar(200) NULL,
        Source nvarchar(100) NULL,
        CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_SongAudioStems_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt datetime2(0) NOT NULL CONSTRAINT DF_SongAudioStems_UpdatedAt DEFAULT SYSUTCDATETIME()
    );

    CREATE INDEX IX_SongAudioStems_SongUid_StemKind_CreatedAt
        ON dbo.SongAudioStems (SongUid, StemKind, CreatedAt DESC);
END
"@

function Invoke-SqlNonQuery {
    param(
        [string]$Sql,
        [hashtable]$Parameters = @{}
    )

    $connection = [System.Data.SqlClient.SqlConnection]::new($ConnectionString)
    $command = $connection.CreateCommand()
    $command.CommandText = $Sql

    foreach ($name in $Parameters.Keys) {
        $value = $Parameters[$name]
        if ($null -eq $value -or $value -eq "") {
            $value = [DBNull]::Value
        }
        [void]$command.Parameters.AddWithValue($name, $value)
    }

    try {
        $connection.Open()
        [void]$command.ExecuteNonQuery()
    }
    finally {
        $command.Dispose()
        $connection.Dispose()
    }
}

Invoke-SqlNonQuery -Sql $schemaSql
Write-Host "SongAudioStems schema is ready."

if ($SongUid -and $PublicUrl) {
    Invoke-SqlNonQuery -Sql @"
INSERT INTO dbo.SongAudioStems (SongUid, StemKind, PublicUrl, StoragePath, ModelName, Source, UpdatedAt)
VALUES (@SongUid, @StemKind, @PublicUrl, @StoragePath, @ModelName, @Source, SYSUTCDATETIME());
"@ -Parameters @{
        "@SongUid" = $SongUid
        "@StemKind" = $StemKind
        "@PublicUrl" = $PublicUrl
        "@StoragePath" = $StoragePath
        "@ModelName" = $ModelName
        "@Source" = $Source
    }

    Write-Host "Registered $StemKind stem for $SongUid."
}
