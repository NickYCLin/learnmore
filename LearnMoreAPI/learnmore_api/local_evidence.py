"""僅重驗由受信任操作者登記的不可變音訊證據，不執行推論。"""
from __future__ import annotations

import hashlib
import json
import math
from pathlib import Path
import re
import wave

from . import qwen_evidence


def read_artifact(directory: Path, name: str, *, limit: int) -> bytes:
    if not isinstance(name, str) or not re.fullmatch(r"[A-Za-z0-9_-]+\.(json|wav)", name):
        raise ValueError("evidence_invalid_artifact")
    path = directory / name
    if path.is_symlink() or not path.is_file() or path.stat().st_size > limit:
        raise ValueError("evidence_invalid_artifact")
    with path.open("rb") as source:
        content = source.read(limit + 1)
    if len(content) > limit:
        raise ValueError("evidence_invalid_artifact")
    return content


def digest(content: bytes) -> str:
    return hashlib.sha256(content).hexdigest()


def verify(root: str, evidence_id: str, snapshot_hash: str, song_uid: str,
           youtube_url: str, language: str, lyrics: list[dict]) -> dict:
    if not root:
        raise ValueError("evidence_registry_not_configured")
    if (not re.fullmatch(r"[0-9a-f]{32}", evidence_id)
            or not re.fullmatch(r"[0-9A-Fa-f]{64}", snapshot_hash) or language not in {"ja", "en"}):
        raise ValueError("evidence_invalid_contract")
    if not lyrics or len({lyric["lyricId"] for lyric in lyrics}) != len(lyrics):
        raise ValueError("evidence_invalid_contract")
    directory = Path(root)
    if directory.is_symlink() or not directory.is_absolute():
        raise ValueError("evidence_invalid_registry")
    registry_path = directory / "registry.json"
    if registry_path.is_symlink() or registry_path.stat().st_mode & 0o022:
        raise ValueError("evidence_invalid_registry")
    registry = json.loads(read_artifact(directory, "registry.json", limit=1_000_000))
    registered = registry.get(evidence_id)
    if not isinstance(registered, str) or not re.fullmatch(r"[0-9a-f]{64}", registered):
        raise ValueError("evidence_not_registered")
    bundle = directory / evidence_id
    if bundle.is_symlink() or not bundle.is_dir():
        raise ValueError("evidence_invalid_artifact")
    manifest_bytes = read_artifact(bundle, "manifest.json", limit=2_000_000)
    if digest(manifest_bytes) != registered:
        raise ValueError("evidence_manifest_changed")
    manifest = json.loads(manifest_bytes)
    if (manifest.get("version") != 1 or manifest.get("evidenceId") != evidence_id
            or manifest.get("snapshotHash") != snapshot_hash
            or manifest.get("songUid") != song_uid or manifest.get("youtubeUrl") != youtube_url
            or manifest.get("language") != language or manifest.get("lyrics") != lyrics):
        raise ValueError("evidence_snapshot_mismatch")
    durations = []
    for name in ("source", "vocals", "raw"):
        artifact = manifest[name]
        content = read_artifact(bundle, artifact["file"], limit=12_000_000 if name == "raw" else 200_000_000)
        if digest(content) != artifact["sha256"]:
            raise ValueError("evidence_audio_or_raw_changed")
        if name != "raw":
            import io
            with wave.open(io.BytesIO(content)) as audio:
                duration = audio.getnframes() / audio.getframerate()
                if not math.isfinite(duration) or duration <= 0:
                    raise ValueError("evidence_invalid_duration")
                durations.append(duration)
        else:
            raw = json.loads(content)
    if abs(durations[0] - durations[1]) > 0.05:
        raise ValueError("evidence_audio_duration_mismatch")
    vocals = bundle / manifest["vocals"]["file"]
    words = qwen_evidence.parse_result(raw, vocals, durations[1])
    requested = "Japanese" if language == "ja" else "English"
    if language == "en" and raw["results"][0]["language"] != requested:
        raise ValueError("evidence_language_mismatch")
    baseline = []
    previous = -1.0
    for lyric in lyrics:
        start = lyric.get("currentStart")
        if type(start) not in {int, float} or not math.isfinite(start) or not 0 <= start < durations[1] or start <= previous:
            raise ValueError("evidence_invalid_snapshot_timing")
        previous = start
        baseline.append(dict(lyricId=lyric["lyricId"], japanese=lyric["japanese"], start=start,
                             end=min(start + 0.001, durations[1]), score=0,
                             source="current_timestamp_context"))
    aligned = qwen_evidence.supplement(lyrics, baseline, words,
                                    lambda line: line["source"] == "qwen3_asr_word_match")
    return {"songUid": song_uid, "youtubeUrl": youtube_url, "language": language,
            "model": "qwen3-asr-pinned-local-evidence:" + registered,
            "durationSeconds": durations[1], "alignedLines": aligned,
            "matchedCount": sum(line["source"] == "qwen3_asr_word_match" for line in aligned),
            "totalCount": len(aligned)}
