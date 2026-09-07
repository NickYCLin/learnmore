#!/usr/bin/env python3
from __future__ import annotations

import argparse
import os
import importlib.util
import json
import sys
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any


PROJECT_ROOT = Path(__file__).resolve().parents[1]
SOURCE_FIX_SCRIPT = PROJECT_ROOT / "scripts/learnmore_yoko_source_fix_batch.py"
DEFAULT_REMAINING_JSON = Path("/tmp/learnmore-yoko-remaining-empty.json")
DEFAULT_REPORT_JSON = Path("/tmp/learnmore-yoko-whisper-provisional-report.json")
DEFAULT_API_BASE = os.environ.get("LEARNMORE_API_BASE_URL", "http://localhost:8080")


CONTAMINATION_MARKERS = (
    "sound hodori",
    "サウンドゥ",
    "サウンド ホドリ",
    "호돌이",
    "instagram",
    "youtube",
    "youtu.be",
    "チャンネル登録",
    "ご視聴",
    "視聴ありがとう",
    "subscribe",
    "字幕",
    "翻訳",
    "提供",
)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Create provisional same-video Whisper lyrics for empty Yoko Takahashi tables.")
    parser.add_argument("--remaining-json", type=Path, default=DEFAULT_REMAINING_JSON)
    parser.add_argument("--report-json", type=Path, default=DEFAULT_REPORT_JSON)
    parser.add_argument("--api-base", default=DEFAULT_API_BASE)
    parser.add_argument("--uids", nargs="*", default=[])
    parser.add_argument("--skip-uids", nargs="*", default=[])
    parser.add_argument("--limit", type=int, default=0)
    parser.add_argument("--min-lines", type=int, default=4)
    parser.add_argument("--max-line-seconds", type=float, default=28.0)
    parser.add_argument("--skip-existing", action="store_true")
    parser.add_argument("--write", action="store_true")
    return parser.parse_args()


def load_source_fix_module() -> Any:
    spec = importlib.util.spec_from_file_location("learnmore_yoko_source_fix_batch", SOURCE_FIX_SCRIPT)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Unable to load {SOURCE_FIX_SCRIPT}")
    module = importlib.util.module_from_spec(spec)
    sys.modules["learnmore_yoko_source_fix_batch"] = module
    spec.loader.exec_module(module)
    return module


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


def select_targets(args: argparse.Namespace, songs: list[dict[str, Any]]) -> list[dict[str, Any]]:
    wanted = {uid.strip() for uid in args.uids if uid.strip()}
    skipped = {uid.strip() for uid in args.skip_uids if uid.strip()}
    targets: list[dict[str, Any]] = []
    for song in songs:
        uid = str(song.get("SongUid") or "")
        if not uid or uid in skipped:
            continue
        if wanted and uid not in wanted:
            continue
        targets.append(song)
    if args.limit > 0:
        targets = targets[: args.limit]
    return targets


def is_contaminated(text: str) -> bool:
    lowered = text.lower()
    return any(marker in lowered for marker in CONTAMINATION_MARKERS)


def clean_text(text: str) -> str:
    return " ".join(text.replace("\u3000", " ").split()).strip()


def build_lines_from_transcript(segments: list[dict[str, Any]], max_line_seconds: float) -> tuple[list[dict[str, Any]], dict[str, Any]]:
    lines: list[dict[str, Any]] = []
    rejected = 0
    long_lines = 0
    for segment in segments:
        text = clean_text(str(segment.get("text") or ""))
        if not text:
            rejected += 1
            continue
        start = float(segment.get("start") or 0.0)
        end = float(segment.get("end") or start + 0.5)
        duration = max(0.0, end - start)
        if is_contaminated(text):
            rejected += 1
            continue
        if duration > max_line_seconds:
            long_lines += 1
            rejected += 1
            continue
        if len(text) <= 1:
            rejected += 1
            continue
        if lines and abs(lines[-1]["start"] - start) < 0.15 and lines[-1]["japanese"] == text:
            rejected += 1
            continue
        lines.append(
            {
                "lyricId": len(lines) + 1,
                "japanese": text,
                "start": round(start, 3),
                "end": round(max(end, start + 0.25), 3),
                "source": "same_video_whisper_segment",
                "score": 1.0,
            }
        )
    return lines, {"rawSegments": len(segments), "kept": len(lines), "rejected": rejected, "longRejected": long_lines}


def main() -> int:
    args = parse_args()
    source_fix = load_source_fix_module()
    api_token = source_fix.read_env(source_fix.API_ENV)["LEARNMORE_API_TOKEN"]
    smoke = source_fix.load_smoke_module()
    ssh_config = smoke.load_ssh_config(smoke.DEFAULT_SSH_CONFIG, smoke.DEFAULT_HOST, smoke.DEFAULT_SSH_PORT)
    remaining = json.loads(args.remaining_json.read_text(encoding="utf-8"))["songs"]
    targets = select_targets(args, remaining)

    report: list[dict[str, Any]] = []
    for index, song in enumerate(targets, start=1):
        uid = str(song["SongUid"])
        print(f"PROCESS {index}/{len(targets)} SongID={song['SongID']} Title={song['Title']} UID={uid}", flush=True)
        try:
            if args.skip_existing:
                rows_now = source_fix.existing_row_count(smoke, ssh_config, uid)
                if rows_now > 0:
                    report.append({"songId": song["SongID"], "uid": uid, "title": song["Title"], "ok": True, "skipped": "already_has_rows", "rows": rows_now})
                    print(f"  SKIP already_has_rows rows={rows_now}", flush=True)
                    continue

            transcript = post_api(
                args.api_base,
                api_token,
                "/v1/transcribe-youtube",
                {"songUid": uid, "youtubeUrl": song["Url"], "language": "ja"},
                timeout=2400,
            )
            lines, metrics = build_lines_from_transcript(transcript.get("segments", []), args.max_line_seconds)
            if len(lines) < args.min_lines:
                report.append({"songId": song["SongID"], "uid": uid, "title": song["Title"], "ok": False, "stage": "validate", "failure": "too_few_whisper_lines", "metrics": metrics})
                print(f"  SKIP too_few_whisper_lines metrics={metrics}", flush=True)
                continue
            if not args.write:
                report.append({"songId": song["SongID"], "uid": uid, "title": song["Title"], "ok": True, "dryRun": True, "rows": len(lines), "metrics": metrics})
                print(f"  DRY rows={len(lines)} metrics={metrics}", flush=True)
                continue

            rows = source_fix.build_supplement_rows(args.api_base, api_token, uid, lines)
            reason = "已用同影片 Whisper 產生暫補歌詞與時間軸；尚未找到可靠正式歌詞來源，內容與分行仍需人工抽查。"
            db_result = source_fix.write_remote(
                smoke,
                ssh_config,
                song,
                rows,
                "high_accuracy_needs_review",
                reason,
                {"method": "same_video_whisper_provisional", **metrics},
            )
            report.append({"songId": song["SongID"], "uid": uid, "title": song["Title"], "ok": True, "rows": len(rows), "status": "high_accuracy_needs_review", "metrics": metrics, "backupPath": db_result.get("backupPath")})
            print(f"  OK rows={len(rows)} metrics={metrics}", flush=True)
        except Exception as exc:
            report.append({"songId": song["SongID"], "uid": uid, "title": song["Title"], "ok": False, "stage": "exception", "error": str(exc)[:1000]})
            print(f"  ERROR {str(exc)[:400]}", flush=True)

    args.report_json.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"REPORT {args.report_json}")
    print(json.dumps({"total": len(report), "ok": sum(1 for item in report if item.get("ok") and not item.get("dryRun")), "dryRun": sum(1 for item in report if item.get("dryRun")), "failed": sum(1 for item in report if not item.get("ok"))}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
