param(
    [Parameter(Mandatory = $true)]
    [string]$SeedPath,

    [string]$Source = 'codex_batch_alias',

    [string]$ConfigPath = $(if ($env:LEARNMORE_APPSETTINGS) { $env:LEARNMORE_APPSETTINGS } else { 'D:\Web\LearnMore\appsettings.Local.json' }),

    [string]$BackupRoot = 'C:\Temp\learnmore_alias_batch_backups'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Data

if (!(Test-Path $SeedPath)) { throw "Seed file not found: $SeedPath" }
if (!(Test-Path $ConfigPath)) { throw "Config file not found: $ConfigPath" }

$seed = Get-Content $SeedPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($null -eq $seed) { throw 'Seed file is empty or invalid JSON.' }
if ($seed -isnot [System.Array]) { $seed = @($seed) }

New-Item -ItemType Directory -Force -Path $BackupRoot | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$backupPath = Join-Path $BackupRoot ("SongAliases_before_{0}.json" -f $stamp)

$config = Get-Content $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
$connString = $config.ConnectionStrings.DefaultConnection
if ([string]::IsNullOrWhiteSpace($connString)) { throw 'DefaultConnection not found in config.' }

$conn = [System.Data.SqlClient.SqlConnection]::new($connString)
$conn.Open()
$tx = $conn.BeginTransaction()
try {
    $schemaCmd = $conn.CreateCommand()
    $schemaCmd.Transaction = $tx
    $schemaCmd.CommandText = @"
IF OBJECT_ID('dbo.SongAliases', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.SongAliases (
        AliasID INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        SongUid NVARCHAR(50) NOT NULL,
        AliasText NVARCHAR(255) NOT NULL,
        AliasType NVARCHAR(50) NOT NULL,
        Source NVARCHAR(100) NULL,
        CreatedAt DATETIME NOT NULL CONSTRAINT DF_SongAliases_CreatedAt DEFAULT GETDATE()
    );
END;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SongAliases_AliasText' AND object_id = OBJECT_ID('dbo.SongAliases'))
BEGIN
    CREATE INDEX IX_SongAliases_AliasText ON dbo.SongAliases (AliasText) INCLUDE (SongUid, AliasType);
END;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_SongAliases_SongUid' AND object_id = OBJECT_ID('dbo.SongAliases'))
BEGIN
    CREATE INDEX IX_SongAliases_SongUid ON dbo.SongAliases (SongUid);
END;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_SongAliases_SongUid_AliasText' AND object_id = OBJECT_ID('dbo.SongAliases'))
BEGIN
    ;WITH DuplicateAliases AS (
        SELECT AliasID,
               ROW_NUMBER() OVER (PARTITION BY SongUid, AliasText ORDER BY AliasID) AS RowNumber
        FROM dbo.SongAliases
    )
    DELETE FROM DuplicateAliases WHERE RowNumber > 1;

    CREATE UNIQUE INDEX UX_SongAliases_SongUid_AliasText
    ON dbo.SongAliases (SongUid, AliasText)
    WITH (IGNORE_DUP_KEY = ON);
END;
"@
    [void]$schemaCmd.ExecuteNonQuery()

    $backupCmd = $conn.CreateCommand()
    $backupCmd.Transaction = $tx
    $backupCmd.CommandText = "SELECT AliasID, SongUid, AliasText, AliasType, Source, CreatedAt FROM dbo.SongAliases ORDER BY AliasID"
    $adapter = [System.Data.SqlClient.SqlDataAdapter]::new($backupCmd)
    $table = [System.Data.DataTable]::new()
    [void]$adapter.Fill($table)
    $backupRows = foreach ($row in $table.Rows) {
        [pscustomobject]@{
            AliasID = $row.AliasID
            SongUid = [string]$row.SongUid
            AliasText = [string]$row.AliasText
            AliasType = [string]$row.AliasType
            Source = [string]$row.Source
            CreatedAt = $row.CreatedAt
        }
    }
    $backupRows | ConvertTo-Json -Depth 5 | Set-Content -Path $backupPath -Encoding UTF8

    $inserted = 0
    $skipped = 0
    foreach ($item in $seed) {
        $songUid = [string]$item.songUid
        if ([string]::IsNullOrWhiteSpace($songUid)) { throw 'Seed item missing songUid.' }
        foreach ($alias in @($item.aliases)) {
            $aliasText = ([string]$alias.aliasText).Trim()
            $aliasType = ([string]$alias.aliasType).Trim()
            if ([string]::IsNullOrWhiteSpace($aliasText)) { continue }
            if ([string]::IsNullOrWhiteSpace($aliasType)) { $aliasType = 'alternate_title' }

            $cmd = $conn.CreateCommand()
            $cmd.Transaction = $tx
            $cmd.CommandText = @"
IF EXISTS (SELECT 1 FROM dbo.Songs WHERE SongUid = @SongUid)
   AND NOT EXISTS (SELECT 1 FROM dbo.SongAliases WHERE SongUid = @SongUid AND AliasText = @AliasText)
BEGIN
    INSERT INTO dbo.SongAliases (SongUid, AliasText, AliasType, Source)
    VALUES (@SongUid, @AliasText, @AliasType, @Source);
    SELECT CAST(1 AS INT);
END
ELSE
BEGIN
    SELECT CAST(0 AS INT);
END
"@
            [void]$cmd.Parameters.Add('@SongUid', [System.Data.SqlDbType]::NVarChar, 100)
            [void]$cmd.Parameters.Add('@AliasText', [System.Data.SqlDbType]::NVarChar, 255)
            [void]$cmd.Parameters.Add('@AliasType', [System.Data.SqlDbType]::NVarChar, 50)
            [void]$cmd.Parameters.Add('@Source', [System.Data.SqlDbType]::NVarChar, 100)
            $cmd.Parameters['@SongUid'].Value = $songUid
            $cmd.Parameters['@AliasText'].Value = $aliasText
            $cmd.Parameters['@AliasType'].Value = $aliasType
            $cmd.Parameters['@Source'].Value = $Source
            $result = [int]$cmd.ExecuteScalar()
            if ($result -eq 1) { $inserted++ } else { $skipped++ }
        }
    }

    $verifyCmd = $conn.CreateCommand()
    $verifyCmd.Transaction = $tx
    $verifyCmd.CommandText = @"
WITH Flags AS (
  SELECT S.SongUid,
    CASE WHEN EXISTS (SELECT 1 FROM dbo.SongAliases A WHERE A.SongUid = S.SongUid) THEN 1 ELSE 0 END AS HasAnyAlias,
    CASE WHEN EXISTS (SELECT 1 FROM dbo.SongAliases A WHERE A.SongUid = S.SongUid AND A.AliasText LIKE N'%[一-龥]%') THEN 1 ELSE 0 END AS HasCjkAlias
  FROM dbo.Songs S
)
SELECT COUNT(*) AS Songs,
       SUM(HasAnyAlias) AS SongsWithAnyAlias,
       SUM(HasCjkAlias) AS SongsWithCjkAlias,
       SUM(CASE WHEN HasAnyAlias = 0 THEN 1 ELSE 0 END) AS SongsWithoutAnyAlias,
       SUM(CASE WHEN HasCjkAlias = 0 THEN 1 ELSE 0 END) AS SongsWithoutCjkAlias
FROM Flags;
"@
    $reader = $verifyCmd.ExecuteReader()
    [void]$reader.Read()
    $summary = [pscustomobject]@{
        Inserted = $inserted
        Skipped = $skipped
        BackupPath = $backupPath
        Songs = [int]$reader['Songs']
        SongsWithAnyAlias = [int]$reader['SongsWithAnyAlias']
        SongsWithCjkAlias = [int]$reader['SongsWithCjkAlias']
        SongsWithoutAnyAlias = [int]$reader['SongsWithoutAnyAlias']
        SongsWithoutCjkAlias = [int]$reader['SongsWithoutCjkAlias']
    }
    $reader.Close()

    $tx.Commit()
    $summary | ConvertTo-Json -Depth 4
}
catch {
    $tx.Rollback()
    throw
}
finally {
    $conn.Dispose()
}
