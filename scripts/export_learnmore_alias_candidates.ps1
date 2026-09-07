param(
    [string]$OutputPath = 'C:\Temp\learnmore_alias_candidates.json',

    [int]$Limit = 50,

    [string]$ConfigPath = $(if ($env:LEARNMORE_APPSETTINGS) { $env:LEARNMORE_APPSETTINGS } else { 'D:\Web\LearnMore\appsettings.Local.json' })
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Data

if (!(Test-Path $ConfigPath)) { throw "Config file not found: $ConfigPath" }

$config = Get-Content $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
$connString = $config.ConnectionStrings.DefaultConnection
if ([string]::IsNullOrWhiteSpace($connString)) { throw 'DefaultConnection not found in config.' }

$conn = [System.Data.SqlClient.SqlConnection]::new($connString)
$conn.Open()
try {
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = @"
SELECT TOP (@Limit)
    S.SongUid,
    S.Title,
    S.Artist,
    CASE WHEN COL_LENGTH('dbo.Songs', 'Performer') IS NOT NULL THEN S.Performer ELSE NULL END AS Performer,
    S.YouTubeVideoUrl
FROM dbo.Songs S
WHERE S.SongUid IS NOT NULL
  AND LTRIM(RTRIM(S.SongUid)) <> ''
  AND S.Title IS NOT NULL
  AND LTRIM(RTRIM(S.Title)) <> ''
  AND NOT EXISTS (
      SELECT 1
      FROM dbo.SongAliases A
      WHERE A.SongUid = S.SongUid
        AND A.AliasText LIKE N'%[一-龥]%'
  )
ORDER BY S.SongID DESC;
"@
    [void]$cmd.Parameters.Add('@Limit', [System.Data.SqlDbType]::Int)
    $cmd.Parameters['@Limit'].Value = [Math]::Max(1, $Limit)

    $adapter = [System.Data.SqlClient.SqlDataAdapter]::new($cmd)
    $table = [System.Data.DataTable]::new()
    [void]$adapter.Fill($table)

    $rows = foreach ($row in $table.Rows) {
        [pscustomobject]@{
            SongUid = [string]$row.SongUid
            Title = [string]$row.Title
            Artist = if ($row.Artist -eq [DBNull]::Value) { '' } else { [string]$row.Artist }
            Performer = if ($row.Performer -eq [DBNull]::Value) { '' } else { [string]$row.Performer }
            YouTubeVideoUrl = if ($row.YouTubeVideoUrl -eq [DBNull]::Value) { '' } else { [string]$row.YouTubeVideoUrl }
        }
    }

    $directory = Split-Path -Parent $OutputPath
    if (![string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    $rows | ConvertTo-Json -Depth 4 | Set-Content -Path $OutputPath -Encoding UTF8
    [pscustomobject]@{
        OutputPath = $OutputPath
        Count = @($rows).Count
    } | ConvertTo-Json -Depth 3
}
finally {
    $conn.Dispose()
}
