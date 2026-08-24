#!/usr/bin/env python3
"""
Use openai-whisper to produce coarse segment timestamps for second-opinion alignment.

Usage:
    python openai_whisper_segments.py <audio_file> <output_json> [model]
"""

import json
import os
import subprocess
import sys
import tempfile


def clip_audio(audio_file: str, clip_start: float, clip_end: float) -> str:
    if clip_end <= clip_start:
        raise ValueError(f"invalid clip range: {clip_start}..{clip_end}")

    fd, clipped_path = tempfile.mkstemp(suffix=".wav", prefix="learnmore_secondary_")
    os.close(fd)
    cmd = [
        "ffmpeg",
        "-y",
        "-ss",
        str(clip_start),
        "-to",
        str(clip_end),
        "-i",
        audio_file,
        "-acodec",
        "pcm_s16le",
        "-ar",
        "16000",
        "-ac",
        "1",
        clipped_path,
    ]
    subprocess.run(cmd, capture_output=True, check=True)
    return clipped_path


def main() -> int:
    if len(sys.argv) < 3:
        print(json.dumps({"error": "Usage: python openai_whisper_segments.py <audio_file> <output_json> [model]"}, ensure_ascii=False))
        return 1

    audio_file = sys.argv[1]
    output_json = sys.argv[2]
    model_name = sys.argv[3] if len(sys.argv) >= 4 else os.environ.get("LEARNMORE_SECONDARY_ALIGNMENT_MODEL", "small")
    clip_start = float(sys.argv[4]) if len(sys.argv) >= 5 else None
    clip_end = float(sys.argv[5]) if len(sys.argv) >= 6 else None

    if not os.path.exists(audio_file):
        print(json.dumps({"error": f"audio file not found: {audio_file}"}, ensure_ascii=False))
        return 2

    try:
        import whisper
    except Exception as exc:
        print(json.dumps({"error": f"import whisper failed: {exc}"}, ensure_ascii=False))
        return 3

    clip_offset = clip_start if clip_start is not None else 0.0
    transcribe_audio = audio_file
    temp_audio = None
    if clip_start is not None and clip_end is not None:
        temp_audio = clip_audio(audio_file, clip_start, clip_end)
        transcribe_audio = temp_audio

    model = whisper.load_model(model_name)
    result = model.transcribe(
        transcribe_audio,
        language="ja",
        fp16=False,
        verbose=False,
        condition_on_previous_text=False,
        temperature=0,
    )

    segments = []
    for seg in result.get("segments", []):
        text = (seg.get("text") or "").strip()
        if not text:
            continue
        segments.append(
            {
                "start": round(float(seg.get("start") or 0.0), 3),
                "end": round(float(seg.get("end") or 0.0), 3),
                "text": text,
            }
        )

    if clip_offset:
        for seg in segments:
            seg["start"] = round(seg["start"] + clip_offset, 3)
            seg["end"] = round(seg["end"] + clip_offset, 3)

    payload = {
        "language": result.get("language", "ja"),
        "segments": segments,
        "model": model_name,
    }

    with open(output_json, "w", encoding="utf-8") as fh:
        json.dump(payload, fh, ensure_ascii=False, indent=2)

    print(json.dumps({"success": True, "segments": len(segments), "output": output_json, "model": model_name}, ensure_ascii=False))
    if temp_audio and os.path.exists(temp_audio):
        os.remove(temp_audio)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
