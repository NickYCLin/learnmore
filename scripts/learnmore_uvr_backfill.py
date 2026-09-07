#!/usr/bin/env python3
"""Batch-submit LearnMore songs to the LearnMoreAPI UVR separation endpoint.

Input JSON may use either database-style keys (`SongUid`, `YouTubeVideoUrl`) or
API-style keys (`songUid`, `youtubeUrl`). The script writes a JSON state file so
the same command can be safely re-run; completed songs are skipped by default.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any


DEFAULT_API_BASE_URL = os.environ.get("LEARNMORE_API_BASE_URL", "http://localhost:8080")
DEFAULT_API_ENV = Path(__file__).resolve().parents[1] / "LearnMoreAPI" / ".env"


def read_env(path: Path) -> dict[str, str]:
    if not path.exists():
        return {}

    values: dict[str, str] = {}
    for raw_line in path.read_text(encoding="utf-8").splitlines():
        line = raw_line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        values[key.strip()] = value.strip().strip('"').strip("'")
    return values


def load_json(path: Path, default: Any) -> Any:
    if not path.exists():
        return default
    with path.open("r", encoding="utf-8-sig") as handle:
        return json.load(handle)


def dump_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8") as handle:
        json.dump(value, handle, ensure_ascii=False, indent=2)
        handle.write("\n")


def normalize_songs(raw: Any) -> list[dict[str, str]]:
    if isinstance(raw, dict):
        raw = [raw]
    if not isinstance(raw, list):
        raise ValueError("input must be a JSON array of songs")

    songs: list[dict[str, str]] = []
    for item in raw:
        if not isinstance(item, dict):
            raise ValueError("each song must be an object")
        song_uid = str(item.get("SongUid") or item.get("songUid") or "").strip()
        youtube_url = str(item.get("YouTubeVideoUrl") or item.get("youtubeVideoUrl") or item.get("youtubeUrl") or "").strip()
        title = str(item.get("Title") or item.get("title") or "").strip()
        artist = str(item.get("Artist") or item.get("artist") or "").strip()
        if not song_uid or not youtube_url:
            continue
        songs.append({"songUid": song_uid, "youtubeUrl": youtube_url, "title": title, "artist": artist})
    return songs


def post_separate(endpoint: str, api_token: str, song: dict[str, str], model: str, timeout: float) -> dict[str, Any]:
    payload = {"songUid": song["songUid"], "youtubeUrl": song["youtubeUrl"], "model": model}
    request = urllib.request.Request(
        endpoint,
        data=json.dumps(payload, ensure_ascii=False).encode("utf-8"),
        headers={
            "Authorization": f"Bearer {api_token}",
            "Content-Type": "application/json; charset=utf-8",
        },
        method="POST",
    )
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as exc:
        detail = exc.read().decode("utf-8", errors="replace")
        raise RuntimeError(f"HTTP {exc.code}: {detail}") from exc
    except urllib.error.URLError as exc:
        raise RuntimeError(f"request failed: {exc}") from exc


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", required=True, type=Path, help="JSON array of songs to process")
    parser.add_argument("--state-output", required=True, type=Path, help="JSON state file with per-song results")
    parser.add_argument("--api-base-url", default=DEFAULT_API_BASE_URL)
    parser.add_argument("--api-token", default="", help="defaults to LEARNMORE_API_TOKEN in --api-env")
    parser.add_argument("--api-env", type=Path, default=DEFAULT_API_ENV)
    parser.add_argument("--model", default="default", help="passed through to /v1/separate-youtube")
    parser.add_argument("--request-timeout", type=float, default=3600.0)
    parser.add_argument("--sleep-seconds", type=float, default=0.0, help="pause between songs")
    parser.add_argument("--retry-failed", action="store_true", help="retry songs that already have failed status")
    parser.add_argument("--limit", type=int, default=0, help="optional max songs for a dry staged run")
    args = parser.parse_args()

    env = read_env(args.api_env)
    api_token = args.api_token or env.get("LEARNMORE_API_TOKEN") or os.getenv("LEARNMORE_API_TOKEN", "")
    if not api_token:
        raise ValueError("LearnMoreAPI token is required")

    songs = normalize_songs(load_json(args.input, []))
    if args.limit > 0:
        songs = songs[: args.limit]

    endpoint = args.api_base_url.rstrip("/") + "/v1/separate-youtube"
    state = load_json(args.state_output, {"songs": {}})
    state.setdefault("songs", {})

    processed = 0
    for song in songs:
        song_uid = song["songUid"]
        existing = state["songs"].get(song_uid)
        if existing and existing.get("status") == "ok":
            continue
        if existing and existing.get("status") == "failed" and not args.retry_failed:
            continue

        print(f"processing {song_uid} {song.get('title', '')}", file=sys.stderr)
        started_at = time.strftime("%Y-%m-%dT%H:%M:%S%z")
        try:
            result = post_separate(endpoint, api_token, song, args.model, args.request_timeout)
            state["songs"][song_uid] = {
                "status": "ok",
                "title": song.get("title", ""),
                "artist": song.get("artist", ""),
                "startedAt": started_at,
                "finishedAt": time.strftime("%Y-%m-%dT%H:%M:%S%z"),
                "result": result,
            }
        except Exception as exc:  # noqa: BLE001 - operator batch should keep moving
            state["songs"][song_uid] = {
                "status": "failed",
                "title": song.get("title", ""),
                "artist": song.get("artist", ""),
                "startedAt": started_at,
                "finishedAt": time.strftime("%Y-%m-%dT%H:%M:%S%z"),
                "error": str(exc),
            }
        dump_json(args.state_output, state)
        processed += 1
        if args.sleep_seconds > 0:
            time.sleep(args.sleep_seconds)

    ok = sum(1 for item in state["songs"].values() if item.get("status") == "ok")
    failed = sum(1 for item in state["songs"].values() if item.get("status") == "failed")
    print(f"processed this run: {processed}; ok: {ok}; failed: {failed}; state: {args.state_output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
