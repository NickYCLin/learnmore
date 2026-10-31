[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$AppSettingsPath,
    [Parameter(Mandatory=$true)][string]$StateDirectory,
    [Parameter(Mandatory=$true)][string]$PythonPath,
    [Parameter(Mandatory=$true)][string]$CodexPath,
    [string]$SqlDataSource = '',
    [string]$ExpectedDatabase = 'language',
    [ValidateRange(1,5)][int]$MaxSongs = 1,
    [ValidateRange(1,32)][int]$BatchSize = 16,
    [ValidateRange(10,600)][int]$CodexTimeoutSeconds = 240,
    [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$cn = $null
$tx = $null
$utf8 = New-Object Text.UTF8Encoding($false)
$processingReason = 'Codex 正在自動補齊中文、讀音與羅馬拼音。'

function Save-Json($path, $value) {
    $temporary = $path + '.tmp'
    [IO.File]::WriteAllText($temporary, (ConvertTo-Json -InputObject $value -Depth 15 -Compress), $utf8)
    Move-Item -LiteralPath $temporary -Destination $path -Force
}
function Record($value) {
    $value.utc = [DateTime]::UtcNow.ToString('o')
    [IO.File]::AppendAllText((Join-Path $StateDirectory 'events.jsonl'),
        (ConvertTo-Json -InputObject $value -Compress) + [Environment]::NewLine, $utf8)
    Save-Json (Join-Path $StateDirectory 'status.json') $value
}
function Command($sql, $parameters) {
    $q = $cn.CreateCommand()
    $q.Transaction = $tx
    $q.CommandTimeout = 30
    $q.CommandText = $sql
    foreach ($key in $parameters.Keys) {
        $v = $parameters[$key]
        if ($null -eq $v) { $v = [DBNull]::Value }
        $null = $q.Parameters.AddWithValue('@' + $key, $v)
    }
    return $q
}
function Query-Json($sql, $parameters) {
    $q = Command $sql $parameters
    try {
        $reader = $q.ExecuteReader()
        $result = ''
        try { while ($reader.Read()) { $result += $reader.GetString(0) } }
        finally { $reader.Close() }
        if ([string]::IsNullOrWhiteSpace($result)) { return '[]' }
        return $result
    } finally { $q.Dispose() }
}
function Execute($sql, $parameters) {
    $q = Command $sql $parameters
    try { return $q.ExecuteNonQuery() } finally { $q.Dispose() }
}
function Hash-Text([string]$value) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($utf8.GetBytes($value)))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}
function Utc-Date($value) {
    if ($value -is [DateTime]) { return $value.ToUniversalTime() }
    return [DateTime]::Parse([string]$value, [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
}
function Missing($value) {
    return [string]::IsNullOrWhiteSpace([string]$value) -or
        [string]$value -in @('翻譯中...', '翻譯中…', '待翻譯', '翻譯待補')
}
function Lyrics-Sql($uid, [bool]$locked) {
    $hint = ''
    if ($locked) { $hint = ' WITH (UPDLOCK,HOLDLOCK)' }
    return 'SELECT LyricID,TimeStamp,Japanese,Chinese,JapaneseRuby,Roman FROM dbo.[Songs_' +
        $uid + ']' + $hint + ' ORDER BY LyricID FOR JSON PATH,INCLUDE_NULL_VALUES'
}
function Master-Sql([bool]$locked) {
    $hint = ''
    if ($locked) { $hint = ' WITH (UPDLOCK,HOLDLOCK)' }
    return 'SELECT SongUid,HighAccuracyStatus,HighAccuracyStatusReason FROM dbo.Songs' +
        $hint + ' WHERE SongUid=@Uid FOR JSON PATH,INCLUDE_NULL_VALUES'
}
function Run-Transformer($beforePath, $outputPath) {
    # Python launches the native Codex executable, with tools disabled and JSON schema enforced.
    $helper = Join-Path $PSScriptRoot 'learnmore_codex_backfill.py'
    $raw = & $PythonPath $helper --snapshot $beforePath --output $outputPath --codex $CodexPath `
        --batch-size $BatchSize --timeout $CodexTimeoutSeconds 2>$null
    $code = $LASTEXITCODE
    $answer = ($raw | Out-String) | ConvertFrom-Json
    if ($code -ne 0 -or -not $answer.ok) {
        $failure = [string]$answer.error
        if ($failure -cnotmatch '^[a-z_]{1,80}$') { $failure = 'transformer_failed' }
        throw $failure
    }
}

try {
    $null = New-Item -ItemType Directory -Path $StateDirectory -Force
    if ($SelfTest) {
        $probeRoot = Join-Path $StateDirectory 'probe'
        $null = New-Item -ItemType Directory -Path $probeRoot -Force
        $inputPath = Join-Path $probeRoot 'input.json'
        $resultPath = Join-Path $probeRoot 'result.json'
        $fixture = '{"songUid":"00000000-0000-0000-0000-000000000000","lyrics":[{"LyricID":1,"Japanese":"今日はいい天気です。","Chinese":"","JapaneseRuby":"","Roman":""}]}'
        [IO.File]::WriteAllText($inputPath,$fixture,$utf8)
        Run-Transformer $inputPath $resultPath
        $proof = Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($proof.needsReview -or @($proof.patches).Count -ne 1) { throw 'probe_incomplete' }
        $scriptHash = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
        $helperHash = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'learnmore_codex_backfill.py') -Algorithm SHA256).Hash
        Save-Json (Join-Path $probeRoot 'receipt.json') @{ ok=$true; scriptHash=$scriptHash; helperHash=$helperHash; utc=[DateTime]::UtcNow.ToString('o') }
        Record @{ outcome='probe_passed'; rows=1 }
        exit 0
    }
    $cooldownPath = Join-Path $StateDirectory 'cooldown.json'
    if (Test-Path -LiteralPath $cooldownPath) {
        $cooldown = Get-Content -LiteralPath $cooldownPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ((Utc-Date $cooldown.until) -gt [DateTime]::UtcNow) {
            Record @{ outcome='cooldown'; nextRetryUtc=$cooldown.until }
            exit 0
        }
    }
    $configuration = Get-Content -LiteralPath $AppSettingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $builder = New-Object Data.SqlClient.SqlConnectionStringBuilder -ArgumentList ([string]$configuration.ConnectionStrings.DefaultConnection)
    if ($builder.InitialCatalog -cne $ExpectedDatabase) { throw 'unexpected_database' }
    if ($SqlDataSource) { $builder['Data Source'] = $SqlDataSource }
    $builder['Connect Timeout'] = 10
    $cn = New-Object Data.SqlClient.SqlConnection -ArgumentList $builder.ConnectionString
    $cn.Open()
    # Session-owned application lock survives no process crash and requires no schema migration.
    $q = Command "DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=N'LearnMore.CodexBackfill', @LockMode=N'Exclusive', @LockOwner=N'Session', @LockTimeout=0; SELECT @r;" @{}
    try { $lockResult = [int]$q.ExecuteScalar() } finally { $q.Dispose() }
    if ($lockResult -lt 0) { Record @{ outcome='busy' }; exit 0 }
    $limit = $MaxSongs * 50
    $candidates = (Query-Json ("SELECT TOP ($limit) SongUid FROM dbo.Songs WHERE HighAccuracyStatus=N'translation_pending_codex' ORDER BY SongID FOR JSON PATH") @{}) | ConvertFrom-Json
    $processed = 0
    foreach ($candidate in $candidates) {
        if ($processed -ge $MaxSongs) { break }
        $uid = [string]$candidate.SongUid
        if ($uid -cnotmatch '^[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12}$') {
            $null = Execute "UPDATE dbo.Songs SET HighAccuracyStatus=N'high_accuracy_needs_review',HighAccuracyStatusReason=N'歌曲識別碼格式不完整，請確認原資料。' WHERE SongUid=@Uid AND HighAccuracyStatus=N'translation_pending_codex'" @{Uid=$uid}
            continue
        }
        $songRoot = Join-Path $StateDirectory $uid
        $null = New-Item -ItemType Directory -Path $songRoot -Force
        $statePath = Join-Path $songRoot 'retry.json'
        try { $lyricsJson = Query-Json (Lyrics-Sql $uid $false) @{} }
        catch {
            $null = Execute "UPDATE dbo.Songs SET HighAccuracyStatus=N'high_accuracy_needs_review',HighAccuracyStatusReason=N'歌詞資料表或欄位不完整，請確認原資料。' WHERE SongUid=@Uid AND HighAccuracyStatus=N'translation_pending_codex'" @{Uid=$uid}
            Record @{ outcome='needs_review'; songUid=$uid; error='lyrics_schema_unavailable' }
            $processed++
            continue
        }
        [object[]]$sourceRows = ConvertFrom-Json -InputObject $lyricsJson
        $sourceError = ''
        if ($sourceRows.Count -eq 0) { $sourceError = 'lyrics_empty' }
        elseif (@($sourceRows | Where-Object { [string]::IsNullOrWhiteSpace([string]$_.Japanese) }).Count -gt 0) { $sourceError = 'missing_japanese' }
        if ($sourceError) {
            $tx = $cn.BeginTransaction([Data.IsolationLevel]::Serializable)
            $lockedMaster = (Query-Json (Master-Sql $true) @{Uid=$uid}) | ConvertFrom-Json
            $lockedLyrics = Query-Json (Lyrics-Sql $uid $true) @{}
            if ($lockedMaster.HighAccuracyStatus -ceq 'translation_pending_codex' -and $lockedLyrics -ceq $lyricsJson) {
                $null = Execute "UPDATE dbo.Songs SET HighAccuracyStatus=N'high_accuracy_needs_review',HighAccuracyStatusReason=N'尚未取得完整日文歌詞，無法進行文字補件；請先重新辨識或補上原文。' WHERE SongUid=@Uid AND HighAccuracyStatus=N'translation_pending_codex'" @{Uid=$uid}
                $tx.Commit()
                $tx.Dispose()
                $tx = $null
                Record @{ outcome='needs_review'; songUid=$uid; error=$sourceError }
            } else {
                $tx.Rollback()
                $tx.Dispose()
                $tx = $null
                Record @{ outcome='skipped'; songUid=$uid; error='concurrent_lyric_change' }
            }
            $processed++
            continue
        }
        $fingerprint = Hash-Text $lyricsJson
        $attempts = 0
        $state = $null
        if (Test-Path -LiteralPath $statePath) {
            $state = Get-Content -LiteralPath $statePath -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($state.fingerprint -ceq $fingerprint) {
                if ($state.attempts -ge 3) {
                    $null = Execute "UPDATE dbo.Songs SET HighAccuracyStatus=N'high_accuracy_needs_review',HighAccuracyStatusReason=N'Codex 自動補件多次中斷，請檢查工作狀態；未標記完成。' WHERE SongUid=@Uid AND HighAccuracyStatus=N'translation_pending_codex' AND HighAccuracyStatusReason=@Processing" @{Uid=$uid;Processing=$processingReason}
                    continue
                }
                if ((Utc-Date $state.nextRetryUtc) -gt [DateTime]::UtcNow) { continue }
                $attempts = [int]$state.attempts
            }
        }
        $attempts++
        $retry = @{ fingerprint=$fingerprint; attempts=$attempts; nextRetryUtc=[DateTime]::UtcNow.AddMinutes(5 * [math]::Pow(3,$attempts-1)).ToString('o') }
        Save-Json $statePath $retry
        $processed++
        $jobRoot = Join-Path $songRoot ([Guid]::NewGuid().ToString('N'))
        $null = New-Item -ItemType Directory -Path $jobRoot
        $beforePath = Join-Path $jobRoot 'before.json'
        $outputPath = Join-Path $jobRoot 'result.json'
        $master = (Query-Json (Master-Sql $false) @{Uid=$uid}) | ConvertFrom-Json
        if (@($master).Count -ne 1 -or $master.HighAccuracyStatus -cne 'translation_pending_codex') { continue }
        $originalReason = [string]$master.HighAccuracyStatusReason
        if ($null -ne $state -and $state.fingerprint -ceq $fingerprint -and $null -ne $state.originalReason) {
            $retry.originalReason = [string]$state.originalReason
        } else { $retry.originalReason = $originalReason }
        Save-Json $statePath $retry
        # Write SQL-generated JSON directly: PS 5 nested array wrappers must not alter the input.
        $snapshot = '{"songUid":' + (ConvertTo-Json $uid -Compress) + ',"lyrics":' + $lyricsJson + '}'
        [IO.File]::WriteAllText($beforePath, $snapshot, $utf8)
        $source = $snapshot | ConvertFrom-Json
        $committed = $false
        try {
            $n = Execute "UPDATE dbo.Songs SET HighAccuracyStatusReason=@Reason WHERE SongUid=@Uid AND HighAccuracyStatus=N'translation_pending_codex' AND ISNULL(HighAccuracyStatusReason,N'')=@Before" @{Uid=$uid;Reason=$processingReason;Before=$originalReason}
            if ($n -ne 1) { throw 'concurrent_status_change' }
            Record @{ outcome='processing'; songUid=$uid; attempt=$attempts }
            Run-Transformer $beforePath $outputPath
            $result = Get-Content -LiteralPath $outputPath -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($result.version -ne 1 -or $result.songUid -cne $uid -or $result.sourceSha256 -cne (Hash-Text $snapshot)) { throw 'result_source_mismatch' }
            $tx = $cn.BeginTransaction([Data.IsolationLevel]::Serializable)
            $currentMaster = (Query-Json (Master-Sql $true) @{Uid=$uid}) | ConvertFrom-Json
            if ($currentMaster.HighAccuracyStatus -cne 'translation_pending_codex' -or $currentMaster.HighAccuracyStatusReason -cne $processingReason) { throw 'concurrent_status_change' }
            if ((Query-Json (Lyrics-Sql $uid $true) @{}) -cne $lyricsJson) { throw 'concurrent_lyric_change' }
            $expected = @{}
            foreach ($row in $source.lyrics) { $expected[[int]$row.LyricID] = $row }
            $seen = @{}
            $updated = 0
            foreach ($patch in $result.patches) {
                $id = [int]$patch.lyricId
                if ($seen.ContainsKey($id) -or -not $expected.ContainsKey($id)) { throw 'invalid_result_identity' }
                $seen[$id] = $true
                $row = $expected[$id]
                $set = @()
                $parameters = @{ Id=$id }
                foreach ($field in @('Chinese','JapaneseRuby','Roman')) {
                    $property = $patch.PSObject.Properties[$field]
                    if ($null -eq $property) { continue }
                    $value = [string]$property.Value
                    if (Missing $value) { throw 'invalid_backfill_text' }
                    if ($field -eq 'Chinese' -and -not (Missing $row.Chinese)) { throw 'overwrite_not_allowed' }
                    $set += ($field + '=@' + $field)
                    $parameters[$field] = $value
                    $row.$field = $value
                }
                if ($set.Count -gt 0) {
                    if ((Execute ('UPDATE dbo.[Songs_'+$uid+'] SET '+($set -join ',')+' WHERE LyricID=@Id') $parameters) -ne 1) { throw 'unexpected_update_count' }
                    $updated++
                }
            }
            $after = (Query-Json (Lyrics-Sql $uid $false) @{}) | ConvertFrom-Json
            if (@($after).Count -ne $expected.Count) { throw 'readback_count_mismatch' }
            $incomplete = $false
            foreach ($row in $after) {
                $saved = $expected[[int]$row.LyricID]
                if ($null -eq $saved) { throw 'readback_identity_mismatch' }
                foreach ($field in @('Japanese','Chinese','JapaneseRuby','Roman')) {
                    if ($row.$field -cne $saved.$field) { throw 'readback_content_mismatch' }
                }
                if ([double]$row.TimeStamp -ne [double]$saved.TimeStamp) { throw 'timestamp_changed' }
                if ((Missing $row.Chinese) -or (Missing $row.JapaneseRuby) -or (Missing $row.Roman)) { $incomplete=$true }
            }
            $status = 'high_accuracy_needs_review'
            $reason = '中文、讀音與羅馬拼音已自動補齊，秒數仍依待校正流程檢查。'
            $recoveryEnabled = $configuration.HighAccuracyQueueRecoveryEnabled -ne $false
            if ($result.needsReview -or $incomplete) {
                $reason = 'Codex 自動補件仍有語意或讀音疑義，請核對原歌詞；未標記整首完成。'
            } elseif ($retry.originalReason -match 'timing_validation_pending' -and $recoveryEnabled) {
                $status = 'high_accuracy_pending'
                $reason = '中文、讀音與羅馬拼音已自動補齊，等待既有背景工作校正秒數。'
            }
            if ((Execute 'UPDATE dbo.Songs SET HighAccuracyStatus=@Status,HighAccuracyStatusReason=@Reason WHERE SongUid=@Uid' @{Uid=$uid;Status=$status;Reason=$reason}) -ne 1) { throw 'status_update_failed' }
            $tx.Commit()
            $tx = $null
            $committed = $true
            Save-Json (Join-Path $jobRoot 'receipt.json') @{ applied=$true; songUid=$uid; updatedRows=$updated; status=$status; sourceSha256=$result.sourceSha256; utc=[DateTime]::UtcNow.ToString('o') }
            Record @{ outcome='applied'; songUid=$uid; updatedRows=$updated; status=$status }
        } catch {
            if ($tx) { $tx.Rollback(); $tx=$null }
            if ($committed) {
                Record @{ outcome='applied_receipt_failed'; songUid=$uid; error='receipt_write_failed' }
                continue
            }
            $code = [string]$_.Exception.Message
            if ($code -cnotmatch '^[a-z_]{1,80}$') { $code='worker_failed' }
            $failureStatus = 'translation_pending_codex'
            $failureReason = 'Codex 自動補件暫未完成，稍後會重試；原有歌詞與秒數已保留。'
            if ($attempts -ge 3) { $failureStatus='high_accuracy_needs_review'; $failureReason='Codex 自動補件連續失敗，請確認登入、額度或原歌詞內容；未標記完成。' }
            $null = Execute "UPDATE dbo.Songs SET HighAccuracyStatus=@Status,HighAccuracyStatusReason=@Reason WHERE SongUid=@Uid AND HighAccuracyStatus=N'translation_pending_codex' AND HighAccuracyStatusReason=@Processing" @{Uid=$uid;Status=$failureStatus;Reason=$failureReason;Processing=$processingReason}
            Record @{ outcome='failed'; songUid=$uid; error=$code; attempt=$attempts; nextRetryUtc=$retry.nextRetryUtc }
            if ($code -eq 'codex_auth_or_quota') {
                Save-Json $cooldownPath @{ until=[DateTime]::UtcNow.AddMinutes(30).ToString('o') }
                break
            }
        }
    }
    if ($processed -eq 0) { Record @{ outcome='idle'; processedSongs=0 } }
} catch {
    if ($tx) { try { $tx.Rollback() } catch {} }
    # Only exact known codes may leave the probe; never log arbitrary exception messages.
    $errorCode = 'infrastructure_unavailable'
    if ($SelfTest -and $_.Exception.Message -cin @('codex_auth_or_quota','codex_timeout','codex_cli_failed',
        'codex_config_unreadable','codex_tools_not_disabled','codex_output_invalid','probe_incomplete')) {
        $errorCode = $_.Exception.Message
    }
    if (Test-Path -LiteralPath $StateDirectory) { Record @{ outcome='worker_error'; error=$errorCode } }
    exit 2
} finally {
    if ($cn) { $cn.Dispose() }
}
