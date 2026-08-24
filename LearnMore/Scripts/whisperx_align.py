#!/usr/bin/env python3
"""
WhisperX Forced Alignment：用音訊 + 正確歌詞文本，產生精準的逐行時間戳。

Usage:
    python whisperx_align.py <audio_file> <lyrics_file> <output_json>

Input:
    - audio_file: WAV/MP3 音訊檔路徑
    - lyrics_file: 純文字歌詞（每行一句日文）
    - output_json: 輸出 JSON 路徑

Output JSON 格式:
    [{"start": 0.72, "end": 3.04, "text": "無敵の笑顔で荒らすメディア"}, ...]
"""

import sys
import json
import os

def main():
    if len(sys.argv) < 4:
        print("Usage: python whisperx_align.py <audio_file> <lyrics_file> <output_json>")
        sys.exit(1)

    audio_file = sys.argv[1]
    lyrics_file = sys.argv[2]
    output_json = sys.argv[3]

    if not os.path.exists(audio_file):
        print(json.dumps({"error": f"Audio file not found: {audio_file}"}))
        sys.exit(1)

    if not os.path.exists(lyrics_file):
        print(json.dumps({"error": f"Lyrics file not found: {lyrics_file}"}))
        sys.exit(1)

    import whisperx
    import torch

    device = "cuda" if torch.cuda.is_available() else "cpu"
    compute_type = "float16" if device == "cuda" else "int8"

    # 載入音訊
    audio = whisperx.load_audio(audio_file)
    total_duration = len(audio) / 16000

    # 讀歌詞
    with open(lyrics_file, 'r', encoding='utf-8') as f:
        lines = [l.strip() for l in f if l.strip()]

    if not lines:
        print(json.dumps({"error": "No lyrics found"}))
        sys.exit(1)

    # 建構 segments（均分時間範圍，讓 forced alignment 精準對齊）
    interval = total_duration / len(lines)
    segments = []
    for i, line in enumerate(lines):
        segments.append({
            "text": line,
            "start": i * interval,
            "end": (i + 1) * interval,
        })

    # Forced alignment
    model_a, metadata = whisperx.load_align_model(language_code="ja", device=device)
    aligned = whisperx.align(segments, model_a, metadata, audio, device, return_char_alignments=False)

    # 輸出結果
    result = []
    for seg in aligned.get('segments', []):
        result.append({
            "start": round(seg.get('start', 0), 2),
            "end": round(seg.get('end', 0), 2),
            "text": seg.get('text', ''),
        })

    with open(output_json, 'w', encoding='utf-8') as f:
        json.dump(result, f, ensure_ascii=False, indent=2)

    print(json.dumps({"success": True, "segments": len(result), "duration": round(total_duration, 1)}))

if __name__ == "__main__":
    main()
