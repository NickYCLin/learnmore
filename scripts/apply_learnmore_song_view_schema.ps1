param(
    [Parameter(Mandatory = $true)]
    [string]$ConnectionString
)

$ErrorActionPreference = "Stop"

$schemaSql = @"
CREATE OR ALTER VIEW dbo.V_SongsData
AS
SELECT
    S.SongID,
    S.Title,
    S.Artist,
    S.Performer,
    S.Translator,
    S.TranslationSource,
    S.YouTubeVideoUrl,
    S.ChannelThumbnailUrl,
    S.SongUid,
    S.SongType,
    ISNULL(D.ViewCount, 0) AS ViewCount,
    ISNULL(D.ViewCount_WeeklyGrowth, 0) AS ViewCount_WeeklyGrowth,
    ISNULL(D.ViewCount_MonthlyGrowth, 0) AS ViewCount_MonthlyGrowth,
    ISNULL(D.LikeCount, 0) AS LikeCount,
    ISNULL(D.LikeCount_WeeklyGrowth, 0) AS LikeCount_WeeklyGrowth,
    ISNULL(D.LikeCount_MonthlyGrowth, 0) AS LikeCount_MonthlyGrowth,
    ISNULL(D.UpdatedAt, GETDATE()) AS Expr1,
    S.AddedDate
FROM dbo.Songs AS S
LEFT OUTER JOIN dbo.SongsData AS D ON S.SongUid = D.SongUid;
"@

$connection = [System.Data.SqlClient.SqlConnection]::new($ConnectionString)
$command = $connection.CreateCommand()
$command.CommandText = $schemaSql

try {
    $connection.Open()
    [void]$command.ExecuteNonQuery()
    Write-Host "V_SongsData schema is ready."
}
finally {
    $command.Dispose()
    $connection.Dispose()
}
