#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import re
import sys
import time
import urllib.parse
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any

import learnmore_live_authenticated_smoke as auth


DEFAULT_BASE_URL = "https://magicplus-design.serveirc.com/LearnMore"
DEFAULT_API_BASE_URL = "http://127.0.0.1:18080"
DEFAULT_API_ENV = Path(__file__).resolve().parents[1] / "LearnMoreAPI" / ".env"
MAX_AUTOMATIC_SECONDS = 360
MAX_LRCLIB_DURATION_DELTA_SECONDS = 20
MAX_VERIFIED_LRCLIB_DURATION_DELTA_SECONDS = 3
TITLE_ALIASES = {
    "seishun sick": ["青春病"],
    "michi teyu ku": ["満ちてゆく"],
    "michi teyu ku overflowing": ["満ちてゆく"],
    "yoru no fuchi": ["夜の淵"],
    "seikai": ["正解"],
    "sokkenai": ["そっけない"],
    "soramado": ["空窓"],
    "saihate aini": ["サイハテアイニ"],
    "zenzenzense movie ver": ["前前前世 (movie ver.)", "前前前世 movie ver."],
    "shuntou": ["春灯"],
    "kaishin no ichigeki": ["会心の一撃"],
    "dreamer s high": ["ドリーマーズ・ハイ", "Dreamer's High"],
    "kyoshinsho": ["狭心症"],
    "25kome no senshokutai": ["25コ目の染色体", "25個目の染色体"],
    "yushinron": ["有心論"],
    "ordermade": ["オーダーメイド"],
    "the peak": ["最高到達点"],
    "the peak 最高到達点": ["最高到達点"],
    "turquoise": ["ターコイズ"],
    "turquoise ターコイズ": ["ターコイズ"],
    "saraba": ["サラバ"],
    "saraba サラバ": ["サラバ"],
}


def extract_bracketed_title_artist(raw_title: str) -> tuple[str, str]:
    match = re.search(r"[『「](.+?)[』」]", raw_title)
    if not match:
        return "", ""

    inner = re.sub(r"\s+", " ", match.group(1)).strip()
    if not inner:
        return "", ""

    for separator in ("｜", "|"):
        if separator in inner:
            title, artist = inner.split(separator, 1)
            return title.strip(), normalize_artist_name(artist.strip())

    return inner, ""


def extract_acoustic_performer(raw_title: str) -> str:
    match = re.search(
        r"\bacoustic\s+(?:cover|ver(?:sion)?\.?)\s*[.。]?\s*(.+)$",
        raw_title,
        flags=re.I,
    )
    if not match:
        return ""

    performer = match.group(1).strip()
    performer = re.sub(r"[【\[].*$", "", performer).strip()
    performer = performer.strip("。 .-–—|｜")
    return normalize_artist_name(performer)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Submit a YouTube playlist to LearnMore /Media/webtranscribe.")
    parser.add_argument("--playlist-jsonl", type=Path, required=True)
    parser.add_argument("--result-jsonl", type=Path, required=True)
    parser.add_argument("--base-url", default=DEFAULT_BASE_URL)
    parser.add_argument("--limit", type=int, default=0)
    parser.add_argument("--start", type=int, default=1)
    parser.add_argument("--sleep", type=float, default=2.0)
    parser.add_argument("--timeout", type=int, default=900)
    parser.add_argument("--learnmore-api-base-url", default=DEFAULT_API_BASE_URL)
    parser.add_argument("--learnmore-api-env", type=Path, default=DEFAULT_API_ENV)
    parser.add_argument("--artist", default="", help="Override original artist for every playlist item.")
    parser.add_argument("--performer", default="", help="Override performer for every playlist item. Defaults to artist.")
    parser.add_argument("--skip-summon-over-limit", action="store_true")
    parser.add_argument("--enable-shazam-whisper-fallback", action="store_true")
    parser.add_argument("--only-shazam-whisper-fallback", action="store_true")
    parser.add_argument("--shazam-min-score", type=float, default=0.55)
    return parser.parse_args()


def normalize_title(title: str) -> str:
    extracted_title, _ = extract_bracketed_title_artist(title)
    value = extracted_title or title
    value = re.sub(r"^\s*Fujii\s+Kaze\s*[-–—]\s*", "", value, flags=re.I)
    value = re.sub(r"^\s*Aimyon\s*[-–—]\s*", "", value, flags=re.I)
    value = re.sub(r"^\s*SEKAI\s+NO\s+OWARI\s*(?:[-–—/／|｜:：]|[「『\"“”])\s*", "", value, flags=re.I)
    value = re.sub(r"^\s*世界の終わり\s*(?:[-–—/／|｜:：]|[「『\"“”])\s*", "", value)
    value = re.sub(r"^\s*RADWIMPS\s+feat\.\s+[^-–—]+[-–—]\s*", "", value, flags=re.I)
    value = re.sub(r"^\s*RADWIMPS\s*[-–—]\s*", "", value, flags=re.I)
    value = re.sub(r"^\s*King\s+Gnu\s*[-–—]\s*", "", value, flags=re.I)
    value = re.sub(r"^\s*あいみょん\s*[-–—]\s*", "", value)
    quoted = re.match(r"^\s*あいみょん\s*[「『](.+?)[」』]", value)
    if quoted:
        value = quoted.group(1)
    quoted = re.match(r"^\s*(.+?)[」』\"“”]\s*(?:Music\s+Video\s*)?[（(]Short\s+Ver\.?[)）]\s*$", value, flags=re.I)
    if quoted:
        value = quoted.group(1)
    quoted = re.match(r"^\s*(.+?)[」』\"“”]\s+Short\s+Version\s+PV\b.*$", value, flags=re.I)
    if quoted:
        value = quoted.group(1)
    quoted = re.match(r"^\s*(.+?)[」』\"“”]\s+MV\s*$", value, flags=re.I)
    if quoted:
        value = quoted.group(1)
    quoted = re.match(r"^\s*(.+?)[」』\"“”]\s+.*(?:Lyrics\s+)?MV\s*$", value, flags=re.I)
    if quoted:
        value = quoted.group(1)
    value = re.sub(r"\s*/\s*Official\s+Video\s*$", "", value, flags=re.I)
    value = re.sub(r"\s+Official\s+Audio\s*$", "", value, flags=re.I)
    value = re.sub(r"\s*\[(?:Official\s+(?:Music\s+|Lyric\s+)?Video|Acoustic\s+Music\s+Video|Lyric\s+Video|MV|Official\s+Music\s+Video\s*\(Short\s+ver\\.?\s*\))\]\s*(?:[#（(].*)?$", "", value, flags=re.I)
    value = re.sub(r"\s*[（(](?:Official|Acoustic)\s+(?:Music\s+)?Video[)）]\s*$", "", value, flags=re.I)
    value = re.sub(r"\s*【(?:OFFICIAL\s+)?(?:MUSIC\s+)?VIDEO】\s*$", "", value, flags=re.I)
    value = re.sub(r"\s+Music\s+Video\s*[（(]Short\s+Ver\.?[)）]\s*$", "", value, flags=re.I)
    value = re.sub(r"\s+Music\s+Video\s*$", "", value, flags=re.I)
    value = re.sub(r"\s+Lyrics\s+MV\s*$", "", value, flags=re.I)
    value = re.sub(r"\s*\[(?:the\s+theme\s+song\s+of\s+the\s+movie.+)\]\s*$", "", value, flags=re.I)
    value = value.strip("」』“”‘’\"' ")
    return re.sub(r"\s+", " ", value).strip()


def normalize_artist_name(artist: str) -> str:
    compact = re.sub(r"[\s_\-]+", "", artist).lower()
    if compact in {"aimyon", "あいみょん"}:
        return "あいみょん"
    if compact in {"fujiikaze", "藤井風"}:
        return "藤井風"
    if compact == "radwimps":
        return "RADWIMPS"
    if compact == "kinggnu":
        return "King Gnu"
    if compact in {"sekainoowari", "世界の終わり"}:
        return "SEKAI NO OWARI"
    return artist.strip()


def infer_artist(raw_title: str, override: str = "") -> str:
    if override.strip():
        return normalize_artist_name(override)
    _, bracketed_artist = extract_bracketed_title_artist(raw_title)
    if bracketed_artist:
        return bracketed_artist
    match = re.match(r"^\s*([^-\u2013\u2014|｜/／「『\"“”]+?)\s*(?:-|–|—|/|／|｜|「|『|\"|“|”)\s*", raw_title)
    if match:
        return normalize_artist_name(match.group(1))
    return ""


def infer_performer(raw_title: str, artist: str, override: str = "") -> str:
    if override.strip():
        return normalize_artist_name(override)
    acoustic_performer = extract_acoustic_performer(raw_title)
    if acoustic_performer:
        return acoustic_performer
    return artist or infer_artist(raw_title)


def infer_video_artist(video: dict[str, Any], raw_title: str, override: str = "") -> str:
    if override.strip():
        return normalize_artist_name(override)
    explicit = str(video.get("artist") or video.get("artistHint") or "").strip()
    if explicit:
        return normalize_artist_name(explicit)
    return infer_artist(raw_title)


def infer_video_performer(video: dict[str, Any], raw_title: str, artist: str, override: str = "") -> str:
    if override.strip():
        return normalize_artist_name(override)
    explicit = str(video.get("performer") or "").strip()
    if explicit:
        return normalize_artist_name(explicit)
    return infer_performer(raw_title, artist)


def title_search_terms(title: str) -> list[str]:
    terms = [title]
    terms.append(re.sub(r"\s*\[[^\]]*(?:official|music|video|short|acoustic)[^\]]*\]\s*$", "", title, flags=re.I).strip())
    terms.append(re.sub(r"\s*[（(][^）)]*(?:official|music|video|short|acoustic)[^）)]*[)）]\s*$", "", title, flags=re.I).strip())
    normalized_key = re.sub(r"[^a-z0-9]+", " ", title.lower()).strip()
    for alias in TITLE_ALIASES.get(normalized_key, []):
        terms.append(alias)
    compact_key = normalized_key.replace(" overflowing", "")
    for alias in TITLE_ALIASES.get(compact_key, []):
        terms.append(alias)
    return list(dict.fromkeys(term for term in terms if term))


def comparable_title(value: str) -> str:
    value = normalize_title(value)
    value = value.lower()
    return re.sub(r"[^0-9a-z\u3040-\u309F\u30A0-\u30FF\u4E00-\u9FFF\u3400-\u4DBF]+", "", value)


def load_playlist(path: Path) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    for line in path.read_text(encoding="utf-8").splitlines():
        if line.strip():
            rows.append(json.loads(line))
    return rows


def load_done(path: Path) -> set[str]:
    if not path.exists():
        return set()
    done: set[str] = set()
    for line in path.read_text(encoding="utf-8").splitlines():
        if not line.strip():
            continue
        row = json.loads(line)
        if row.get("status") in {"done", "duplicate", "skipped_duration"}:
            done.add(row["videoId"])
    return done


def append_result(path: Path, row: dict[str, Any]) -> None:
    with path.open("a", encoding="utf-8") as handle:
        handle.write(json.dumps(row, ensure_ascii=False, sort_keys=True) + "\n")


def read_env(path: Path) -> dict[str, str]:
    values: dict[str, str] = {}
    if not path.exists():
        return values
    for raw in path.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        values[key.strip()] = value.strip().strip("\"'")
    return values


def login(base_url: str) -> auth.SessionClient:
    config = auth.load_ssh_config(auth.DEFAULT_SSH_CONFIG, auth.DEFAULT_HOST, auth.DEFAULT_SSH_PORT)
    appsettings = auth.read_remote_json(config, auth.REMOTE_APPSETTINGS)
    account = appsettings.get("TestAccount", {})
    email = account.get("Email") or auth.TEST_EMAIL
    password = account.get("Password") or ""
    smoke_token = account.get("SmokeToken") or ""
    if not password or auth.looks_like_placeholder(password):
        raise RuntimeError("TestAccount password is not usable.")
    if auth.looks_like_placeholder(smoke_token):
        raise RuntimeError("TestAccount smoke token is not usable.")

    session = auth.SessionClient()
    status, body = auth.try_login(session, base_url, email, password, smoke_token)
    if status != 200 or not body or "success" not in body.lower():
        raise RuntimeError(f"TestLogin failed: status={status} body={body[:200] if body else ''}")
    return session


def request_json(url: str, payload: dict[str, Any] | None = None, headers: dict[str, str] | None = None, timeout: int = 300) -> dict[str, Any] | list[Any]:
    data = None if payload is None else json.dumps(payload, ensure_ascii=False).encode("utf-8")
    request = urllib.request.Request(
        url,
        data=data,
        headers=headers or {},
        method="GET" if payload is None else "POST",
    )
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return json.loads(response.read().decode("utf-8"))


def parse_lrc(synced_lyrics: str) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    for raw in synced_lyrics.splitlines():
        match = re.match(r"\[(\d+):(\d+(?:\.\d+)?)\]\s*(.*)$", raw)
        if not match:
            continue
        japanese = match.group(3).strip()
        if not japanese:
            continue
        timestamp = int(match.group(1)) * 60 + float(match.group(2))
        rows.append({"time": round(timestamp, 2), "japanese": japanese})
    return rows


def find_best_lrclib_synced(title: str, artist: str, video_duration: int | float | None) -> dict[str, Any] | None:
    results: list[Any] = []
    seen_ids: set[int] = set()
    terms = title_search_terms(title)
    comparable_terms = {comparable_title(term) for term in terms}
    for term in terms:
        queries = [
            {"artist_name": artist, "track_name": term},
            {"q": f"{artist} {term}"},
        ]
        for query_values in queries:
            query = urllib.parse.urlencode(query_values)
            response = request_json(f"https://lrclib.net/api/search?{query}", timeout=60)
            if not isinstance(response, list):
                continue
            for row in response:
                row_id = row.get("id") if isinstance(row, dict) else None
                if not isinstance(row_id, int) or row_id in seen_ids:
                    continue
                seen_ids.add(row_id)
                results.append(row)

    candidates = [
        row for row in results
        if isinstance(row, dict)
        and row.get("syncedLyrics")
        and parse_lrc(str(row.get("syncedLyrics") or ""))
    ]
    if not candidates:
        return None

    exact_candidates = [
        row for row in candidates
        if comparable_title(str(row.get("trackName") or "")) in comparable_terms
    ]
    if exact_candidates:
        candidates = exact_candidates

    if video_duration:
        candidates.sort(key=lambda row: (
            abs(float(row.get("duration") or 0) - float(video_duration)),
            -len(parse_lrc(str(row.get("syncedLyrics") or ""))),
        ))
        best = candidates[0]
        delta = abs(float(best.get("duration") or 0) - float(video_duration))
        if delta > MAX_LRCLIB_DURATION_DELTA_SECONDS:
            return None
        return best

    return candidates[0]


def call_learnmore_api(api_base_url: str, api_token: str, endpoint: str, song_uid: str, lines: list[dict[str, Any]], timeout: int) -> dict[str, Any]:
    payload = {
        "songUid": song_uid,
        "lines": [
            {"lyricId": index + 1, "japanese": line["japanese"]}
            for index, line in enumerate(lines)
        ],
    }
    response = request_json(
        api_base_url.rstrip("/") + endpoint,
        payload,
        {
            "Content-Type": "application/json",
            "Authorization": f"Bearer {api_token}",
        },
        timeout=timeout,
    )
    if not isinstance(response, dict):
        raise RuntimeError(f"Unexpected LearnMoreAPI response from {endpoint}.")
    return response


def call_learnmore_api_raw(
    api_base_url: str,
    api_token: str,
    endpoint: str,
    payload: dict[str, Any],
    timeout: int,
) -> dict[str, Any]:
    response = request_json(
        api_base_url.rstrip("/") + endpoint,
        payload,
        {
            "Content-Type": "application/json",
            "Authorization": f"Bearer {api_token}",
        },
        timeout=timeout,
    )
    if not isinstance(response, dict):
        raise RuntimeError(f"Unexpected LearnMoreAPI response from {endpoint}.")
    return response


def find_shazam_candidate(
    api_base_url: str,
    api_token: str,
    title: str,
    artist: str,
    video_duration: int | float | None,
    min_score: float,
    timeout: int,
) -> dict[str, Any] | None:
    seen_urls: set[str] = set()
    for term in title_search_terms(title):
        response = call_learnmore_api_raw(
            api_base_url,
            api_token,
            "/v1/shazam-search",
            {"title": term, "artist": artist, "country": "tw", "limit": 8},
            timeout,
        )
        candidates = response.get("candidates") if isinstance(response.get("candidates"), list) else []
        for candidate in candidates:
            if not isinstance(candidate, dict):
                continue
            shazam_url = str(candidate.get("shazamUrl") or "")
            if shazam_url in seen_urls:
                continue
            seen_urls.add(shazam_url)
            if not candidate.get("hasLyrics"):
                continue
            score = float(candidate.get("score") or 0)
            if score < min_score:
                continue
            candidate_duration = candidate.get("durationSeconds")
            if video_duration and candidate_duration:
                delta = abs(float(candidate_duration) - float(video_duration))
                max_delta = 45 if score >= 0.70 else MAX_LRCLIB_DURATION_DELTA_SECONDS
                if delta > max_delta:
                    continue
                candidate["durationDeltaSeconds"] = round(delta, 3)
            candidate["matchedSearchTitle"] = term
            return candidate
    return None


def build_lyrics_from_timed_lines(
    api_base_url: str,
    api_token: str,
    song_uid: str,
    lines: list[dict[str, Any]],
    timeout: int,
) -> list[dict[str, Any]]:
    translations = call_learnmore_api(api_base_url, api_token, "/v1/translate-lines", song_uid, lines, timeout).get("translations", [])
    annotations = call_learnmore_api(api_base_url, api_token, "/v1/annotate-lines", song_uid, lines, timeout).get("annotations", [])
    translations_by_id = {int(item["lyricId"]): item for item in translations}
    annotations_by_id = {int(item["lyricId"]): item for item in annotations}

    lyrics = []
    for index, line in enumerate(lines, start=1):
        translation = translations_by_id[index]
        annotation = annotations_by_id[index]
        lyrics.append({
            "Time": line["time"],
            "Japanese": line["japanese"],
            "Chinese": translation["chinese"],
            "JapaneseRuby": annotation["japaneseRuby"],
            "Roman": annotation["roman"],
        })
    return lyrics


def post_summon(
    session: auth.SessionClient,
    base_url: str,
    video_id: str,
    title: str,
    artist: str,
    performer: str,
    lyrics: list[dict[str, Any]],
) -> tuple[int, str]:
    payload = {
        "YouTubeLink": f"https://www.youtube.com/watch?v={video_id}",
        "SongTitle": title,
        "SongArtist": artist,
        "SongCover": "",
        "SongPerformer": performer,
        "ChineseTitleAlias": "",
        "SongTranslator": "",
        "Lyrics": lyrics,
    }
    return session.post_json(base_url.rstrip("/") + "/Media/Summon", payload)


def summarize_alignment_sources(lines: list[dict[str, Any]]) -> dict[str, int]:
    summary: dict[str, int] = {}
    for line in lines:
        source = str(line.get("source") or "unknown")
        summary[source] = summary.get(source, 0) + 1
    return summary


def row_from_exception(video: dict[str, Any], status: str, exc: Exception) -> dict[str, Any]:
    body = ""
    http_status = None
    if isinstance(exc, urllib.error.HTTPError):
        http_status = exc.code
        body = exc.read().decode("utf-8", "ignore")[:500]
    return {
        "status": status,
        "videoId": video["id"],
        "title": normalize_title(video.get("title") or video["id"]),
        "httpStatus": http_status,
        "reason": str(exc),
        "body": body,
    }


def summon_from_shazam_whisper(
    session: auth.SessionClient,
    base_url: str,
    api_base_url: str,
    api_token: str,
    video: dict[str, Any],
    timeout: int,
    min_score: float,
    artist_override: str = "",
    performer_override: str = "",
) -> dict[str, Any]:
    video_id = video["id"]
    raw_title = video.get("title") or video_id
    title = normalize_title(raw_title)
    artist = infer_video_artist(video, raw_title, artist_override)
    performer = infer_video_performer(video, raw_title, artist, performer_override)
    started = time.time()
    candidate = find_shazam_candidate(
        api_base_url,
        api_token,
        title,
        artist,
        video.get("duration"),
        min_score,
        timeout,
    )
    if not candidate:
        return {
            "status": "shazam_not_found",
            "videoId": video_id,
            "title": title,
            "durationSeconds": video.get("duration"),
            "reason": "No Shazam lyric candidate matched the title, artist, and duration.",
            "elapsedSeconds": round(time.time() - started, 1),
        }

    aligned = call_learnmore_api_raw(
        api_base_url,
        api_token,
        "/v1/transcribe-align-shazam",
        {
            "songUid": video_id,
            "youtubeUrl": f"https://www.youtube.com/watch?v={video_id}",
            "shazamUrl": candidate["shazamUrl"],
            "language": "ja",
        },
        timeout,
    )
    aligned_lines = aligned.get("alignedLines") if isinstance(aligned.get("alignedLines"), list) else []
    if not aligned_lines:
        return {
            "status": "shazam_alignment_empty",
            "videoId": video_id,
            "title": title,
            "shazamUrl": candidate.get("shazamUrl"),
            "elapsedSeconds": round(time.time() - started, 1),
        }
    def is_unverified_alignment_source(line: dict[str, Any]) -> bool:
        source = str(line.get("source") or "")
        return source == "proportional_fallback" or "unverified" in source

    if any(is_unverified_alignment_source(line) for line in aligned_lines):
        return {
            "status": "shazam_alignment_low_confidence",
            "videoId": video_id,
            "title": title,
            "shazamUrl": candidate.get("shazamUrl"),
            "alignmentSources": summarize_alignment_sources(aligned_lines),
            "reason": "At least one lyric line could not be verified against the YouTube audio.",
            "elapsedSeconds": round(time.time() - started, 1),
        }

    timed_lines = [
        {"time": round(float(line["start"]), 2), "japanese": str(line["japanese"]).strip()}
        for line in aligned_lines
        if str(line.get("japanese") or "").strip()
    ]
    lyrics = build_lyrics_from_timed_lines(api_base_url, api_token, video_id, timed_lines, timeout)
    try:
        status, body = post_summon(session, base_url, video_id, title, artist, performer, lyrics)
    except urllib.error.HTTPError as exc:
        body = exc.read().decode("utf-8", "ignore")
        if exc.code == 409:
            try:
                data = json.loads(body)
            except json.JSONDecodeError:
                data = {"raw": body}
            return {
                "status": "duplicate",
                "videoId": video_id,
                "title": title,
                "songUid": data.get("ExistingSongUid"),
                "message": data.get("Message"),
                "source": "shazam_whisper",
                "shazamUrl": candidate.get("shazamUrl"),
                "elapsedSeconds": round(time.time() - started, 1),
            }
        return {
            "status": "http_error",
            "videoId": video_id,
            "title": title,
            "httpStatus": exc.code,
            "body": body[:500],
            "source": "shazam_whisper",
            "shazamUrl": candidate.get("shazamUrl"),
            "elapsedSeconds": round(time.time() - started, 1),
        }

    try:
        data = json.loads(body)
    except json.JSONDecodeError:
        data = {"raw": body}
    return {
        "status": "done" if status == 200 else "error",
        "videoId": video_id,
        "title": title,
        "songUid": data.get("SongUid"),
        "message": data.get("Message"),
        "source": "shazam_whisper",
        "shazamUrl": candidate.get("shazamUrl"),
        "shazamTitle": candidate.get("title"),
        "shazamArtist": candidate.get("artist"),
        "alignmentSources": summarize_alignment_sources(aligned_lines),
        "lineCount": len(lyrics),
        "elapsedSeconds": round(time.time() - started, 1),
    }


def summon_from_lrclib(
    session: auth.SessionClient,
    base_url: str,
    api_base_url: str,
    api_token: str,
    video: dict[str, Any],
    timeout: int,
    artist_override: str = "",
    performer_override: str = "",
) -> dict[str, Any]:
    video_id = video["id"]
    raw_title = video.get("title") or video_id
    title = normalize_title(raw_title)
    artist = infer_video_artist(video, raw_title, artist_override)
    performer = infer_video_performer(video, raw_title, artist, performer_override)
    started = time.time()
    source = find_best_lrclib_synced(title, artist, video.get("duration"))
    if not source:
        return {
            "status": "skipped_duration",
            "videoId": video_id,
            "title": title,
            "durationSeconds": video.get("duration"),
            "reason": "No synced LRCLIB source close enough to the YouTube duration.",
        }
    lrclib_duration = source.get("duration")
    if video.get("duration") and lrclib_duration:
        delta = abs(float(lrclib_duration) - float(video["duration"]))
        if delta > MAX_VERIFIED_LRCLIB_DURATION_DELTA_SECONDS:
            return {
                "status": "skipped_duration",
                "videoId": video_id,
                "title": title,
                "durationSeconds": video.get("duration"),
                "lrclibId": source.get("id"),
                "lrclibDurationSeconds": lrclib_duration,
                "durationDeltaSeconds": round(delta, 3),
                "reason": "LRCLIB duration differs from YouTube too much; require same-video Whisper verification before upload.",
            }

    lines = parse_lrc(str(source.get("syncedLyrics") or ""))
    lyrics = build_lyrics_from_timed_lines(api_base_url, api_token, video_id, lines, timeout)
    try:
        status, body = post_summon(session, base_url, video_id, title, artist, performer, lyrics)
    except urllib.error.HTTPError as exc:
        body = exc.read().decode("utf-8", "ignore")
        if exc.code == 409:
            try:
                data = json.loads(body)
            except json.JSONDecodeError:
                data = {"raw": body}
            return {
                "status": "duplicate",
                "videoId": video_id,
                "title": title,
                "songUid": data.get("ExistingSongUid"),
                "message": data.get("Message"),
                "lrclibId": source.get("id"),
                "lrclibDurationSeconds": source.get("duration"),
                "elapsedSeconds": round(time.time() - started, 1),
            }
        return {
            "status": "http_error",
            "videoId": video_id,
            "title": title,
            "httpStatus": exc.code,
            "body": body[:500],
            "lrclibId": source.get("id"),
            "elapsedSeconds": round(time.time() - started, 1),
        }

    try:
        data = json.loads(body)
    except json.JSONDecodeError:
        data = {"raw": body}
    return {
        "status": "done" if status == 200 else "error",
        "videoId": video_id,
        "title": title,
        "songUid": data.get("SongUid"),
        "message": data.get("Message"),
        "lrclibId": source.get("id"),
        "lrclibDurationSeconds": source.get("duration"),
        "lineCount": len(lyrics),
        "elapsedSeconds": round(time.time() - started, 1),
    }


def read_sse(response: Any) -> list[tuple[str, dict[str, Any]]]:
    events: list[tuple[str, dict[str, Any]]] = []
    event_name: str | None = None
    data_lines: list[str] = []
    for raw in response:
        line = raw.decode("utf-8", "ignore").rstrip("\r\n")
        if not line:
            if event_name and data_lines:
                payload = "\n".join(data_lines)
                try:
                    data = json.loads(payload)
                except json.JSONDecodeError:
                    data = {"raw": payload}
                events.append((event_name, data))
                if event_name in {"done", "error"}:
                    return events
            event_name = None
            data_lines = []
            continue
        if line.startswith("event:"):
            event_name = line.split(":", 1)[1].strip()
        elif line.startswith("data:"):
            data_lines.append(line.split(":", 1)[1].strip())
    return events


def submit(session: auth.SessionClient, base_url: str, video: dict[str, Any], timeout: int, artist_override: str = "", performer_override: str = "") -> dict[str, Any]:
    video_id = video["id"]
    raw_title = video.get("title") or video_id
    artist = infer_video_artist(video, raw_title, artist_override)
    performer = infer_video_performer(video, raw_title, artist, performer_override)
    payload = {
        "YouTubeUrl": f"https://www.youtube.com/watch?v={video_id}",
        "Title": normalize_title(raw_title),
        "Artist": artist,
        "Performer": performer,
        "ChineseTitleAlias": "",
        "Language": "ja",
    }
    request = urllib.request.Request(
        base_url.rstrip("/") + "/Media/webtranscribe",
        data=json.dumps(payload).encode("utf-8"),
        headers={"Content-Type": "application/json", "Accept": "text/event-stream"},
        method="POST",
    )
    started = time.time()
    try:
        with session.opener.open(request, timeout=timeout) as response:
            events = read_sse(response)
    except urllib.error.HTTPError as exc:
        return {
            "status": "http_error",
            "videoId": video_id,
            "title": payload["Title"],
            "httpStatus": exc.code,
            "body": exc.read().decode("utf-8", "ignore")[:500],
            "elapsedSeconds": round(time.time() - started, 1),
        }

    last = events[-1] if events else ("empty", {})
    event, data = last
    if event == "done":
        return {
            "status": "done",
            "videoId": video_id,
            "title": payload["Title"],
            "songUid": data.get("songUid"),
            "elapsedSeconds": round(time.time() - started, 1),
        }
    if event == "error" and data.get("reason") == "duplicate_youtube_video":
        return {
            "status": "duplicate",
            "videoId": video_id,
            "title": payload["Title"],
            "songUid": data.get("existingSongUid"),
            "message": data.get("message"),
            "elapsedSeconds": round(time.time() - started, 1),
        }
    return {
        "status": "error",
        "videoId": video_id,
        "title": payload["Title"],
        "event": event,
        "data": data,
        "elapsedSeconds": round(time.time() - started, 1),
    }


def main() -> int:
    args = parse_args()
    videos = load_playlist(args.playlist_jsonl)
    done = load_done(args.result_jsonl)
    selected = videos[args.start - 1 :]
    if args.limit > 0:
        selected = selected[: args.limit]

    session = login(args.base_url)
    api_token = read_env(args.learnmore_api_env).get("LEARNMORE_API_TOKEN", "")
    for index, video in enumerate(selected, start=args.start):
        video_id = video["id"]
        if video_id in done:
            print(f"[{index}/{len(videos)}] skip recorded {video_id}", flush=True)
            continue
        if args.only_shazam_whisper_fallback:
            if not api_token:
                row = {
                    "status": "api_token_missing",
                    "videoId": video_id,
                    "title": normalize_title(video.get("title") or video_id),
                    "reason": "LearnMoreAPI token is required for Shazam + Whisper fallback.",
                }
            else:
                print(f"[{index}/{len(videos)}] shazam+whisper {video_id} {normalize_title(video.get('title') or video_id)}", flush=True)
                try:
                    row = summon_from_shazam_whisper(
                        session,
                        args.base_url,
                        args.learnmore_api_base_url,
                        api_token,
                        video,
                        args.timeout,
                        args.shazam_min_score,
                        args.artist,
                        args.performer,
                    )
                except Exception as exc:
                    row = row_from_exception(video, "shazam_whisper_error", exc)
            append_result(args.result_jsonl, row)
            print(f"[{index}/{len(videos)}] {row['status']} {row.get('songUid') or row.get('reason') or row.get('message')}", flush=True)
            done.add(video_id)
            continue
        duration = video.get("duration")
        if duration and duration > MAX_AUTOMATIC_SECONDS:
            if args.skip_summon_over_limit or not api_token:
                row = {
                    "status": "skipped_duration",
                    "videoId": video_id,
                    "title": normalize_title(video.get("title") or video_id),
                    "durationSeconds": duration,
                    "reason": "webtranscribe rejects videos over 6 minutes and summon fallback is disabled or missing LearnMoreAPI token",
                }
            else:
                print(f"[{index}/{len(videos)}] summon {video_id} {normalize_title(video.get('title') or video_id)}", flush=True)
                try:
                    row = summon_from_lrclib(session, args.base_url, args.learnmore_api_base_url, api_token, video, args.timeout, args.artist, args.performer)
                except Exception as exc:
                    row = row_from_exception(video, "summon_error", exc)
                if row.get("status") == "skipped_duration" and args.enable_shazam_whisper_fallback:
                    print(f"[{index}/{len(videos)}] shazam+whisper {video_id} {normalize_title(video.get('title') or video_id)}", flush=True)
                    try:
                        row = summon_from_shazam_whisper(
                            session,
                            args.base_url,
                            args.learnmore_api_base_url,
                            api_token,
                            video,
                            args.timeout,
                            args.shazam_min_score,
                            args.artist,
                            args.performer,
                        )
                    except Exception as exc:
                        row = row_from_exception(video, "shazam_whisper_error", exc)
                elif row.get("status") == "summon_error" and args.enable_shazam_whisper_fallback:
                    print(f"[{index}/{len(videos)}] shazam+whisper {video_id} {normalize_title(video.get('title') or video_id)}", flush=True)
                    try:
                        row = summon_from_shazam_whisper(
                            session,
                            args.base_url,
                            args.learnmore_api_base_url,
                            api_token,
                            video,
                            args.timeout,
                            args.shazam_min_score,
                            args.artist,
                            args.performer,
                        )
                    except Exception as exc:
                        row = row_from_exception(video, "shazam_whisper_error", exc)
            append_result(args.result_jsonl, row)
            print(f"[{index}/{len(videos)}] {row['status']} {row.get('songUid') or row.get('reason') or row.get('message')}", flush=True)
            done.add(video_id)
            continue
        print(f"[{index}/{len(videos)}] submit {video_id} {normalize_title(video.get('title') or video_id)}", flush=True)
        row = submit(session, args.base_url, video, args.timeout, args.artist, args.performer)
        if row.get("status") == "error" and args.enable_shazam_whisper_fallback and api_token:
            print(f"[{index}/{len(videos)}] shazam+whisper {video_id} {normalize_title(video.get('title') or video_id)}", flush=True)
            try:
                row = summon_from_shazam_whisper(
                    session,
                    args.base_url,
                    args.learnmore_api_base_url,
                    api_token,
                    video,
                    args.timeout,
                    args.shazam_min_score,
                    args.artist,
                    args.performer,
                )
            except Exception as exc:
                row = row_from_exception(video, "shazam_whisper_error", exc)
        append_result(args.result_jsonl, row)
        print(f"[{index}/{len(videos)}] {row['status']} {row.get('songUid') or row.get('message') or row.get('data')}", flush=True)
        done.add(video_id)
        time.sleep(args.sleep)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
