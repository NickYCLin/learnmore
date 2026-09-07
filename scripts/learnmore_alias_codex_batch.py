#!/usr/bin/env python3
"""Generate Traditional Chinese SongAliases seed JSON with LearnMoreAPI or Codex CLI.

This is an offline/operator tool. It intentionally does not run inside the
LearnMore web app, so Media/Upload and Media/Summon do not call OpenAI APIs at
runtime. The output seed can be reviewed and applied separately.
"""

from __future__ import annotations

import argparse
import os
import json
import re
import subprocess
import sys
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any

CJK_RE = re.compile(r"[\u3400-\u9fff]")
ALLOWED_ALIAS_TYPES = {"chinese_title", "alternate_title"}
DEFAULT_API_BASE_URL = os.environ.get("LEARNMORE_API_BASE_URL", "http://localhost:8080")
DEFAULT_API_ENV = Path(__file__).resolve().parents[1] / "LearnMoreAPI" / ".env"


def load_json(path: Path) -> Any:
    with path.open("r", encoding="utf-8-sig") as handle:
        return json.load(handle)


def dump_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8") as handle:
        json.dump(value, handle, ensure_ascii=False, indent=2)
        handle.write("\n")


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


def normalize_song_rows(raw: Any) -> list[dict[str, str]]:
    if isinstance(raw, dict):
        raw = [raw]
    if not isinstance(raw, list):
        raise ValueError("input must be a JSON array of songs")

    rows: list[dict[str, str]] = []
    for item in raw:
        if not isinstance(item, dict):
            raise ValueError("each song must be an object")
        song_uid = str(item.get("SongUid") or item.get("songUid") or "").strip()
        title = str(item.get("Title") or item.get("title") or "").strip()
        artist = str(item.get("Artist") or item.get("artist") or "").strip()
        performer = str(item.get("Performer") or item.get("performer") or "").strip()
        cover = str(item.get("Cover") or item.get("cover") or "").strip()
        youtube_url = str(item.get("YouTubeVideoUrl") or item.get("youtubeVideoUrl") or "").strip()
        if not song_uid or not title:
            raise ValueError(f"song row requires SongUid and Title: {item!r}")
        rows.append(
            {
                "songUid": song_uid,
                "title": title,
                "artist": artist,
                "performer": performer,
                "cover": cover,
                "youtubeVideoUrl": youtube_url,
            }
        )
    return rows


def build_prompt(songs: list[dict[str, str]]) -> str:
    return "\n".join(
        [
            "You are helping maintain LearnMore song search aliases.",
            "Generate Traditional Chinese search aliases for the songs below.",
            "Rules:",
            "- Output ONLY valid JSON array, no markdown.",
            "- Each item must be: {\"songUid\":\"...\",\"aliases\":[{\"aliasText\":\"...\",\"aliasType\":\"chinese_title\"|\"alternate_title\",\"note\":\"short reason\"}]}",
            "- Preserve the original Songs.Title; never rewrite it.",
            "- Prefer official or common Traditional Chinese song names if known.",
            "- If a song has no established Chinese title, provide useful Traditional Chinese search aliases such as Chinese artist name, song-title + 中文歌詞, or common query phrases; do not invent misleading names.",
            "- At least one aliasText per song must contain CJK characters.",
            "- Keep aliases concise. Use Traditional Chinese, not Simplified Chinese.",
            "Songs:",
            json.dumps(songs, ensure_ascii=False, indent=2),
        ]
    )


def run_codex(prompt: str, codex_bin: str) -> str:
    completed = subprocess.run(
        [codex_bin, "exec", prompt],
        check=False,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        encoding="utf-8",
    )
    if completed.returncode != 0:
        raise RuntimeError(f"codex exited with {completed.returncode}:\n{completed.stdout}")
    return completed.stdout


def post_learnmore_api_chunk(chunk: list[dict[str, str]], endpoint: str, api_token: str) -> list[dict[str, Any]]:
    payload = {
        "songs": [
            {
                "songUid": song["songUid"],
                "title": song["title"],
                "artist": song["artist"],
                "performer": song.get("performer", ""),
                "youtubeUrl": song.get("youtubeVideoUrl", ""),
            }
            for song in chunk
        ]
    }
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
        with urllib.request.urlopen(request, timeout=240) as response:
            body = response.read().decode("utf-8")
    except urllib.error.HTTPError as exc:
        detail = exc.read().decode("utf-8", errors="replace")
        raise RuntimeError(f"LearnMoreAPI returned HTTP {exc.code}: {detail}") from exc
    except urllib.error.URLError as exc:
        raise RuntimeError(f"LearnMoreAPI request failed: {exc}") from exc

    response_json = json.loads(body)
    response_songs = response_json.get("songs")
    if not isinstance(response_songs, list):
        raise ValueError("LearnMoreAPI response missing songs array")
    return response_songs


def build_fallback_alias(song: dict[str, str], reason: str) -> dict[str, Any]:
    prefix = song.get("artist") or song.get("performer") or ""
    alias_text = f"{prefix} {song['title']} 中文歌詞".strip()
    return {
        "songUid": song["songUid"],
        "aliases": [
            {
                "aliasText": alias_text,
                "aliasType": "alternate_title",
                "note": f"LearnMoreAPI alias fallback: {reason[:80]}",
            }
        ],
    }


def run_learnmore_api(songs: list[dict[str, str]], api_base_url: str, api_token: str, batch_size: int) -> list[dict[str, Any]]:
    if not api_token:
        raise ValueError("LearnMoreAPI token is required when --use-learnmore-api is set")

    endpoint = api_base_url.rstrip("/") + "/v1/generate-song-aliases"
    generated: list[dict[str, Any]] = []
    safe_batch_size = max(1, min(batch_size, 50))
    for start in range(0, len(songs), safe_batch_size):
        chunk = songs[start : start + safe_batch_size]
        try:
            generated.extend(post_learnmore_api_chunk(chunk, endpoint, api_token))
        except Exception as exc:  # noqa: BLE001 - operator-facing batch fallback
            print(f"WARN: alias batch {start + 1}-{start + len(chunk)} failed; retrying per song: {exc}", file=sys.stderr)
            for song in chunk:
                try:
                    generated.extend(post_learnmore_api_chunk([song], endpoint, api_token))
                except Exception as single_exc:  # noqa: BLE001 - keep the backfill moving with a conservative alias
                    print(f"WARN: alias fallback for {song['songUid']}: {single_exc}", file=sys.stderr)
                    generated.append(build_fallback_alias(song, str(single_exc)))
        print(f"generated aliases for {min(start + len(chunk), len(songs))}/{len(songs)} songs", file=sys.stderr)

    return generated


def extract_json_array(text: str) -> Any:
    decoder = json.JSONDecoder()
    candidates: list[Any] = []
    for match in re.finditer(r"\[", text):
        try:
            value, _ = decoder.raw_decode(text[match.start() :])
        except json.JSONDecodeError:
            continue
        if isinstance(value, list):
            candidates.append(value)
    if not candidates:
        raise ValueError("could not find a JSON array in Codex output")

    # Codex sometimes emits a full seed array that contains nested aliases arrays.
    # Prefer the top-level shape over an inner aliases array discovered later.
    for value in candidates:
        if value and all(isinstance(item, dict) and ("songUid" in item or "SongUid" in item) for item in value):
            return value
    return candidates[0]


def normalize_alias_text(value: Any) -> str:
    text = str(value or "").strip()
    text = re.sub(r"^[\"'「『《]+|[\"'」』》]+$", "", text).strip()
    text = re.sub(r"\s+", " ", text)
    return text[:255]


def validate_seed(raw_seed: Any, song_uids: set[str]) -> list[dict[str, Any]]:
    if not isinstance(raw_seed, list):
        raise ValueError("Codex seed must be a JSON array")

    normalized: list[dict[str, Any]] = []
    seen_pairs: set[tuple[str, str]] = set()
    for item in raw_seed:
        if not isinstance(item, dict):
            raise ValueError("each seed item must be an object")
        song_uid = str(item.get("songUid") or item.get("SongUid") or "").strip()
        if song_uid not in song_uids:
            raise ValueError(f"unexpected songUid from Codex: {song_uid!r}")
        aliases = item.get("aliases")
        if not isinstance(aliases, list) or not aliases:
            raise ValueError(f"missing aliases for {song_uid}")

        clean_aliases: list[dict[str, str]] = []
        has_cjk = False
        for alias in aliases:
            if not isinstance(alias, dict):
                raise ValueError(f"alias for {song_uid} must be an object")
            alias_text = normalize_alias_text(alias.get("aliasText") or alias.get("AliasText"))
            alias_type = str(alias.get("aliasType") or alias.get("AliasType") or "alternate_title").strip()
            note = str(alias.get("note") or alias.get("Note") or "").strip()
            if alias_type not in ALLOWED_ALIAS_TYPES:
                raise ValueError(f"unsupported aliasType for {song_uid}: {alias_type}")
            if not alias_text:
                continue
            if CJK_RE.search(alias_text):
                has_cjk = True
            pair = (song_uid, alias_text.casefold())
            if pair in seen_pairs:
                continue
            seen_pairs.add(pair)
            clean_aliases.append({"aliasText": alias_text, "aliasType": alias_type, "note": note})

        if not clean_aliases:
            raise ValueError(f"no non-empty aliases for {song_uid}")
        if not has_cjk:
            raise ValueError(f"no CJK alias generated for {song_uid}")
        normalized.append({"songUid": song_uid, "aliases": clean_aliases})
    return normalized


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", required=True, type=Path, help="JSON array of songs needing CJK aliases")
    parser.add_argument("--output", required=True, type=Path, help="validated alias seed JSON path")
    parser.add_argument("--raw-output", type=Path, help="optional raw Codex transcript path")
    parser.add_argument("--codex-bin", default="codex", help="Codex CLI executable")
    parser.add_argument("--from-raw", type=Path, help="skip Codex and validate a saved raw transcript")
    parser.add_argument("--use-learnmore-api", action="store_true", help="generate aliases through LearnMoreAPI instead of local Codex CLI")
    parser.add_argument("--api-base-url", default=DEFAULT_API_BASE_URL)
    parser.add_argument("--api-token", default="", help="LearnMoreAPI bearer token; defaults to LEARNMORE_API_TOKEN in --api-env")
    parser.add_argument("--api-env", type=Path, default=DEFAULT_API_ENV)
    parser.add_argument("--api-batch-size", type=int, default=10, help="songs per LearnMoreAPI request, capped at 50")
    args = parser.parse_args()

    songs = normalize_song_rows(load_json(args.input))
    if args.use_learnmore_api:
        env = read_env(args.api_env)
        api_token = args.api_token or env.get("LEARNMORE_API_TOKEN", "")
        raw_seed = run_learnmore_api(songs, args.api_base_url, api_token, args.api_batch_size)
        if args.raw_output:
            dump_json(args.raw_output, {"songs": raw_seed})
    elif args.from_raw:
        raw_text = args.from_raw.read_text(encoding="utf-8")
        raw_seed = extract_json_array(raw_text)
    else:
        raw_text = run_codex(build_prompt(songs), args.codex_bin)
        if args.raw_output:
            args.raw_output.parent.mkdir(parents=True, exist_ok=True)
            args.raw_output.write_text(raw_text, encoding="utf-8")
        raw_seed = extract_json_array(raw_text)

    seed = validate_seed(raw_seed, {song["songUid"] for song in songs})
    dump_json(args.output, seed)
    print(f"wrote {sum(len(item['aliases']) for item in seed)} aliases for {len(seed)} songs to {args.output}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:  # noqa: BLE001 - operator-facing CLI
        print(f"ERROR: {exc}", file=sys.stderr)
        raise SystemExit(1)
