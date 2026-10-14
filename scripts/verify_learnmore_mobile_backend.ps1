# Compatible with Windows PowerShell 5.1. Read-only; no login or database writes.
param(
    [uri]$BaseUrl = 'https://magicplus-design.serveirc.com/LearnMore/',
    [ValidateRange(1, 120)][int]$TimeoutSeconds = 15
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($BaseUrl.UserInfo -or $BaseUrl.Query -or $BaseUrl.Fragment -or
    ($BaseUrl.Scheme -ne 'https' -and -not ($BaseUrl.Scheme -eq 'http' -and $BaseUrl.IsLoopback))) {
    throw 'Use an HTTPS base URL (HTTP is allowed only on loopback).'
}
$base = $BaseUrl.AbsoluteUri.TrimEnd('/')

function Request([string]$Path, [int]$ExpectedStatus = 200) {
    try {
        $response = Invoke-WebRequest ($base + '/' + $Path) -UseBasicParsing -MaximumRedirection 0 -TimeoutSec $TimeoutSeconds -Headers @{ Accept = 'application/json' }
    } catch {
        $responseProperty = $_.Exception.PSObject.Properties['Response']
        if ($null -ne $responseProperty -and $null -ne $responseProperty.Value) {
            $actual = [int]$responseProperty.Value.StatusCode
            if ($actual -eq $ExpectedStatus) { return }
            throw ($Path + ': HTTP ' + $actual + ', expected ' + $ExpectedStatus)
        }
        throw ($Path + ': ' + $_.Exception.Message)
    }
    if ([int]$response.StatusCode -ne $ExpectedStatus) { throw ($Path + ': unexpected HTTP ' + $response.StatusCode) }
    if ($ExpectedStatus -eq 200 -and [string]$response.Headers['Content-Type'] -notmatch '^application/json\b') {
        throw ($Path + ': response is not JSON')
    }
    $response
}
function Require-Fields($Value, [string[]]$Names, [string]$Path) {
    if ($null -eq $Value) { throw ($Path + ': missing object') }
    foreach ($name in $Names) {
        # PowerShell property access is case-insensitive; the JavaScript app is not.
        if (@($Value.PSObject.Properties.Name) -cnotcontains $name) { throw ($Path + ': missing exact JSON field ' + $name) }
    }
}
function Check-Song($Song, [string]$Path) {
    Require-Fields $Song @('songUid', 'title', 'artist', 'performer', 'videoId') $Path
    if ($Song.songUid -isnot [string] -or $Song.songUid -cnotmatch '^[A-Za-z0-9_-]{1,80}$') { throw ($Path + ': invalid songUid') }
    foreach ($name in @('title', 'artist', 'performer')) {
        if ($Song.$name -isnot [string]) { throw ($Path + ': invalid ' + $name) }
    }
    if ($null -ne $Song.videoId -and ($Song.videoId -isnot [string] -or $Song.videoId -cnotmatch '^[A-Za-z0-9_-]{11}$')) { throw ($Path + ': invalid videoId') }
}

$path = 'api/mobile/v1/status'
$response = Request $path
$status = $response.Content | ConvertFrom-Json
Require-Fields $status @('version') $path
if ($status.version -is [string] -or $status.version -is [bool] -or $status.version -ne 1) { throw ($path + ': expected version 1') }

$path = 'api/mobile/v1/songs'
$response = Request $path
if (-not $response.Content.TrimStart().StartsWith('[')) { throw ($path + ': expected JSON array') }
# Do not wrap this pipeline in @(...): PowerShell 5.1 then nests the JSON array.
$songs = $response.Content | ConvertFrom-Json
if ($null -eq $songs -or @($songs).Count -eq 0) { throw ($path + ': no songs available for verification') }
foreach ($song in $songs) { Check-Song $song $path }
$first = @($songs)[0]
$uid = [Uri]::EscapeDataString($first.songUid)
$path = 'api/mobile/v1/songs/' + $uid
$response = Request $path
$detail = $response.Content | ConvertFrom-Json
Require-Fields $detail @('song', 'lyrics') $path
Check-Song $detail.song $path
if ($detail.song.songUid -cne $first.songUid -or $detail.lyrics -isnot [array]) { throw ($path + ': invalid song detail') }
foreach ($line in $detail.lyrics) {
    Require-Fields $line @('id', 'time', 'japanese', 'ruby', 'roman', 'chinese') $path
    if (($line.id -isnot [int] -and $line.id -isnot [long]) -or
        ($line.time -isnot [int] -and $line.time -isnot [long] -and $line.time -isnot [double] -and $line.time -isnot [decimal]) -or
        [double]::IsNaN($line.time) -or [double]::IsInfinity($line.time) -or $line.time -lt 0) { throw ($path + ': invalid lyric id/time') }
    foreach ($name in @('japanese', 'ruby', 'roman', 'chinese')) {
        if ($line.$name -isnot [string]) { throw ($path + ': invalid lyric ' + $name) }
    }
}
$null = Request 'api/mobile/v1/groups' 401
$null = Request 'api/mobile/v1/songs?favorites=true' 401
Write-Output ('PASS mobile API: ' + @($songs).Count + ' songs; detail and anonymous access verified.')
