#!/usr/bin/env python3
from __future__ import annotations

import argparse
import os
import base64
import importlib.util
import json
import re
import sys
import tempfile
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path
from typing import Any


DEFAULT_API_BASE = os.environ.get("LEARNMORE_API_BASE_URL", "http://localhost:8080")
DEFAULT_SEARCH_JSON = Path("/tmp/learnmore-yoko-shazam-search.json")
DEFAULT_REPORT_JSON = Path("/tmp/learnmore-yoko-fix-report.json")
PROJECT_ROOT = Path(__file__).resolve().parents[1]
API_ENV = PROJECT_ROOT / "LearnMoreAPI/.env"
SMOKE_SCRIPT = PROJECT_ROOT / "scripts/learnmore_live_authenticated_smoke.py"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Backfill empty high-accuracy lyric tables for Yoko Takahashi songs.")
    parser.add_argument("--search-json", type=Path, default=DEFAULT_SEARCH_JSON)
    parser.add_argument("--report-json", type=Path, default=DEFAULT_REPORT_JSON)
    parser.add_argument("--api-base", default=DEFAULT_API_BASE)
    parser.add_argument("--min-score", type=float, default=0.95)
    parser.add_argument("--limit", type=int, default=0)
    parser.add_argument("--uids", nargs="*", default=[])
    parser.add_argument("--skip-uids", nargs="*", default=[])
    parser.add_argument("--skip-existing", action="store_true")
    parser.add_argument("--write", action="store_true")
    return parser.parse_args()


def read_env(path: Path) -> dict[str, str]:
    values: dict[str, str] = {}
    for raw in path.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        values[key.strip()] = value.strip().strip("\"").strip("'")
    return values


def post_api(base_url: str, api_token: str, path: str, payload: dict[str, Any], timeout: float) -> dict[str, Any]:
    request = urllib.request.Request(
        base_url.rstrip("/") + path,
        data=json.dumps(payload, ensure_ascii=False).encode("utf-8"),
        headers={"Content-Type": "application/json", "Authorization": f"Bearer {api_token}"},
    )
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as exc:
        body = exc.read().decode("utf-8", "replace")
        raise RuntimeError(f"{path} HTTP {exc.code}: {body[:1200]}") from exc


def load_smoke_module() -> Any:
    spec = importlib.util.spec_from_file_location("learnmore_smoke", SMOKE_SCRIPT)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Unable to load {SMOKE_SCRIPT}")
    module = importlib.util.module_from_spec(spec)
    sys.modules["learnmore_smoke"] = module
    spec.loader.exec_module(module)
    return module


def remote_ps1_unique(smoke: Any, config: Any, script_body: str) -> str:
    remote_name = f"learnmore_codex_{uuid.uuid4().hex}.ps1"
    remote_posix = f"/C:/Temp/{remote_name}"
    remote_windows = f"C:\\Temp\\{remote_name}"
    with tempfile.TemporaryDirectory(prefix="learnmore-yoko-") as tmpdir:
        local = Path(tmpdir) / remote_name
        local.write_text(script_body, encoding="utf-8")
        smoke.run_local(smoke.scp_base(config) + [str(local), f"{config.username}@{config.host}:{remote_posix}"])
    try:
        return smoke.run_local(
            smoke.ssh_base(config)
            + [f"powershell -ExecutionPolicy Bypass -File {remote_windows}"]
        )
    finally:
        try:
            smoke.run_local(
                smoke.ssh_base(config)
                + [f"powershell -NoProfile -Command \"Remove-Item '{remote_windows}' -Force -ErrorAction SilentlyContinue\""]
            )
        except Exception:
            pass


def select_targets(args: argparse.Namespace, search_results: list[dict[str, Any]]) -> list[dict[str, Any]]:
    wanted = {uid.strip() for uid in args.uids if uid.strip()}
    skipped = {uid.strip() for uid in args.skip_uids if uid.strip()}
    targets: list[dict[str, Any]] = []
    for item in search_results:
        song = item.get("song") or {}
        best = item.get("best") or {}
        uid = str(song.get("SongUid") or "")
        if not uid or uid in skipped:
            continue
        if wanted and uid not in wanted:
            continue
        if not best.get("hasLyrics"):
            continue
        if float(best.get("score") or 0) < args.min_score:
            continue
        targets.append(item)
    if args.limit > 0:
        targets = targets[: args.limit]
    return targets


def existing_row_count(smoke: Any, ssh_config: Any, song_uid: str) -> int:
    script = rf"""
$ErrorActionPreference='Stop'
$config=Get-Content 'D:\Web\LearnMore\appsettings.Local.json' -Raw | ConvertFrom-Json
$connString=$config.ConnectionStrings.DefaultConnection
Add-Type -AssemblyName System.Data
$conn=New-Object System.Data.SqlClient.SqlConnection($connString)
$conn.Open()
try {{
  $cmd=$conn.CreateCommand()
  $cmd.CommandText='SELECT COUNT(*) FROM dbo.[Songs_{song_uid}]'
  [int]$cmd.ExecuteScalar()
}} finally {{
  $conn.Close()
}}
""".strip()
    output = remote_ps1_unique(smoke, ssh_config, script)
    matches = re.findall(r"(?m)^\s*(\d+)\s*$", output)
    if not matches:
        raise RuntimeError(f"Could not parse lyric row count for {song_uid}: {output[:500]}")
    return int(matches[-1])


def chunked(items: list[dict[str, Any]], size: int) -> list[list[dict[str, Any]]]:
    return [items[index : index + size] for index in range(0, len(items), size)]


def build_supplement_rows(base_url: str, api_token: str, song_uid: str, aligned_lines: list[dict[str, Any]]) -> list[dict[str, Any]]:
    request_lines = [
        {"lyricId": index + 1, "japanese": str(line["japanese"]).strip()}
        for index, line in enumerate(aligned_lines)
    ]
    translations: list[dict[str, Any]] = []
    annotations: list[dict[str, Any]] = []
    for line_chunk in chunked(request_lines, 80):
        payload = {"songUid": song_uid, "lines": line_chunk}
        translations.extend(post_api(base_url, api_token, "/v1/translate-lines", payload, timeout=420)["translations"])
        annotations.extend(post_api(base_url, api_token, "/v1/annotate-lines", payload, timeout=420)["annotations"])

    translations_by_id = {int(item["lyricId"]): str(item["chinese"]).strip() for item in translations}
    annotations_by_id = {int(item["lyricId"]): item for item in annotations}
    rows: list[dict[str, Any]] = []
    for index, line in enumerate(aligned_lines, start=1):
        annotation = annotations_by_id[index]
        rows.append(
            {
                "LyricID": index,
                "TimeStamp": round(float(line["start"]), 3),
                "Japanese": str(line["japanese"]).strip(),
                "Chinese": translations_by_id[index],
                "JapaneseRuby": str(annotation["japaneseRuby"]).strip(),
                "Roman": str(annotation["roman"]).strip(),
            }
        )
    return rows


def alignment_metrics(lines: list[dict[str, Any]]) -> dict[str, Any]:
    total = len(lines)
    verified = sum(1 for line in lines if str(line.get("source", "")).startswith("shazam_timed_lyrics_verified"))
    unverified = sum(1 for line in lines if str(line.get("source", "")).startswith("shazam_timed_lyrics_unverified"))
    word = sum(1 for line in lines if line.get("source") == "whisper_word_match")
    segment = sum(1 for line in lines if line.get("source") == "whisper_segment_match")
    fallback = sum(1 for line in lines if line.get("source") == "proportional_fallback")
    good = sum(
        1
        for line in lines
        if str(line.get("source", "")).startswith("shazam_timed_lyrics_verified")
        or line.get("source") == "whisper_word_match"
        or (line.get("source") == "whisper_segment_match" and float(line.get("score") or 0) >= 0.52)
    )
    return {
        "total": total,
        "verifiedTimed": verified,
        "unverifiedTimed": unverified,
        "wordMatches": word,
        "segmentMatches": segment,
        "fallback": fallback,
        "good": good,
        "goodRatio": round(good / max(total, 1), 4),
    }


def validation_failure(metrics: dict[str, Any]) -> str | None:
    if int(metrics["total"]) < 4:
        return "too_few_lines"
    fallback_ratio = int(metrics["fallback"]) / max(int(metrics["total"]), 1)
    if fallback_ratio > 0.15:
        return "too_much_proportional_fallback"
    if float(metrics["goodRatio"]) < 0.65:
        return "low_same_video_verification"
    return None


def status_for_metrics(metrics: dict[str, Any]) -> tuple[str, str]:
    if float(metrics["goodRatio"]) >= 0.75 and int(metrics["unverifiedTimed"]) == 0 and int(metrics["fallback"]) == 0:
        return "high_accuracy_completed", "已使用 Shazam 歌詞並經同影片 Whisper 驗證校正。"
    return "high_accuracy_needs_review", "已補入 Shazam 歌詞並用同影片 Whisper 建立主要時間錨；未達完成門檻，需人工校正秒數。"


def write_remote(smoke: Any, ssh_config: Any, song: dict[str, Any], rows: list[dict[str, Any]], status: str, reason: str, metrics: dict[str, Any]) -> dict[str, Any]:
    uid = str(song["SongUid"])
    payload = {"songUid": uid, "status": status, "reason": reason, "rows": rows, "metrics": metrics}
    encoded = base64.b64encode(json.dumps(payload, ensure_ascii=False).encode("utf-8")).decode("ascii")
    script = rf"""
$ErrorActionPreference='Stop'
$payloadJson=[System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('{encoded}'))
$payload=$payloadJson | ConvertFrom-Json
$config=Get-Content 'D:\Web\LearnMore\appsettings.Local.json' -Raw | ConvertFrom-Json
$connString=$config.ConnectionStrings.DefaultConnection
Add-Type -AssemblyName System.Data
$conn=New-Object System.Data.SqlClient.SqlConnection($connString)
$conn.Open()
$tx=$conn.BeginTransaction()
$identityOn=$false
$backupDir='C:\Temp\learnmore_yoko_source_fix_backup'
New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
$backupPath=Join-Path $backupDir ('{uid}_' + (Get-Date -Format 'yyyyMMdd_HHmmss') + '.json')
try {{
  $metaCmd=$conn.CreateCommand(); $metaCmd.Transaction=$tx
  $metaCmd.CommandText=@"
SELECT TOP 1 SongID, SongUid, Title, Artist, Performer, YouTubeVideoUrl, HighAccuracyStatus, HighAccuracyStatusReason
FROM dbo.Songs WHERE SongUid=@uid
"@
  $null=$metaCmd.Parameters.AddWithValue('@uid','{uid}')
  $reader=$metaCmd.ExecuteReader(); $meta=[ordered]@{{}}
  if($reader.Read()){{ foreach($i in 0..($reader.FieldCount-1)){{ $meta[$reader.GetName($i)] = if($reader.IsDBNull($i)){{ $null }}else{{ $reader.GetValue($i).ToString() }} }} }}
  $reader.Close()

  $lyricsCmd=$conn.CreateCommand(); $lyricsCmd.Transaction=$tx
  $lyricsCmd.CommandText='SELECT LyricID, TimeStamp, Japanese, Chinese, JapaneseRuby, Roman FROM dbo.[Songs_{uid}] ORDER BY LyricID'
  $lyricsReader=$lyricsCmd.ExecuteReader(); $existing=@()
  while($lyricsReader.Read()){{
    $existing += [pscustomobject]@{{
      LyricID=[int]$lyricsReader['LyricID']; TimeStamp=[double]$lyricsReader['TimeStamp'];
      Japanese=if($lyricsReader['Japanese'] -eq [DBNull]::Value){{''}}else{{$lyricsReader['Japanese'].ToString()}};
      Chinese=if($lyricsReader['Chinese'] -eq [DBNull]::Value){{''}}else{{$lyricsReader['Chinese'].ToString()}};
      JapaneseRuby=if($lyricsReader['JapaneseRuby'] -eq [DBNull]::Value){{''}}else{{$lyricsReader['JapaneseRuby'].ToString()}};
      Roman=if($lyricsReader['Roman'] -eq [DBNull]::Value){{''}}else{{$lyricsReader['Roman'].ToString()}}
    }}
  }}
  $lyricsReader.Close()
  [pscustomobject]@{{Meta=$meta; ExistingLyrics=$existing; NewRows=$payload.rows; NewStatus=$payload.status; NewReason=$payload.reason; Metrics=$payload.metrics}} | ConvertTo-Json -Depth 20 | Set-Content -Path $backupPath -Encoding UTF8

  $delete=$conn.CreateCommand(); $delete.Transaction=$tx
  $delete.CommandText='DELETE FROM dbo.[Songs_{uid}]'
  $deleted=$delete.ExecuteNonQuery()

  $identity=$conn.CreateCommand(); $identity.Transaction=$tx
  $identity.CommandText='SET IDENTITY_INSERT dbo.[Songs_{uid}] ON'
  $null=$identity.ExecuteNonQuery(); $identityOn=$true
  foreach($row in $payload.rows){{
    $insert=$conn.CreateCommand(); $insert.Transaction=$tx
    $insert.CommandText='INSERT INTO dbo.[Songs_{uid}] (LyricID, TimeStamp, Japanese, Chinese, JapaneseRuby, Roman) VALUES (@LyricID, @TimeStamp, @Japanese, @Chinese, @JapaneseRuby, @Roman)'
    $p=$insert.Parameters.Add('@LyricID',[System.Data.SqlDbType]::Int); $p.Value=[int]$row.LyricID
    $p=$insert.Parameters.Add('@TimeStamp',[System.Data.SqlDbType]::Float); $p.Value=[double]$row.TimeStamp
    $p=$insert.Parameters.Add('@Japanese',[System.Data.SqlDbType]::NVarChar,-1); $p.Value=[string]$row.Japanese
    $p=$insert.Parameters.Add('@Chinese',[System.Data.SqlDbType]::NVarChar,-1); $p.Value=[string]$row.Chinese
    $p=$insert.Parameters.Add('@JapaneseRuby',[System.Data.SqlDbType]::NVarChar,-1); $p.Value=[string]$row.JapaneseRuby
    $p=$insert.Parameters.Add('@Roman',[System.Data.SqlDbType]::NVarChar,-1); $p.Value=[string]$row.Roman
    $null=$insert.ExecuteNonQuery()
  }}
  $identityOff=$conn.CreateCommand(); $identityOff.Transaction=$tx
  $identityOff.CommandText='SET IDENTITY_INSERT dbo.[Songs_{uid}] OFF'
  $null=$identityOff.ExecuteNonQuery(); $identityOn=$false

  $update=$conn.CreateCommand(); $update.Transaction=$tx
  $update.CommandText='UPDATE dbo.Songs SET HighAccuracyStatus=@Status, HighAccuracyStatusReason=@Reason WHERE SongUid=@SongUid'
  $null=$update.Parameters.AddWithValue('@Status',[string]$payload.status)
  $null=$update.Parameters.AddWithValue('@Reason',[string]$payload.reason)
  $null=$update.Parameters.AddWithValue('@SongUid','{uid}')
  $updated=$update.ExecuteNonQuery()
  $tx.Commit()
  [pscustomobject]@{{ok=$true; backupPath=$backupPath; deleted=$deleted; rows=$payload.rows.Count; updated=$updated}} | ConvertTo-Json -Depth 6 -Compress
}} catch {{
  if($identityOn){{ try {{ $off=$conn.CreateCommand(); $off.Transaction=$tx; $off.CommandText='SET IDENTITY_INSERT dbo.[Songs_{uid}] OFF'; $null=$off.ExecuteNonQuery() }} catch {{}} }}
  try {{ $tx.Rollback() }} catch {{}}
  throw
}} finally {{
  $conn.Close()
}}
""".strip()
    return json.loads(remote_ps1_unique(smoke, ssh_config, script))


def main() -> int:
    args = parse_args()
    api_token = read_env(API_ENV)["LEARNMORE_API_TOKEN"]
    search_results = json.loads(args.search_json.read_text(encoding="utf-8"))
    targets = select_targets(args, search_results)
    smoke = load_smoke_module()
    ssh_config = smoke.load_ssh_config(smoke.DEFAULT_SSH_CONFIG, smoke.DEFAULT_HOST, smoke.DEFAULT_SSH_PORT)

    report: list[dict[str, Any]] = []
    for index, item in enumerate(targets, start=1):
        song = item["song"]
        best = item["best"]
        uid = str(song["SongUid"])
        print(f"PROCESS {index}/{len(targets)} SongID={song['SongID']} Title={song['Title']} UID={uid}", flush=True)
        try:
            if args.skip_existing:
                rows_now = existing_row_count(smoke, ssh_config, uid)
                if rows_now > 0:
                    report.append({"songId": song["SongID"], "uid": uid, "title": song["Title"], "ok": True, "skipped": "already_has_rows", "rows": rows_now})
                    print(f"  SKIP already_has_rows rows={rows_now}", flush=True)
                    continue

            aligned = post_api(
                args.api_base,
                api_token,
                "/v1/transcribe-align-shazam",
                {"songUid": uid, "youtubeUrl": song["Url"], "shazamUrl": best["shazamUrl"], "language": "ja"},
                timeout=2400,
            )
            aligned_lines = [line for line in aligned.get("alignedLines", []) if str(line.get("japanese", "")).strip()]
            metrics = alignment_metrics(aligned_lines)
            failure = validation_failure(metrics)
            if failure:
                report.append({"songId": song["SongID"], "uid": uid, "title": song["Title"], "ok": False, "stage": "validate", "failure": failure, "metrics": metrics})
                print(f"  SKIP {failure} metrics={metrics}", flush=True)
                continue

            if not args.write:
                report.append({"songId": song["SongID"], "uid": uid, "title": song["Title"], "ok": True, "dryRun": True, "rows": len(aligned_lines), "metrics": metrics})
                print(f"  DRY rows={len(aligned_lines)} metrics={metrics}", flush=True)
                continue

            rows = build_supplement_rows(args.api_base, api_token, uid, aligned_lines)
            status, reason = status_for_metrics(metrics)
            db_result = write_remote(smoke, ssh_config, song, rows, status, reason, metrics)
            report.append({"songId": song["SongID"], "uid": uid, "title": song["Title"], "ok": True, "rows": len(rows), "status": status, "metrics": metrics, "backupPath": db_result.get("backupPath")})
            print(f"  OK rows={len(rows)} status={status} metrics={metrics}", flush=True)
            time.sleep(0.25)
        except Exception as exc:
            report.append({"songId": song["SongID"], "uid": uid, "title": song["Title"], "ok": False, "stage": "exception", "error": str(exc)[:1000]})
            print(f"  ERROR {str(exc)[:400]}", flush=True)

    args.report_json.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"REPORT {args.report_json}")
    print(json.dumps({"total": len(report), "ok": sum(1 for item in report if item.get("ok") and not item.get("dryRun")), "dryRun": sum(1 for item in report if item.get("dryRun")), "failed": sum(1 for item in report if not item.get("ok"))}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
