"""讀取獨立 Qwen ASR 的逐字證據；不接受正式歌詞作為辨識提示。"""
from __future__ import annotations

import hashlib
import json
import logging
import math
import os
from pathlib import Path
import re
import subprocess
import tempfile
import time
import unicodedata
import wave

MODELS = {
    "Qwen/Qwen3-ASR-1.7B": "7278e1e70fe206f11671096ffdd38061171dd6e5",
    "Qwen/Qwen3-ForcedAligner-0.6B": "c7cbfc2048c462b0d63a45797104fc9db3ad62b7",
}


def normalize(text: str) -> str:
    return "".join(c for c in unicodedata.normalize("NFKC", text).casefold() if c.isalnum())


def load_command(raw: str) -> list[str]:
    if not raw.strip():
        return []
    try:
        command = json.loads(raw)
    except ValueError as error:
        raise ValueError("qwen_command_must_be_json_array") from error
    if not isinstance(command, list) or not command or any(not isinstance(x, str) or not x.strip() for x in command):
        raise ValueError("qwen_command_must_be_json_array")
    return command


def parse_result(payload: dict, audio_path: Path, duration: float) -> list[dict]:
    if not isinstance(payload, dict):
        raise ValueError("qwen_invalid_report")
    if not math.isfinite(duration) or duration <= 0:
        raise ValueError("qwen_invalid_duration")
    if (payload.get("models") != MODELS or payload.get("verificationStatus") != "unreviewed"
            or payload.get("backend") != "transformers-cuda-bfloat16"):
        raise ValueError("qwen_wrong_model_contract")
    results = payload.get("results")
    if not isinstance(results, list) or len(results) != 1:
        raise ValueError("qwen_wrong_result_count")
    result = results[0]
    if not isinstance(result, dict):
        raise ValueError("qwen_invalid_result")
    with audio_path.open("rb") as source:
        digest = hashlib.file_digest(source, "sha256").hexdigest()
    if result.get("file") != audio_path.name or result.get("fileSha256") != digest:
        raise ValueError("qwen_wrong_audio")
    text = result.get("text")
    language = result.get("language")
    if (not isinstance(text, str) or not isinstance(language, str)
            or (language != "" and (not set(language.split(",")).issubset({"Japanese", "English"})
                                   or len(language.split(",")) != len(set(language.split(",")))))):
        raise ValueError("qwen_unsupported_transcript")
    timestamps = result.get("timeStamps") or {"items": []}
    if not isinstance(timestamps, dict) or not isinstance(timestamps.get("items"), list):
        raise ValueError("qwen_missing_timestamps")
    words, previous = [], -1.0
    for item in timestamps["items"]:
        if not isinstance(item, dict):
            raise ValueError("qwen_invalid_word_timing")
        start, end, token = item.get("start_time"), item.get("end_time"), item.get("text")
        if (type(start) not in {int, float} or type(end) not in {int, float}
                or not math.isfinite(start) or not math.isfinite(end)
                or start < previous or start < 0 or end <= start or end > duration
                or not isinstance(token, str) or not normalize(token)):
            raise ValueError("qwen_invalid_word_timing")
        words.append({"index": len(words) + 1, "start": start, "end": end, "text": token})
        previous = start
    if normalize(text) != normalize("".join(w["text"] for w in words)):
        raise ValueError("qwen_incomplete_word_coverage")
    return words


def transcribe(audio_path: Path, duration: float, command: list[str], timeout: float,
               language: str = "ja") -> list[dict]:
    root = Path(os.environ.get("QWEN_DIAGNOSTICS_DIR", "/tmp/learnmore-qwen-diagnostics"))
    root.mkdir(mode=0o700, parents=True, exist_ok=True)
    directory = Path(tempfile.mkdtemp(prefix="evidence-", dir=root))
    output = directory / "result.json"
    receipt = {"diagnosticId": directory.name, "language": language, "state": "started"}
    (directory / "diagnostic.json").write_text(json.dumps(receipt))
    try:
        run_transcription([audio_path], output, command, timeout, language)
        if output.stat().st_size > 12_000_000:
            raise ValueError("qwen_report_too_large")
        words = parse_result(json.loads(output.read_text()), audio_path, duration)
        receipt.update(state="completed", exitCode=0)
        return words
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        code = "timeout" if isinstance(error, subprocess.TimeoutExpired) else (
            "subprocess_exit" if isinstance(error, subprocess.CalledProcessError) else "invalid_evidence")
        if isinstance(error, ValueError) and str(error) in {
            "qwen_invalid_word_timing", "qwen_wrong_model_contract", "qwen_incomplete_word_coverage",
            "qwen_wrong_audio", "qwen_report_too_large"}:
            code = str(error)
        receipt.update(state="failed", errorCode=code,
                       exitCode=error.returncode if isinstance(error, subprocess.CalledProcessError) else None)
        logging.getLogger(__name__).warning("Qwen evidence failed diagnosticId=%s code=%s", directory.name, code)
        raise
    finally:
        (directory / "diagnostic.json").write_text(json.dumps(receipt))


def run_transcription(audio_paths: list[Path], output: Path, command: list[str], timeout: float,
                      language: str) -> None:
    languages = {"ja": "Japanese", "en": "English", "auto": "auto"}
    if language not in languages:
        raise ValueError("qwen_unsupported_requested_language")
    allowed = {"PATH", "HOME", "LD_LIBRARY_PATH", "HF_HOME", "PYTHONPATH", "VIRTUAL_ENV",
               "PYTHONDONTWRITEBYTECODE", "PYTHONUNBUFFERED"}
    env = {k: v for k, v in os.environ.items()
           if (k in allowed or k.startswith(("CUDA_", "NVIDIA_", "TORCH_")))
           and not any(marker in k.upper() for marker in ("TOKEN", "SECRET", "PASSWORD", "API_KEY"))}
    env.update(HF_HUB_OFFLINE="1", HF_HUB_DISABLE_IMPLICIT_TOKEN="1")
    subprocess.run([*command, "transcribe", *(str(path) for path in audio_paths),
                    "--language", languages[language], "--max-new-tokens", "512",
                    "--chunk-seconds", "30", "--output", str(output)],
                   check=True, timeout=timeout, capture_output=True, env=env)


def supplement(lyrics: list[dict], aligned: list[dict], words: list[dict], reliable) -> list[dict]:
    """只補完整逐字吻合且位於相鄰時間範圍內的行，保留已有可靠結果。"""
    if len(lyrics) != len(aligned) or any(
        x["lyricId"] != y["lyricId"] or x["japanese"] != y["japanese"] for x, y in zip(lyrics, aligned)
    ):
        raise ValueError("qwen_lyric_contract_mismatch")
    stream, mapped = "", []
    for word in words:
        text = normalize(word["text"])
        stream += text
        mapped.extend([word] * len(text))
    result = list(aligned)
    for i, (lyric, original) in enumerate(zip(lyrics, aligned)):
        if reliable(original):
            continue
        target = normalize(lyric["japanese"])
        hint = lyric.get("currentStart")
        if not target or hint is None or not math.isfinite(hint):
            continue
        previous = result[i - 1]["start"] if i else -1.0
        previous_end = result[i - 1]["end"] if i and reliable(result[i - 1]) else previous
        following = aligned[i + 1]["start"] if i + 1 < len(aligned) else math.inf
        candidates, position = [], 0
        while True:
            start = stream.find(target, position)
            if start < 0:
                break
            stop = start + len(target)
            position = start + 1
            first, last = mapped[start], mapped[stop - 1]
            if ((start and mapped[start - 1]["index"] == first["index"])
                    or (stop < len(mapped) and mapped[stop]["index"] == last["index"])):
                continue
            if (abs(first["start"] - hint) <= 2 and previous < first["start"] < following
                    and previous_end <= first["start"]
                    and first["start"] < last["end"] <= following):
                candidates.append((first, last))
        # 重複句有歧義時保留待校正，不取最近的句子湊命中。
        if len(candidates) == 1:
            first, last = candidates[0]
            result[i] = dict(original, start=first["start"], end=last["end"],
                             source="qwen3_asr_word_match", score=1.0,
                             whisperWordStartIndex=None, whisperWordEndIndex=None)
    return result


def latin_regions(lyrics: list[dict], aligned: list[dict], duration: float, reliable, latin_dominant) -> list[dict]:
    if not math.isfinite(duration) or duration <= 0:
        raise ValueError("qwen_invalid_duration")
    if len(lyrics) != len(aligned) or any(
        lyric["lyricId"] != row["lyricId"] or lyric["japanese"] != row["japanese"]
        for lyric, row in zip(lyrics, aligned)
    ):
        raise ValueError("qwen_lyric_contract_mismatch")
    regions = []
    for index, (lyric, row) in enumerate(zip(lyrics, aligned)):
        hint = lyric.get("currentStart")
        if (reliable(row) or not latin_dominant(lyric["japanese"])
                or type(hint) not in {int, float} or not math.isfinite(hint)):
            continue
        previous_end = next((item["end"] for item in reversed(aligned[:index]) if reliable(item)), 0.0)
        following_start = next((item["start"] for item in aligned[index + 1:] if reliable(item)), duration)
        start = max(0.0, hint - 3.0, previous_end)
        end = min(duration, hint + 12.0, following_start)
        if not 0 <= start <= hint < end <= duration:
            continue
        normalized_text = unicodedata.normalize("NFKC", lyric["japanese"])
        language = "auto" if re.search(r"[\u3040-\u30ff\u3400-\u9fff]", normalized_text) else "en"
        regions.append({"index": index, "start": start, "end": end, "language": language})
    return regions


def supplement_latin_regions(audio_path: Path, duration: float, command: list[str], timeout: float,
                             lyrics: list[dict], aligned: list[dict], reliable, latin_dominant) -> list[dict]:
    regions = latin_regions(lyrics, aligned, duration, reliable, latin_dominant)
    if not regions:
        return aligned
    deadline = time.monotonic() + timeout
    result = list(aligned)
    with tempfile.TemporaryDirectory(prefix="learnmore-latin-evidence-") as directory:
        try:
            with wave.open(str(audio_path), "rb") as source:
                rate = source.getframerate()
                if abs(source.getnframes() / rate - duration) > 0.1:
                    raise ValueError("qwen_audio_duration_mismatch")
                for region in regions:
                    first = math.ceil(region["start"] * rate)
                    stop = math.floor(region["end"] * rate)
                    if stop <= first:
                        raise ValueError("qwen_empty_audio_region")
                    source.setpos(first)
                    frames = source.readframes(stop - first)
                    if len(frames) != (stop - first) * source.getnchannels() * source.getsampwidth():
                        raise ValueError("qwen_truncated_region_audio")
                    path = Path(directory) / f"region-{region['index']}.wav"
                    with wave.open(str(path), "wb") as output:
                        output.setparams(source.getparams())
                        output.writeframes(frames)
                    region.update(path=path, offset=first / rate, duration=(stop - first) / rate)
        except (wave.Error, EOFError) as error:
            raise ValueError("qwen_unsupported_region_audio") from error
        for language in ("en", "auto"):
            batch = [region for region in regions if region["language"] == language]
            if not batch:
                continue
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise subprocess.TimeoutExpired(command[0], timeout)
            output = Path(directory) / f"{language}.json"
            run_transcription([region["path"] for region in batch], output, command, remaining, language)
            if output.stat().st_size > 12_000_000:
                raise ValueError("qwen_report_too_large")
            payload = json.loads(output.read_text())
            rows = payload.get("results") if isinstance(payload, dict) else None
            if not isinstance(rows, list) or len(rows) != len(batch):
                raise ValueError("qwen_wrong_result_count")
            for region, row in zip(batch, rows):
                words = parse_result(dict(payload, results=[row]), region["path"], region["duration"])
                absolute_words = [dict(word, start=word["start"] + region["offset"],
                                       end=word["end"] + region["offset"]) for word in words]
                candidate = supplement(lyrics, result, absolute_words, reliable)
                index = region["index"]
                result[index] = candidate[index]
    return result
