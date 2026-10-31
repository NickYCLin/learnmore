"""Transfer an explicitly registered timed source only after recording-identity checks.

This verifies a published reference timeline, not ASR recognition. It never
interpolates lyric lines or treats an existing database timestamp as evidence.
"""
from __future__ import annotations

import hashlib
import json
import math
import re
import subprocess
import unicodedata
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import parse_qs, urlsplit

import requests

SOURCE = "marumaru_reference_audio_timing_verified"
MANIFEST = Path(__file__).with_name("reference_sources.json")


class SourcePage(HTMLParser):
    def __init__(self):
        super().__init__()
        self.meta = {}
        self.player = {}
        self.script = None

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if tag == "meta" and attrs.get("name") in {"salt", "csrf-token"}:
            self.meta[attrs["name"]] = attrs.get("content", "")
        if attrs.get("id") == "player":
            self.player = attrs
        src = attrs.get("src", "")
        if tag == "script" and re.fullmatch(r"/js/mr-jp-song-detail\.js(?:\?[^#]*)?", src):
            self.script = src


def video_id(url):
    parsed = urlsplit(url)
    if parsed.scheme != "https" or parsed.hostname not in {"www.youtube.com", "youtube.com"}:
        raise ValueError("unsupported_video")
    value = parse_qs(parsed.query).get("v", [""])[0]
    if not re.fullmatch(r"[A-Za-z0-9_-]{11}", value):
        raise ValueError("invalid_video")
    return value


def normalize(text):
    return "".join(unicodedata.normalize("NFKC", text).split())


def fetch_source(registration):
    from hashids import Hashids

    url = registration["sourceUrl"]
    if not re.fullmatch(r"https://www\.marumaru-x\.com/japanese-song/play-[a-z0-9]+", url):
        raise ValueError("unregistered_provider")
    with requests.Session() as session:
        session.headers.update({"User-Agent": "Mozilla/5.0", "Referer": url})

        def read(method, target, **kwargs):
            response = session.request(method, target, timeout=(10, 25), allow_redirects=False, **kwargs)
            if response.status_code != 200 or len(response.content) > 2_000_000:
                raise ValueError("source_unavailable")
            return response

        page = SourcePage()
        page.feed(read("GET", url).text)
        if page.player.get("data-video-id") != registration["referenceVideoId"] or not page.script:
            raise ValueError("reference_video_changed")
        script = read("GET", "https://www.marumaru-x.com" + page.script).text
        alphabet = re.search(r'(?:t|this)\.confusedWord="([^"]+)"', script)
        if not alphabet:
            raise ValueError("unsupported_source_encoding")
        decoder = Hashids(salt=page.meta["salt"], min_length=10, alphabet=alphabet[1])
        payload = read("POST", "https://www.marumaru-x.com/system/process/get-lyrics",
            headers={"X-CSRF-Token": page.meta["csrf-token"], "X-Requested-With": "XMLHttpRequest"},
            data={"SongPK": page.player["data-id"], "SongLang": page.player["data-song-lang"]}).json()
        if payload.get("success") is not True:
            raise ValueError("source_unavailable")

        def decode(value):
            a, b = value.split("-")
            seconds, fraction = decoder.decode(a), decoder.decode(b)
            if len(seconds) != 1 or len(fraction) > 1:
                raise ValueError("invalid_source_time")
            return float(str(seconds[0]) + ("." + str(fraction[0]) if fraction else ""))

        rows = [{"text": row["Lyrics"], "start": decode(row["StartTime"]), "end": decode(row["EndTime"])}
                for row in payload["lyrics"] if row.get("Lyrics")]
        if not 1 <= len(rows) <= 200:
            raise ValueError("invalid_source_count")
        return rows


def load_audio(path):
    import numpy as np

    result = subprocess.run(["ffmpeg", "-v", "error", "-i", str(path), "-f", "f32le",
        "-ac", "1", "-ar", "4000", "-"], capture_output=True, timeout=60, check=True)
    audio = np.frombuffer(result.stdout, dtype="<f4").astype(np.float64)
    if not 30 * 4000 <= len(audio) <= 360 * 4000 or not np.isfinite(audio).all():
        raise ValueError("invalid_reference_audio")
    return audio


def compare_recordings(target, reference):
    import numpy as np
    from scipy.signal import correlate, correlation_lags

    if min(len(target), len(reference)) < 30 * 4000:
        raise ValueError("insufficient_recording")
    target = target - target.mean()
    reference = reference - reference.mean()
    correlation = correlate(target, reference, mode="full", method="fft")
    lag = int(correlation_lags(len(target), len(reference))[int(correlation.argmax())])
    a, b = max(lag, 0), max(-lag, 0)
    length = min(len(target) - a, len(reference) - b)
    overlap = length / max(len(target), len(reference))
    if abs(lag) > 15 * 4000 or overlap < 0.95:
        raise ValueError("recording_extent_mismatch")
    x, y = target[a:a + length], reference[b:b + length]

    def score(left, right):
        left, right = left - left.mean(), right - right.mean()
        denominator = np.linalg.norm(left) * np.linalg.norm(right)
        if denominator <= 1e-12:
            raise ValueError("silent_reference")
        return max(-1.0, min(1.0, float(left @ right / denominator)))

    global_score = score(x, y)
    windows = [score(x[i:i + 48000], y[i:i + 48000])
               for i in np.linspace(0, length - 48000, 12).astype(int)]
    if not math.isfinite(global_score) or global_score < 0.95 or min(windows) < 0.90:
        raise ValueError("recording_identity_unverified")
    return {"correlation": global_score, "minimumWindowCorrelation": min(windows),
        "windowCount": len(windows), "overlapFraction": overlap, "offsetSeconds": lag / 4000,
        "targetDuration": len(target) / 4000, "referenceDuration": len(reference) / 4000}


def build_candidate(request, registration, rows, evidence):
    if (video_id(request.youtubeUrl) != registration["targetVideoId"]
            or len(rows) != len(request.lyrics)
            or len({line.lyricId for line in request.lyrics}) != len(rows)
            or any(normalize(row["text"]) != normalize(line.japanese)
                   for row, line in zip(rows, request.lyrics))):
        raise ValueError("reference_text_mismatch")
    aligned, previous = [], -1.0
    offset = evidence["offsetSeconds"]
    for row, line in zip(rows, request.lyrics):
        start, end = row["start"] + offset, row["end"] + offset
        if (not all(math.isfinite(x) for x in (start, end)) or start < 0 or start <= previous
                or end <= start or row["end"] > evidence["referenceDuration"]
                or end > evidence["targetDuration"]):
            raise ValueError("invalid_reference_timeline")
        aligned.append({"lyricId": line.lyricId, "japanese": line.japanese,
            "start": round(start, 3), "end": round(end, 3), "source": SOURCE,
            "score": min(evidence["correlation"], evidence["minimumWindowCorrelation"])})
        previous = start
    proof = dict(evidence, sourceUrl=registration["sourceUrl"],
        targetVideoId=registration["targetVideoId"], referenceVideoId=registration["referenceVideoId"],
        matchedLineCount=len(rows), sourceTimelineSha256=hashlib.sha256(
            json.dumps(rows, ensure_ascii=False, sort_keys=True).encode()).hexdigest())
    return {"songUid": request.songUid, "youtubeUrl": request.youtubeUrl, "language": request.language,
        "model": "marumaru-timed-reference+recording-identity-12-windows",
        "durationSeconds": evidence["targetDuration"], "alignedLines": aligned,
        "matchedCount": len(aligned), "totalCount": len(aligned), "referenceEvidence": proof}


def try_align_reference(request, settings, download):
    try:
        registrations = json.loads(MANIFEST.read_text()) if MANIFEST.exists() else {}
    except (ValueError, OSError):
        return None
    registration = registrations.get(request.songUid) if isinstance(registrations, dict) else None
    if not isinstance(registration, dict) or request.language != "ja":
        return None
    files = []
    try:
        if video_id(request.youtubeUrl) != registration["targetVideoId"]:
            return None
        rows = fetch_source(registration)
        if (len(rows) != len(request.lyrics) or any(normalize(row["text"]) != normalize(line.japanese)
                for row, line in zip(rows, request.lyrics))):
            return None
        target = download(request.youtubeUrl, settings)
        files.append(target)
        reference = download("https://www.youtube.com/watch?v=" + registration["referenceVideoId"], settings)
        files.append(reference)
        evidence = compare_recordings(load_audio(target), load_audio(reference))
        return build_candidate(request, registration, rows, evidence)
    except (ValueError, KeyError, TypeError, requests.RequestException, OSError, RuntimeError, subprocess.SubprocessError):
        # A provider/network/version mismatch keeps the normal audio verifier.
        return None
    finally:
        for path in set(files):
            path.unlink(missing_ok=True)
