import copy
import hashlib
import json
import subprocess

import pytest

from learnmore_api import main, qwen_evidence as qwen


def report(audio):
    return {"models": qwen.MODELS, "backend": "transformers-cuda-bfloat16",
            "verificationStatus": "unreviewed", "results": [{
                "file": audio.name, "fileSha256": hashlib.sha256(audio.read_bytes()).hexdigest(),
                "language": "English", "text": "Stand by",
                "timeStamps": {"items": [{"text": "Stand", "start_time": 2, "end_time": 2.4},
                                         {"text": "by", "start_time": 2.4, "end_time": 2.7}]}}]}


@pytest.mark.parametrize("change", ["hash", "model", "missing_word", "infinite", "negative",
                                   "beyond_audio", "reversed", "wrong_result", "wrong_file"])
def test_wrong_audio_or_incomplete_evidence_is_rejected(tmp_path, change):
    audio = tmp_path / "vocals.wav"
    audio.write_bytes(b"audio evidence")
    payload = copy.deepcopy(report(audio))
    result = payload["results"][0]
    items = result["timeStamps"]["items"]
    if change == "hash": result["fileSha256"] = "0" * 64
    if change == "model": payload["models"] = {}
    if change == "missing_word": items.pop()
    if change == "infinite": items[0]["start_time"] = float("nan")
    if change == "negative": items[0]["start_time"] = -1
    if change == "beyond_audio": items[-1]["end_time"] = 6
    if change == "reversed": items.reverse()
    if change == "wrong_result": payload["results"].append(result)
    if change == "wrong_file": result["file"] = "other.wav"
    with pytest.raises(ValueError):
        qwen.parse_result(payload, audio, 5)


def test_subprocess_receives_audio_without_lyrics_or_website_credentials(tmp_path, monkeypatch):
    audio = tmp_path / "vocals.wav"
    audio.write_bytes(b"audio evidence")
    monkeypatch.setenv("LEARNMORE_API_TOKEN", "private-test-placeholder")
    monkeypatch.setenv("HF_TOKEN", "private-test-placeholder")
    monkeypatch.setenv("NVIDIA_API_KEY", "private-test-placeholder")

    def run(args, **kwargs):
        assert args[:2] == ["python", "local-asr.py"]
        assert args[2:7] == ["transcribe", str(audio), "--language", "Japanese", "--max-new-tokens"]
        assert "LEARNMORE_API_TOKEN" not in kwargs["env"] and "HF_TOKEN" not in kwargs["env"]
        assert "NVIDIA_API_KEY" not in kwargs["env"]
        assert kwargs["env"]["HF_HUB_OFFLINE"] == "1" and kwargs["timeout"] == 17
        from pathlib import Path
        Path(args[-1]).write_text(json.dumps(report(audio)))

    monkeypatch.setattr(qwen.subprocess, "run", run)
    words = qwen.transcribe(audio, 5, ["python", "local-asr.py"], 17)
    assert [w["text"] for w in words] == ["Stand", "by"]


def test_repeated_words_and_partial_word_matches_are_not_promoted(tmp_path):
    audio = tmp_path / "vocals.wav"
    audio.write_bytes(b"audio evidence")
    words = qwen.parse_result(report(audio), audio, 5)
    lyric = {"lyricId": 1, "japanese": "Stand by", "currentStart": 2.5}
    original = dict(lyric, start=2.5, end=4, source="current_timestamp_context", score=0)
    reliable = lambda row: row["source"] == "qwen3_asr_word_match"
    repeated = words + [dict(w, index=w["index"] + 2, start=w["start"] + 1, end=w["end"] + 1) for w in words]
    assert qwen.supplement([lyric], [original], repeated, reliable) == [original]
    words[-1]["text"] = "bye"
    assert qwen.supplement([lyric], [original], words, reliable) == [original]


@pytest.mark.parametrize("mode", ["match", "wrong_words", "timeout", "disabled", "already_verified"])
def test_high_accuracy_pipeline_preserves_primary_results_and_fails_closed(tmp_path, monkeypatch, mode):
    audio = tmp_path / "vocals.wav"
    audio.write_bytes(b"audio evidence")
    transcript = main.TranscribeYouTubeResponse(songUid="qwen-test", youtubeUrl="https://www.youtube.com/watch?v=qbcqvrV6-2Q",
        language="ja", model="primary", durationSeconds=5, segments=[])
    transcript._vocals_path = audio
    request = main.HighAccuracyAlignRequest(songUid="qwen-test", youtubeUrl=transcript.youtubeUrl,
        language="ja", lyrics=[main.HighAccuracyLyricLine(lyricId=1, japanese="Stand by", currentStart=2.5)])
    original = main.AlignedLyricLine(lyricId=1, japanese="Stand by", start=2.5, end=3.5,
        source="whisper_word_match" if mode == "already_verified" else "current_timestamp_context", score=1)
    monkeypatch.setattr(main, "transcribe_youtube_for_high_accuracy_sync", lambda *args: transcript)
    monkeypatch.setattr(main, "align_high_accuracy_lyrics_to_whisper", lambda *args: [original])
    monkeypatch.setattr(main, "stabilize_unreliable_lines_with_current_start_context", lambda lyrics, rows, duration: rows)

    def transcribe(*args):
        assert mode not in {"disabled", "already_verified"}
        if mode == "timeout": raise subprocess.TimeoutExpired("local-asr", 1)
        payload = report(audio)
        if mode == "wrong_words":
            payload["results"][0]["text"] = "Stand high"
            payload["results"][0]["timeStamps"]["items"][-1]["text"] = "high"
        return qwen.parse_result(payload, audio, 5)

    monkeypatch.setattr(qwen, "transcribe", transcribe)
    response = main.high_accuracy_align_sync(request, main.Settings(api_token="test-only",
        qwen_asr_command=[] if mode == "disabled" else ["local-asr"]))
    assert response.matchedCount == (1 if mode in {"match", "already_verified"} else 0)
    if mode == "match":
        assert response.alignedLines[0].source == "qwen3_asr_word_match"
        assert response.alignedLines[0].start == 2
    if mode == "already_verified":
        assert response.alignedLines[0] == original
    if mode == "timeout":
        assert response.model.endswith("+qwen3-asr-unavailable+high-accuracy-align")


@pytest.mark.parametrize("raw", ['"python local.py"', '{}', '[""]', 'not-json'])
def test_command_configuration_is_an_argument_array(raw):
    with pytest.raises(ValueError):
        qwen.load_command(raw)


def test_health_reports_opt_in_without_exposing_command():
    import asyncio
    body = asyncio.run(main.health(main.Settings(api_token="test-only",
                                                qwen_asr_command=["python", "local-asr.py"])))
    assert body["qwenSupplementalConfigured"] is True
    assert "local-asr.py" not in json.dumps(body)
