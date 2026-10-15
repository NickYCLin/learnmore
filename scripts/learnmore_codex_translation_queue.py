#!/usr/bin/env python3
from __future__ import annotations

import argparse
import os
import json
import secrets
import subprocess
import tempfile
from dataclasses import dataclass
from pathlib import Path
from typing import Any


DEFAULT_HOST = os.environ.get("LEARNMORE_SSH_HOST", "localhost")
DEFAULT_SSH_PORT = int(os.environ.get("LEARNMORE_SSH_PORT", "22"))
DEFAULT_SSH_CONFIG = Path(os.environ.get("LEARNMORE_SSH_CONFIG", str(Path.home() / ".config/learnmore/ssh.txt")))
REMOTE_APPSETTINGS = os.environ.get("LEARNMORE_APPSETTINGS", r"D:\Web\LearnMore\appsettings.Local.json")


@dataclass
class SshConfig:
    username: str
    password: str
    host: str
    port: int


class QueueError(RuntimeError):
    pass


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Export/apply LearnMore songs waiting for Codex translation backfill.")
    parser.add_argument("--host", default=DEFAULT_HOST)
    parser.add_argument("--ssh-port", type=int, default=DEFAULT_SSH_PORT)
    parser.add_argument("--ssh-config", type=Path, default=DEFAULT_SSH_CONFIG)

    subparsers = parser.add_subparsers(dest="command", required=True)

    export_parser = subparsers.add_parser("export", help="Export production songs with translation_pending_codex status.")
    export_parser.add_argument("--output", type=Path, required=True)
    export_parser.add_argument("--limit", type=int, default=20)

    apply_parser = subparsers.add_parser("apply", help="Apply Codex translations and verify DB completeness.")
    apply_parser.add_argument("--input", type=Path, required=True)
    apply_parser.add_argument("--dry-run", action="store_true")

    return parser.parse_args()


def load_ssh_config(path: Path, host: str, port: int) -> SshConfig:
    raw = path.read_text(encoding="utf-8")
    mapping: dict[str, str] = {}
    for line in raw.splitlines():
        if "=" not in line:
            continue
        key, value = line.split("=", 1)
        mapping[key.strip()] = value.strip()

    username = mapping.get("username")
    password = mapping.get("password")
    if not username or not password:
        raise QueueError(f"ssh config missing username/password: {path}")

    return SshConfig(username=username, password=password, host=host, port=port)


def run_local(command: list[str], *, input_text: str | None = None) -> str:
    result = subprocess.run(command, input=input_text, text=True, capture_output=True)
    if result.returncode != 0:
        raise QueueError(
            f"command failed ({result.returncode}): {' '.join(command)}\n"
            f"STDOUT:\n{result.stdout}\nSTDERR:\n{result.stderr}")
    return result.stdout


def ssh_base(config: SshConfig) -> list[str]:
    return [
        "sshpass", "-p", config.password,
        "ssh",
        "-o", "StrictHostKeyChecking=no",
        "-o", "PreferredAuthentications=password",
        "-o", "PubkeyAuthentication=no",
        "-p", str(config.port),
        f"{config.username}@{config.host}",
    ]


def scp_base(config: SshConfig) -> list[str]:
    return [
        "sshpass", "-p", config.password,
        "scp",
        "-o", "StrictHostKeyChecking=no",
        "-o", "PreferredAuthentications=password",
        "-o", "PubkeyAuthentication=no",
        "-P", str(config.port),
    ]


def ps_quote(value: str) -> str:
    return "'" + value.replace("'", "''") + "'"


def upload_and_run_ps1(config: SshConfig, script_body: str, remote_name: str, args: str = "") -> str:
    with tempfile.TemporaryDirectory(prefix="learnmore-codex-translation-") as tmpdir:
        local = Path(tmpdir) / remote_name
        local.write_text(script_body, encoding="utf-8")
        remote = f"/C:/Temp/{remote_name}"
        run_local(scp_base(config) + [str(local), f"{config.username}@{config.host}:{remote}"])
        command = f"powershell -ExecutionPolicy Bypass -File C:\\Temp\\{remote_name}"
        if args:
            command += " " + args
        return run_local(ssh_base(config) + [command])


def remote_export_script(limit: int) -> str:
    script = r"""
$ErrorActionPreference = 'Stop'
$config = [System.IO.File]::ReadAllText(__APPSETTINGS__) | ConvertFrom-Json
$connString = $config.ConnectionStrings.DefaultConnection
Add-Type -AssemblyName System.Data

function Invoke-Table($conn, [string]$sql) {
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = $sql
    $adapter = New-Object System.Data.SqlClient.SqlDataAdapter($cmd)
    $table = New-Object System.Data.DataTable
    $null = $adapter.Fill($table)
    return ,$table
}

function Convert-DbValue($value) {
    if ($value -eq [DBNull]::Value) { return $null }
    return $value
}

$conn = New-Object System.Data.SqlClient.SqlConnection($connString)
$conn.Open()
try {
    $songsQuery = @"
SELECT TOP (__LIMIT__) SongID, SongUid, Title, Artist, Performer, YouTubeVideoUrl,
       HighAccuracyStatus, HighAccuracyStatusReason
FROM dbo.Songs
WHERE HighAccuracyStatus = 'translation_pending_codex'
ORDER BY SongID
"@
    $songsTable = Invoke-Table $conn $songsQuery
    $songs = @()

    foreach ($song in $songsTable.Rows) {
        $songUid = $song['SongUid'].ToString()
        if ($songUid -notmatch '^[0-9A-Fa-f-]{36}$') { continue }

        $tableName = "[dbo].[Songs_$songUid]"
        $objectName = "Songs_$songUid"
        $existsQuery = "SELECT COUNT(1) AS TableCount FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = N'$objectName'"
        $existsTable = Invoke-Table $conn $existsQuery
        if ($existsTable.Rows.Count -eq 0 -or [int]$existsTable.Rows[0]['TableCount'] -ne 1) { continue }

        $rowsQuery = @"
SELECT LyricID, TimeStamp, Japanese, Chinese, JapaneseRuby, Roman
FROM $tableName
ORDER BY LyricID
"@
        $rowsTable = Invoke-Table $conn $rowsQuery
        $rows = @()
        foreach ($row in $rowsTable.Rows) {
            $rows += [pscustomobject]@{
                lyricId = [int]$row['LyricID']
                timeStamp = Convert-DbValue $row['TimeStamp']
                japanese = Convert-DbValue $row['Japanese']
                currentChinese = Convert-DbValue $row['Chinese']
                japaneseRuby = Convert-DbValue $row['JapaneseRuby']
                roman = Convert-DbValue $row['Roman']
            }
        }

        $missingRows = @($rows | Where-Object {
            [string]::IsNullOrWhiteSpace([string]$_.currentChinese) -or [string]$_.currentChinese -eq '翻譯中...'
        })

        $songs += [pscustomobject]@{
            songUid = $songUid
            songId = [int]$song['SongID']
            title = Convert-DbValue $song['Title']
            artist = Convert-DbValue $song['Artist']
            performer = Convert-DbValue $song['Performer']
            youTubeVideoUrl = Convert-DbValue $song['YouTubeVideoUrl']
            statusReason = Convert-DbValue $song['HighAccuracyStatusReason']
            totalRows = $rows.Count
            missingChineseRows = $missingRows.Count
            rows = $rows
        }
    }

    [pscustomobject]@{
        exportedAt = (Get-Date).ToUniversalTime().ToString("o")
        status = "translation_pending_codex"
        songs = $songs
    } | ConvertTo-Json -Depth 8
}
finally {
    $conn.Dispose()
}
""".strip()
    return script.replace("__APPSETTINGS__", ps_quote(REMOTE_APPSETTINGS)).replace("__LIMIT__", str(max(1, limit)))


def remote_apply_script() -> str:
    script = r"""
param(
    [Parameter(Mandatory=$true)][string]$InputJsonPath,
    [string]$DryRun = 'false'
)

$ErrorActionPreference = 'Stop'
$dryRunFlag = $DryRun -match '^(?i:\$?true|1)$'
$InputJsonPath = $InputJsonPath.Trim("'`"")
$config = [System.IO.File]::ReadAllText(__APPSETTINGS__) | ConvertFrom-Json
$payload = [System.IO.File]::ReadAllText($InputJsonPath) | ConvertFrom-Json
if ($null -eq $payload.songs) { throw 'INPUT_MISSING_SONGS' }

$connString = $config.ConnectionStrings.DefaultConnection
$backupRoot = Join-Path 'C:\Temp' ('learnmore_codex_translation_' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$null = New-Item -ItemType Directory -Force -Path $backupRoot
Add-Type -AssemblyName System.Data

function New-Command($conn, $tx, [string]$sql) {
    $cmd = $conn.CreateCommand()
    $cmd.Transaction = $tx
    $cmd.CommandText = $sql
    return $cmd
}

function Add-NVarChar($cmd, [string]$name, $value) {
    $param = $cmd.Parameters.Add($name, [System.Data.SqlDbType]::NVarChar, -1)
    $param.Value = if ($null -eq $value) { [DBNull]::Value } else { [string]$value }
}

function Add-Int($cmd, [string]$name, [int]$value) {
    $param = $cmd.Parameters.Add($name, [System.Data.SqlDbType]::Int)
    $param.Value = $value
}

function Invoke-Table($cmd) {
    $adapter = New-Object System.Data.SqlClient.SqlDataAdapter($cmd)
    $table = New-Object System.Data.DataTable
    $null = $adapter.Fill($table)
    return ,$table
}

function Convert-DbValue($value) {
    if ($value -eq [DBNull]::Value) { return $null }
    return $value
}

function Row-ToObject($row) {
    $object = [ordered]@{}
    foreach ($column in $row.Table.Columns) {
        $object[$column.ColumnName] = Convert-DbValue $row[$column.ColumnName]
    }
    return [pscustomobject]$object
}

function Rows-ToObjects($table) {
    $rows = @()
    foreach ($row in $table.Rows) {
        $rows += Row-ToObject $row
    }
    return $rows
}

function Get-LyricStats($conn, $tx, [string]$tableName) {
    $cmd = New-Command $conn $tx @"
WITH OrderedLyrics AS (
    SELECT LyricID, TimeStamp, Japanese, Chinese, JapaneseRuby, Roman,
           LAG(TimeStamp) OVER (ORDER BY LyricID) AS PrevTime
    FROM $tableName
)
SELECT COUNT(1) AS TotalRows,
       SUM(CASE WHEN Japanese IS NULL OR LTRIM(RTRIM(Japanese)) = '' THEN 1 ELSE 0 END) AS MissingJapanese,
       SUM(CASE WHEN Chinese IS NULL OR LTRIM(RTRIM(Chinese)) = '' OR Chinese = N'翻譯中...' THEN 1 ELSE 0 END) AS MissingChinese,
       SUM(CASE WHEN JapaneseRuby IS NULL OR LTRIM(RTRIM(JapaneseRuby)) = '' THEN 1 ELSE 0 END) AS MissingRuby,
       SUM(CASE WHEN Roman IS NULL OR LTRIM(RTRIM(Roman)) = '' THEN 1 ELSE 0 END) AS MissingRoman,
       SUM(CASE WHEN TimeStamp IS NULL THEN 1 ELSE 0 END) AS NullTimestampRows,
       SUM(CASE WHEN PrevTime IS NOT NULL AND TimeStamp < PrevTime THEN 1 ELSE 0 END) AS OrderBad
FROM OrderedLyrics
"@
    $table = Invoke-Table $cmd
    return Row-ToObject $table.Rows[0]
}

$conn = New-Object System.Data.SqlClient.SqlConnection($connString)
$conn.Open()
$tx = $conn.BeginTransaction()
try {
    $results = @()

    foreach ($song in @($payload.songs)) {
        $songUid = [string]$song.songUid
        if ($songUid -notmatch '^[0-9A-Fa-f-]{36}$') { throw "INVALID_SONG_UID:$songUid" }
        $tableName = "[dbo].[Songs_$songUid]"

        $metaCmd = New-Command $conn $tx @"
SELECT SongID, SongUid, Title, Artist, Performer, YouTubeVideoUrl, HighAccuracyStatus, HighAccuracyStatusReason
FROM dbo.Songs
WHERE SongUid = @SongUid
"@
        Add-NVarChar $metaCmd '@SongUid' $songUid
        $metaTable = Invoke-Table $metaCmd
        if ($metaTable.Rows.Count -ne 1) { throw "SONG_NOT_FOUND:$songUid" }
        $meta = $metaTable.Rows[0]
        if ([string]$meta['HighAccuracyStatus'] -ne 'translation_pending_codex') {
            throw "SONG_NOT_PENDING_CODEX:$songUid"
        }

        $existsCmd = New-Command $conn $tx "SELECT COUNT(1) AS TableCount FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = N'Songs_$songUid'"
        $existsTable = Invoke-Table $existsCmd
        if ($existsTable.Rows.Count -eq 0 -or [int]$existsTable.Rows[0]['TableCount'] -ne 1) {
            throw "LYRIC_TABLE_NOT_FOUND:$songUid"
        }

        $rowsCmd = New-Command $conn $tx "SELECT LyricID, TimeStamp, Japanese, Chinese, JapaneseRuby, Roman FROM $tableName ORDER BY LyricID"
        $rowsTable = Invoke-Table $rowsCmd
        $plainRows = Rows-ToObjects $rowsTable
        $backup = [pscustomobject]@{
            metadata = Row-ToObject $meta
            rows = $plainRows
        }
        $backupPath = Join-Path $backupRoot ($songUid + '.json')
        $backup | ConvertTo-Json -Depth 8 | Set-Content $backupPath -Encoding UTF8

        $missingRows = @($rowsTable.Rows | Where-Object {
            $rowChinese = [string]$_.Item('Chinese')
            [string]::IsNullOrWhiteSpace($rowChinese) -or $rowChinese -eq '翻譯中...'
        })
        $translations = @($song.translations)
        $targetRows = $missingRows
        if ($translations.Count -eq $rowsTable.Rows.Count) {
            $targetRows = @($rowsTable.Rows)
        } elseif ($translations.Count -ne $missingRows.Count) {
            throw "TRANSLATION_COUNT_MISMATCH:$songUid expectedMissing=$($missingRows.Count) expectedFull=$($rowsTable.Rows.Count) actual=$($translations.Count)"
        }

        foreach ($row in $targetRows) {
            $lyricId = [int]$row['LyricID']
            $translationMatches = @($translations | Where-Object { [int]$_.lyricId -eq $lyricId })
            if ($translationMatches.Count -ne 1) { throw "TRANSLATION_ROW_MISMATCH:$songUid lyricId=$lyricId" }
            $translation = $translationMatches[0]
            $currentJapanese = [string](Convert-DbValue $row['Japanese'])
            if ([string]$translation.japanese -ne $currentJapanese) {
                throw "JAPANESE_GUARD_MISMATCH:$songUid lyricId=$lyricId"
            }
            $chinese = [string]$translation.chinese
            if ([string]::IsNullOrWhiteSpace($chinese) -or $chinese -eq '翻譯中...') {
                throw "INVALID_CHINESE:$songUid lyricId=$lyricId"
            }

            $updateCmd = New-Command $conn $tx "UPDATE $tableName SET Chinese = @Chinese WHERE LyricID = @LyricID AND ISNULL(Japanese, N'') = @Japanese"
            Add-NVarChar $updateCmd '@Chinese' $chinese.Trim()
            Add-Int $updateCmd '@LyricID' $lyricId
            Add-NVarChar $updateCmd '@Japanese' $currentJapanese
            $affected = $updateCmd.ExecuteNonQuery()
            if ($affected -ne 1) { throw "UPDATE_GUARD_FAILED:$songUid lyricId=$lyricId" }
        }

        $stats = Get-LyricStats $conn $tx $tableName
        $complete = [int]$stats.TotalRows -gt 0 `
            -and [int]$stats.MissingJapanese -eq 0 `
            -and [int]$stats.MissingChinese -eq 0 `
            -and [int]$stats.MissingRuby -eq 0 `
            -and [int]$stats.MissingRoman -eq 0 `
            -and [int]$stats.NullTimestampRows -eq 0 `
            -and [int]$stats.OrderBad -eq 0

        $timingValidationPending = [string]$meta['HighAccuracyStatusReason'] -match 'timing_validation_pending'
        $statusCmd = New-Command $conn $tx @"
UPDATE dbo.Songs
SET HighAccuracyStatus = @Status,
    HighAccuracyStatusReason = @Reason
WHERE SongUid = @SongUid
  AND HighAccuracyStatus = 'translation_pending_codex'
"@
        Add-NVarChar $statusCmd '@SongUid' $songUid
        if ($complete -and -not $timingValidationPending) {
            Add-NVarChar $statusCmd '@Status' 'high_accuracy_completed'
            Add-NVarChar $statusCmd '@Reason' '已補齊中文翻譯、注音、羅馬拼音並完成欄位檢查。'
        } elseif ($complete -and $timingValidationPending) {
            Add-NVarChar $statusCmd '@Status' 'high_accuracy_needs_review'
            Add-NVarChar $statusCmd '@Reason' '內容欄位已補齊，但秒數尚未通過同影片驗證，需人工確認。'
        } else {
            Add-NVarChar $statusCmd '@Status' 'translation_pending_codex'
            Add-NVarChar $statusCmd '@Reason' '仍有欄位待補齊，等待後台補件。'
        }
        $affectedStatus = $statusCmd.ExecuteNonQuery()
        if ($affectedStatus -ne 1) { throw "STATUS_UPDATE_FAILED:$songUid" }

        $results += [pscustomobject]@{
            songUid = $songUid
            updatedRows = $translations.Count
            dryRun = $dryRunFlag
            wouldComplete = $complete
            backupPath = $backupPath
            stats = $stats
        }
    }

    if ($dryRunFlag) {
        $tx.Rollback()
    } else {
        $tx.Commit()
    }

    [pscustomobject]@{
        dryRun = $dryRunFlag
        backupRoot = $backupRoot
        results = $results
    } | ConvertTo-Json -Depth 8
}
catch {
    try { $tx.Rollback() } catch {}
    throw
}
finally {
    $conn.Dispose()
}
""".strip()
    return script.replace("__APPSETTINGS__", ps_quote(REMOTE_APPSETTINGS))


def command_export(args: argparse.Namespace, config: SshConfig) -> None:
    output = upload_and_run_ps1(
        config,
        remote_export_script(args.limit),
        f"learnmore_codex_translation_export_{secrets.token_hex(4)}.ps1")
    payload = json.loads(output)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({
        "output": str(args.output),
        "songs": len(payload.get("songs", [])),
        "status": payload.get("status"),
    }, ensure_ascii=False))


def command_apply(args: argparse.Namespace, config: SshConfig) -> None:
    if not args.input.exists():
        raise QueueError(f"input file not found: {args.input}")

    remote_input = f"/C:/Temp/learnmore_codex_translation_input_{secrets.token_hex(4)}.json"
    run_local(scp_base(config) + [str(args.input), f"{config.username}@{config.host}:{remote_input}"])
    dry_run_literal = "true" if args.dry_run else "false"
    remote_windows_input = remote_input.replace("/C:/", "C:/").replace("/", "\\")
    output = upload_and_run_ps1(
        config,
        remote_apply_script(),
        f"learnmore_codex_translation_apply_{secrets.token_hex(4)}.ps1",
        args=f"-InputJsonPath {ps_quote(remote_windows_input)} -DryRun {dry_run_literal}")
    print(json.dumps(json.loads(output), ensure_ascii=False, indent=2))


def main() -> int:
    args = parse_args()
    config = load_ssh_config(args.ssh_config, args.host, args.ssh_port)

    if args.command == "export":
        command_export(args, config)
    elif args.command == "apply":
        command_apply(args, config)
    else:
        raise QueueError(f"unknown command: {args.command}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
