#!/usr/bin/env python3
"""
歌詞對齊腳本 - 使用 whisper-timestamped 從 YouTube 音訊生成精確時間戳
"""

import json
import re
import sys
import argparse
from pathlib import Path
from difflib import SequenceMatcher

def normalize_japanese(text):
    """正規化日文文字，移除標點符號"""
    return re.sub(r'[^\u3040-\u309F\u30A0-\u30FF\u4E00-\u9FFF\u3400-\u4DBF]', '', text or '')

def load_whisper_result(json_path):
    """載入 whisper-timestamped 結果"""
    with open(json_path, 'r', encoding='utf-8') as f:
        return json.load(f)

def extract_lines_from_whisper(result, min_confidence=0.5):
    """從 whisper-timestamped 結果提取行級時間戳"""
    lines = []

    for seg in result.get('segments', []):
        text = seg.get('text', '').strip()

        # 跳過雜訊
        if not text or '作詞' in text or '編曲' in text or '初音' in text:
            continue

        # 計算平均 confidence
        words = seg.get('words', [])
        if words:
            avg_conf = sum(w.get('confidence', 0) for w in words) / len(words)
        else:
            avg_conf = 0

        if avg_conf < min_confidence:
            continue

        lines.append({
            'start': seg['start'],
            'end': seg['end'],
            'text': text,
            'words': words,
            'confidence': avg_conf
        })

    return lines

def align_lyrics(whisper_lines, lrclib_lines, threshold=0.4):
    """
    對齊 Whisper 辨識結果和 LrcLib 歌詞
    使用 Whisper 的時間戳，LrcLib 的翻譯
    """
    aligned = []

    for lrc in lrclib_lines:
        lrc_norm = normalize_japanese(lrc.get('japanese', ''))
        if not lrc_norm:
            continue

        best_match = None
        best_score = 0

        for wh in whisper_lines:
            wh_norm = normalize_japanese(wh['text'])

            # 計算相似度
            score = SequenceMatcher(None, lrc_norm, wh_norm).ratio()

            # 檢查包含關係
            if lrc_norm in wh_norm or wh_norm in lrc_norm:
                shorter = min(len(lrc_norm), len(wh_norm))
                longer = max(len(lrc_norm), len(wh_norm))
                contain_score = shorter / longer + 0.3
                score = max(score, contain_score)

            if score > best_score and score >= threshold:
                best_score = score
                best_match = wh

        if best_match:
            aligned.append({
                'start': best_match['start'],
                'end': best_match['end'],
                'japanese': lrc.get('japanese', ''),
                'chinese': lrc.get('chinese', ''),
                'confidence': best_match['confidence'],
                'match_score': best_score
            })
        else:
            # 沒找到匹配，使用 LrcLib 原始時間
            aligned.append({
                'start': lrc.get('timestamp', 0),
                'end': lrc.get('timestamp', 0) + 5,
                'japanese': lrc.get('japanese', ''),
                'chinese': lrc.get('chinese', ''),
                'confidence': 0,
                'match_score': 0
            })

    # 按時間排序
    aligned.sort(key=lambda x: x['start'])

    return aligned

def generate_romaji(japanese_text):
    """生成羅馬拼音 (簡化版，實際應使用 pykakasi)"""
    # 這裡只是佔位，實際應該用 pykakasi 或類似工具
    return ""

def main():
    parser = argparse.ArgumentParser(description='歌詞對齊工具')
    parser.add_argument('--whisper', required=True, help='whisper-timestamped JSON 檔案')
    parser.add_argument('--lrclib', required=True, help='LrcLib JSON 檔案')
    parser.add_argument('--output', required=True, help='輸出 JSON 檔案')

    args = parser.parse_args()

    # 載入 Whisper 結果
    whisper_result = load_whisper_result(args.whisper)
    whisper_lines = extract_lines_from_whisper(whisper_result)

    print(f"Whisper 辨識到 {len(whisper_lines)} 行")

    # 載入 LrcLib 歌詞
    with open(args.lrclib, 'r', encoding='utf-8') as f:
        lrclib_data = json.load(f)

    print(f"LrcLib 有 {len(lrclib_data)} 行")

    # 對齊
    aligned = align_lyrics(whisper_lines, lrclib_data)

    print(f"對齊完成，共 {len(aligned)} 行")

    # 輸出
    with open(args.output, 'w', encoding='utf-8') as f:
        json.dump(aligned, f, ensure_ascii=False, indent=2)

    print(f"結果已儲存到 {args.output}")

    # 顯示結果
    print("\n對齊結果預覽：")
    print("-" * 80)
    for i, line in enumerate(aligned[:10], 1):
        mins = int(line['start'] // 60)
        secs = line['start'] % 60
        jp = line['japanese'][:30]
        zh = line['chinese'][:20] if line['chinese'] else ''
        score = line['match_score']
        print(f"{i:2}. [{mins}:{secs:05.2f}] {jp:<32} → {zh} (score:{score:.2f})")

if __name__ == '__main__':
    main()
