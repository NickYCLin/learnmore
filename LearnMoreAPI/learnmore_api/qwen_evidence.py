"""讀取獨立 Qwen ASR 的逐字證據；不接受正式歌詞作為辨識提示。"""
from __future__ import annotations

import hashlib
import json
import math
import os
from pathlib import Path
import subprocess
import tempfile
import unicodedata

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
    if not isinstance(text, str) or result.get("language") not in {"", "Japanese", "English"}:
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
                or start < previous or start < 0 or end < start or end > duration
                or not isinstance(token, str) or not normalize(token)):
            raise ValueError("qwen_invalid_word_timing")
        words.append({"index": len(words) + 1, "start": start, "end": end, "text": token})
        previous = start
    if normalize(text) != normalize("".join(w["text"] for w in words)):
        raise ValueError("qwen_incomplete_word_coverage")
    return words


def transcribe(audio_path: Path, duration: float, command: list[str], timeout: float,
               language: str = "ja") -> list[dict]:
    languages = {"ja": "Japanese", "en": "English", "auto": "auto"}
    if language not in languages:
        raise ValueError("qwen_unsupported_requested_language")
    # 不將網站 API 憑證或 Hugging Face 登入資訊傳給辨識子程序。
    allowed = {"PATH", "HOME", "LD_LIBRARY_PATH", "HF_HOME", "PYTHONPATH", "VIRTUAL_ENV",
               "PYTHONDONTWRITEBYTECODE", "PYTHONUNBUFFERED"}
    env = {k: v for k, v in os.environ.items()
           if (k in allowed or k.startswith(("CUDA_", "NVIDIA_", "TORCH_")))
           and not any(marker in k.upper() for marker in ("TOKEN", "SECRET", "PASSWORD", "API_KEY"))}
    env.update(HF_HUB_OFFLINE="1", HF_HUB_DISABLE_IMPLICIT_TOKEN="1")
    with tempfile.TemporaryDirectory(prefix="learnmore-qwen-evidence-") as directory:
        output = Path(directory) / "result.json"
        subprocess.run([*command, "transcribe", str(audio_path), "--language", languages[language],
                        "--max-new-tokens", "512", "--chunk-seconds", "30", "--output", str(output)],
                       check=True, timeout=timeout, capture_output=True, env=env)
        if output.stat().st_size > 12_000_000:
            raise ValueError("qwen_report_too_large")
        return parse_result(json.loads(output.read_text()), audio_path, duration)


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
