#!/usr/bin/env python3
"""
用 faster-whisper 產生 word-level timestamps，輸出 JSON 給 LearnMore C# 端使用。

Usage:
    python faster_whisper_words.py <audio_file> <output_json>
"""

import json
import os
import sys


def main() -> int:
    if len(sys.argv) < 3:
        print(json.dumps({"error": "Usage: python faster_whisper_words.py <audio_file> <output_json>"}, ensure_ascii=False))
        return 1

    audio_file = sys.argv[1]
    output_json = sys.argv[2]
    if not os.path.exists(audio_file):
        print(json.dumps({"error": f"audio file not found: {audio_file}"}, ensure_ascii=False))
        return 2

    try:
        from faster_whisper import WhisperModel
    except Exception as exc:
        print(json.dumps({"error": f"import faster_whisper failed: {exc}"}, ensure_ascii=False))
        return 3

    # 正式站 CPU 實測：small 模型在 live precision flow 常超時；tiny 約 134s 可完成 4 分鐘歌曲 word timestamps。
    model_name = os.environ.get("LEARNMORE_FASTER_WHISPER_MODEL", "tiny")
    device = os.environ.get("LEARNMORE_FASTER_WHISPER_DEVICE", "cpu")
    compute_type = os.environ.get("LEARNMORE_FASTER_WHISPER_COMPUTE", "int8")

    model = WhisperModel(model_name, device=device, compute_type=compute_type)
    segments, info = model.transcribe(
        audio_file,
        language="ja",
        vad_filter=False,
        word_timestamps=True,
        beam_size=5,
        condition_on_previous_text=False,
        temperature=0,
    )

    words = []
    for segment in segments:
        for word in (segment.words or []):
            token = (word.word or "").strip()
            if not token:
                continue
            words.append(
                {
                    "word": token,
                    "start": round(float(word.start or 0.0), 3),
                    "end": round(float(word.end or 0.0), 3),
                    "probability": round(float(getattr(word, "probability", 0.0) or 0.0), 4),
                }
            )

    payload = {
        "language": getattr(info, "language", "ja"),
        "duration": round(float(getattr(info, "duration", 0.0) or 0.0), 3),
        "words": words,
    }

    with open(output_json, "w", encoding="utf-8") as fh:
        json.dump(payload, fh, ensure_ascii=False, indent=2)

    print(json.dumps({"success": True, "words": len(words), "output": output_json}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
