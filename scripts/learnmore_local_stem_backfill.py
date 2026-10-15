#!/usr/bin/env python3
"""Generate LearnMore karaoke stems locally and publish them to production.

The script is intentionally resumable. Each song is marked ok/failed in a JSON
state file after processing, so re-running the same command skips completed
songs and can retry failures on demand.
"""

from __future__ import annotations

import argparse
import base64
import filecmp
import json
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path
from typing import Any


DEFAULT_HOST = os.environ.get("LEARNMORE_SSH_HOST", "localhost")
DEFAULT_PORT = os.environ.get("LEARNMORE_SSH_PORT", "22")
DEFAULT_USER = os.environ.get("LEARNMORE_SSH_USER", "")
DEFAULT_PASSWORD_FILE = Path(os.environ.get("LEARNMORE_SSH_CONFIG", str(Path.home() / ".config/learnmore/ssh.txt")))
REMOTE_REGISTER_SCRIPT = r"C:\Temp\learnmore-register-stems.ps1"


REMOTE_REGISTER_SCRIPT_BODY = r'''
param(
    [Parameter(Mandatory = $true)][string]$SongUid,
    [Parameter(Mandatory = $true)][string]$StageDir
)

$ErrorActionPreference = "Stop"
$appRoot = "D:\Web\LearnMore"
$config = Get-Content (Join-Path $appRoot "appsettings.Local.json") -Raw | ConvertFrom-Json
$connectionString = $config.ConnectionStrings.DefaultConnection
$credentialFile = $env:LEARNMORE_NAS_CREDENTIAL_FILE
$nasShare = $env:LEARNMORE_NAS_SHARE
if ($credentialFile -and $nasShare) {
    $credentialLines = Get-Content -LiteralPath $credentialFile | Where-Object { $_.Trim() -ne "" }
    if ($credentialLines.Count -lt 2) { throw "NAS credential file requires username and password." }
    $nasUser = $credentialLines[0].Trim()
    $nasPassword = $credentialLines[1].Trim()
    & net.exe use $nasShare /user:$nasUser $nasPassword /persistent:no | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Cannot connect to the configured NAS share." }
}
$destDir = Join-Path (Join-Path $appRoot "wwwroot\audio-stems") $SongUid
New-Item -ItemType Directory -Force -Path $destDir | Out-Null

$instrumentalSource = Join-Path $StageDir "instrumental.flac"
$vocalsSource = Join-Path $StageDir "vocals.flac"
if (!(Test-Path $instrumentalSource)) { throw "Missing instrumental stem: $instrumentalSource" }
if (!(Test-Path $vocalsSource)) { throw "Missing vocals stem: $vocalsSource" }

Copy-Item $instrumentalSource (Join-Path $destDir "instrumental.flac") -Force
Copy-Item $vocalsSource (Join-Path $destDir "vocals.flac") -Force

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

$connection = [System.Data.SqlClient.SqlConnection]::new($connectionString)
$connection.Open()
try {
    $schemaCommand = $connection.CreateCommand()
    $schemaCommand.CommandText = $schemaSql
    [void]$schemaCommand.ExecuteNonQuery()
    $schemaCommand.Dispose()

    $deleteCommand = $connection.CreateCommand()
    $deleteCommand.CommandText = "DELETE FROM dbo.SongAudioStems WHERE SongUid = @SongUid AND StemKind IN (N'instrumental', N'vocals')"
    [void]$deleteCommand.Parameters.AddWithValue("@SongUid", $SongUid)
    [void]$deleteCommand.ExecuteNonQuery()
    $deleteCommand.Dispose()

    $insertSql = @"
INSERT INTO dbo.SongAudioStems (SongUid, StemKind, PublicUrl, StoragePath, ModelName, Source, UpdatedAt)
VALUES (@SongUid, @StemKind, @PublicUrl, @StoragePath, N'htdemucs', N'local-demucs', SYSUTCDATETIME())
"@

    foreach ($stem in @(
        @{ Kind = "instrumental"; File = "instrumental.flac" },
        @{ Kind = "vocals"; File = "vocals.flac" }
    )) {
        $command = $connection.CreateCommand()
        $command.CommandText = $insertSql
        [void]$command.Parameters.AddWithValue("@SongUid", $SongUid)
        [void]$command.Parameters.AddWithValue("@StemKind", $stem.Kind)
        [void]$command.Parameters.AddWithValue("@PublicUrl", "~/audio-stems/$SongUid/$($stem.File)")
        [void]$command.Parameters.AddWithValue("@StoragePath", (Join-Path $destDir $stem.File))
        [void]$command.ExecuteNonQuery()
        $command.Dispose()
    }
}
finally {
    $connection.Dispose()
}

Remove-Item $StageDir -Recurse -Force -ErrorAction SilentlyContinue
Write-Output "registered=$SongUid"
'''


def load_json(path: Path, default: Any) -> Any:
    if not path.exists():
        return default
    with path.open("r", encoding="utf-8-sig") as handle:
        text = handle.read()
    if not text.strip():
        return default
    return json.loads(text)


def dump_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(path.suffix + ".tmp")
    with tmp.open("w", encoding="utf-8") as handle:
        json.dump(value, handle, ensure_ascii=False, indent=2)
        handle.write("\n")
    tmp.replace(path)


def normalize_songs(raw: Any) -> list[dict[str, str]]:
    if isinstance(raw, dict):
        raw = [raw]
    if not isinstance(raw, list):
        raise ValueError("input must be a JSON array")

    songs: list[dict[str, str]] = []
    for item in raw:
        if not isinstance(item, dict):
            continue
        song_uid = str(item.get("SongUid") or item.get("songUid") or "").strip()
        youtube_url = str(item.get("YouTubeVideoUrl") or item.get("youtubeUrl") or "").strip()
        if not song_uid or not youtube_url:
            continue
        songs.append(
            {
                "songUid": song_uid,
                "youtubeUrl": youtube_url,
                "title": str(item.get("Title") or item.get("title") or "").strip(),
                "artist": str(item.get("Artist") or item.get("artist") or "").strip(),
            }
        )
    return songs


def run(args: list[str], *, timeout: float | None = None, cwd: Path | None = None) -> subprocess.CompletedProcess[str]:
    return subprocess.run(args, cwd=cwd, text=True, capture_output=True, timeout=timeout, check=True)


def run_logged(args: list[str], *, timeout: float | None = None, cwd: Path | None = None) -> None:
    subprocess.run(args, cwd=cwd, timeout=timeout, check=True)


def truncate(value: str, limit: int = 3000) -> str:
    value = value.strip()
    if len(value) <= limit:
        return value
    return value[:limit] + "...[truncated]"


def read_password(path: Path) -> str:
    value = os.getenv("LEARNMORE_SSH_PASSWORD", "").strip()
    if value:
        return value
    text = path.read_text(encoding="utf-8").strip()
    for line in text.splitlines():
        key, separator, raw_value = line.partition("=")
        if separator and key.strip().lower() == "password":
            return raw_value.strip()
    return text


def ssh_base(password: str, args: argparse.Namespace) -> list[str]:
    return [
        "sshpass",
        "-p",
        password,
        "ssh",
        "-p",
        str(args.ssh_port),
        "-o",
        "StrictHostKeyChecking=no",
        f"{args.ssh_user}@{args.ssh_host}",
    ]


def scp_base(password: str, args: argparse.Namespace) -> list[str]:
    return [
        "sshpass",
        "-p",
        password,
        "scp",
        "-P",
        str(args.ssh_port),
        "-o",
        "StrictHostKeyChecking=no",
    ]


def install_remote_register_script(password: str, args: argparse.Namespace, work_dir: Path) -> None:
    script_path = work_dir / "learnmore-register-stems.ps1"
    script_path.write_text(REMOTE_REGISTER_SCRIPT_BODY.strip() + "\n", encoding="utf-8")
    run(scp_base(password, args) + [str(script_path), f"{args.ssh_user}@{args.ssh_host}:C:/Temp/learnmore-register-stems.ps1"])


def powershell_encoded(command: str) -> str:
    return base64.b64encode(command.encode("utf-16le")).decode("ascii")


def download_audio(song: dict[str, str], song_dir: Path, args: argparse.Namespace) -> Path:
    output_template = str(song_dir / "source.%(ext)s")
    command = [
        args.ytdlp_bin,
        "--no-playlist",
        "--extract-audio",
        "--audio-format",
        "wav",
        "--audio-quality",
        "0",
        "--output",
        output_template,
        "--print",
        "after_move:filepath",
        song["youtubeUrl"],
    ]
    if args.ytdlp_cookies:
        command[1:1] = ["--cookies", str(args.ytdlp_cookies)]

    result = run(command, timeout=args.download_timeout)
    candidates = [Path(line.strip()) for line in result.stdout.splitlines() if line.strip()]
    for candidate in reversed(candidates):
        if candidate.exists() and candidate.is_file():
            return candidate

    fallback = song_dir / "source.wav"
    if fallback.exists():
        return fallback
    raise RuntimeError("yt-dlp did not produce an audio file")


def separate_audio(audio_path: Path, song_dir: Path, args: argparse.Namespace) -> tuple[Path, Path]:
    output_dir = song_dir / "demucs"
    command = [
        args.python_bin,
        "-m",
        "demucs",
        "--two-stems",
        "vocals",
        "--name",
        args.model,
        "--segment",
        str(args.segment_seconds),
        "--jobs",
        str(args.jobs),
        "--shifts",
        str(args.shifts),
        "--mp3",
        "--mp3-bitrate",
        "320",
        "--out",
        str(output_dir),
        str(audio_path),
    ]
    if args.device:
        command[3:3] = ["--device", args.device]
    run_logged(command, timeout=args.demucs_timeout)

    candidates = sorted((output_dir / args.model).glob("**/*"))
    audio_candidates = [path for path in candidates if path.is_file() and path.suffix.lower() in {".wav", ".flac", ".mp3"}]
    vocals = next((path for path in audio_candidates if path.stem.casefold() == "vocals"), None)
    instrumental = next(
        (
            path
            for path in audio_candidates
            if path.stem.casefold() in {"no_vocals", "no-vocals", "instrumental"}
        ),
        None,
    )
    if not vocals or not instrumental:
        found = ", ".join(str(path.relative_to(output_dir)) for path in candidates if path.is_file())
        raise RuntimeError(f"demucs did not produce expected stems; found: {found}")
    instrumental_flac = convert_to_flac(instrumental, song_dir / "instrumental.flac", args)
    vocals_flac = convert_to_flac(vocals, song_dir / "vocals.flac", args)
    if filecmp.cmp(instrumental_flac, vocals_flac, shallow=False):
        raise RuntimeError("demucs produced identical instrumental and vocals outputs")
    return instrumental_flac, vocals_flac


def convert_to_flac(source: Path, destination: Path, args: argparse.Namespace) -> Path:
    destination.parent.mkdir(parents=True, exist_ok=True)
    tmp_destination = destination.with_name(destination.name + ".tmp.flac")
    tmp_destination.unlink(missing_ok=True)

    command = [
        "ffmpeg",
        "-y",
        "-hide_banner",
        "-loglevel",
        "error",
    ]
    if source.suffix.lower() == ".wav":
        command += ["-c:a", "pcm_s16le"]
    command += [
        "-i",
        str(source),
        "-af",
        f"loudnorm=I={args.stem_target_lufs}:LRA=11:TP=-1.5",
        "-compression_level",
        "8",
        str(tmp_destination),
    ]
    result = subprocess.run(command, text=True, capture_output=True, timeout=600)
    if result.returncode != 0:
        tmp_destination.unlink(missing_ok=True)
        details = truncate((result.stderr or result.stdout or "").strip(), 1200)
        raise RuntimeError(f"ffmpeg failed converting {source.name} to FLAC with exit {result.returncode}: {details}")
    tmp_destination.replace(destination)
    return destination


def upload_and_register(song_uid: str, instrumental: Path, vocals: Path, password: str, args: argparse.Namespace, work_dir: Path) -> None:
    publish_dir = work_dir / "publish" / song_uid
    publish_dir.mkdir(parents=True, exist_ok=True)
    shutil.copy2(instrumental, publish_dir / "instrumental.flac")
    shutil.copy2(vocals, publish_dir / "vocals.flac")

    remote_stage = f"C:/Temp/learnmore-stems-stage/{song_uid}"
    mkdir_command = f"New-Item -ItemType Directory -Force -Path '{remote_stage}' | Out-Null"
    run(ssh_base(password, args) + ["powershell", "-NoProfile", "-EncodedCommand", powershell_encoded(mkdir_command)])
    run(scp_base(password, args) + [str(publish_dir / "instrumental.flac"), f"{args.ssh_user}@{args.ssh_host}:{remote_stage}/instrumental.flac"])
    run(scp_base(password, args) + [str(publish_dir / "vocals.flac"), f"{args.ssh_user}@{args.ssh_host}:{remote_stage}/vocals.flac"])

    command = f"& '{REMOTE_REGISTER_SCRIPT}' -SongUid '{song_uid}' -StageDir 'C:\\Temp\\learnmore-stems-stage\\{song_uid}'"
    run(ssh_base(password, args) + ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-EncodedCommand", powershell_encoded(command)])


def process_song(song: dict[str, str], password: str, args: argparse.Namespace) -> dict[str, Any]:
    song_uid = song["songUid"]
    song_dir = args.work_dir / "work" / song_uid
    if song_dir.exists() and not args.keep_work:
        shutil.rmtree(song_dir)
    song_dir.mkdir(parents=True, exist_ok=True)

    started = time.time()
    audio_path = download_audio(song, song_dir, args)
    instrumental, vocals = separate_audio(audio_path, song_dir, args)
    upload_and_register(song_uid, instrumental, vocals, password, args, args.work_dir)

    result = {
        "status": "ok",
        "title": song.get("title", ""),
        "artist": song.get("artist", ""),
        "youtubeUrl": song.get("youtubeUrl", ""),
        "finishedAt": time.strftime("%Y-%m-%dT%H:%M:%S%z"),
        "elapsedSeconds": round(time.time() - started, 1),
        "instrumentalBytes": instrumental.stat().st_size,
        "vocalsBytes": vocals.stat().st_size,
    }
    if not args.keep_work:
        shutil.rmtree(song_dir, ignore_errors=True)
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, required=True)
    parser.add_argument("--state-output", type=Path, required=True)
    parser.add_argument("--work-dir", type=Path, default=Path("var/uvr-backfill"))
    parser.add_argument("--python-bin", default="var/uvr-backfill/.venv/bin/python")
    parser.add_argument("--ytdlp-bin", default="yt-dlp")
    parser.add_argument("--ytdlp-cookies", type=Path)
    parser.add_argument("--model", default="htdemucs")
    parser.add_argument("--device", default="")
    parser.add_argument("--segment-seconds", type=float, default=7.0)
    parser.add_argument("--jobs", type=int, default=0)
    parser.add_argument("--shifts", type=int, default=0)
    parser.add_argument("--stem-target-lufs", type=float, default=-12.5)
    parser.add_argument("--download-timeout", type=float, default=900.0)
    parser.add_argument("--demucs-timeout", type=float, default=3600.0)
    parser.add_argument("--sleep-seconds", type=float, default=2.0)
    parser.add_argument("--limit", type=int, default=0)
    parser.add_argument("--retry-failed", action="store_true")
    parser.add_argument("--keep-work", action="store_true")
    parser.add_argument("--ssh-host", default=DEFAULT_HOST)
    parser.add_argument("--ssh-port", default=DEFAULT_PORT)
    parser.add_argument("--ssh-user", default=DEFAULT_USER)
    parser.add_argument("--ssh-password-file", type=Path, default=DEFAULT_PASSWORD_FILE)
    args = parser.parse_args()

    args.work_dir.mkdir(parents=True, exist_ok=True)
    ffmpeg_static_dir = args.work_dir / "ffmpeg-static"
    os.environ["PATH"] = f"{ffmpeg_static_dir}:{Path.home() / '.local' / 'bin'}:{os.environ.get('PATH', '')}"
    songs = normalize_songs(load_json(args.input, []))
    if args.limit > 0:
        songs = songs[: args.limit]

    state = load_json(args.state_output, {"songs": {}})
    state.setdefault("songs", {})
    password = read_password(args.ssh_password_file)
    install_remote_register_script(password, args, args.work_dir)

    processed = 0
    for song in songs:
        song_uid = song["songUid"]
        existing = state["songs"].get(song_uid)
        if existing and existing.get("status") == "ok":
            continue
        if existing and existing.get("status") == "failed" and not args.retry_failed:
            continue

        print(f"[{time.strftime('%Y-%m-%d %H:%M:%S')}] processing {song_uid} {song.get('title', '')}", flush=True)
        state["songs"][song_uid] = {
            "status": "running",
            "title": song.get("title", ""),
            "artist": song.get("artist", ""),
            "youtubeUrl": song.get("youtubeUrl", ""),
            "startedAt": time.strftime("%Y-%m-%dT%H:%M:%S%z"),
        }
        dump_json(args.state_output, state)

        try:
            state["songs"][song_uid] = process_song(song, password, args)
        except Exception as exc:  # noqa: BLE001 - batch jobs should continue
            state["songs"][song_uid] = {
                "status": "failed",
                "title": song.get("title", ""),
                "artist": song.get("artist", ""),
                "youtubeUrl": song.get("youtubeUrl", ""),
                "finishedAt": time.strftime("%Y-%m-%dT%H:%M:%S%z"),
                "error": truncate(str(exc)),
            }
            print(f"failed {song_uid}: {exc}", file=sys.stderr, flush=True)
        dump_json(args.state_output, state)
        processed += 1
        if args.sleep_seconds > 0:
            time.sleep(args.sleep_seconds)

    ok = sum(1 for item in state["songs"].values() if item.get("status") == "ok")
    failed = sum(1 for item in state["songs"].values() if item.get("status") == "failed")
    running = sum(1 for item in state["songs"].values() if item.get("status") == "running")
    print(f"processed this run: {processed}; ok: {ok}; failed: {failed}; running: {running}; state: {args.state_output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
