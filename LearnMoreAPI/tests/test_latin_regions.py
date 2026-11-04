import hashlib
import json
import subprocess
import wave

import pytest

from learnmore_api import main, qwen_evidence as qwen
from test_qwen_evidence import report


def reliable(row):
    return row["source"] in {"qwen3_asr_word_match", "whisper_word_match"}


def fixture_rows(text="Stand by", lyric_id=7, hint=10):
    lyric = {"lyricId": lyric_id, "japanese": text, "currentStart": hint}
    row = dict(lyric, start=hint, end=hint + 2, source="whisperx_contextual_timestamp_bridge", score=1)
    return [lyric], [row]


def audio_fixture(path):
    with wave.open(str(path), "wb") as output:
        output.setnchannels(1)
        output.setsampwidth(2)
        output.setframerate(10)
        output.writeframes(bytes(range(200)) * 3)
    return path


@pytest.mark.parametrize("language", ["English,Japanese", "Japanese,English", "English", "Japanese", ""])
def test_chunk_language_contract_accepts_only_supported_languages(tmp_path, language):
    audio = tmp_path / "audio.wav"
    audio.write_bytes(b"fixture")
    payload = report(audio)
    payload["results"][0]["language"] = language
    assert len(qwen.parse_result(payload, audio, 5)) == 2


@pytest.mark.parametrize("language", ["English,", ",Japanese", "English,English", "Chinese", "Japanese,French"])
def test_malformed_or_other_languages_are_rejected(tmp_path, language):
    audio = tmp_path / "audio.wav"
    audio.write_bytes(b"fixture")
    payload = report(audio)
    payload["results"][0]["language"] = language
    with pytest.raises(ValueError, match="unsupported_transcript"):
        qwen.parse_result(payload, audio, 5)


def test_forced_alignment_zero_spans_are_not_audio_evidence(tmp_path):
    audio = tmp_path / "audio.wav"
    audio.write_bytes(b"fixture")
    payload = report(audio)
    payload["results"][0]["timeStamps"]["items"][0]["end_time"] = 2
    with pytest.raises(ValueError, match="invalid_word_timing"):
        qwen.parse_result(payload, audio, 5)


@pytest.mark.parametrize("lyric_id", [9, 10, 14, 15, 16, 17, 50, 23, 26, 29, 30, 7, 16, 29])
def test_reported_gap_ids_use_local_english_not_japanese_asr(lyric_id):
    lyrics, rows = fixture_rows(lyric_id=lyric_id)
    assert qwen.latin_regions(lyrics, rows, 30, reliable, main.is_latin_dominant_lyric) == [
        {"index": 0, "start": 7, "end": 22, "language": "en"}]


def test_regions_preserve_primary_anchors_and_boundaries():
    lyrics, rows = fixture_rows("Stand by 君", hint=10)
    previous = dict(rows[0], lyricId=1, japanese="前", start=5, end=9, source="whisper_word_match")
    following = dict(rows[0], lyricId=20, japanese="後", start=13, end=15, source="whisper_word_match")
    lyrics = [dict(previous), *lyrics, dict(following)]
    rows = [previous, *rows, following]
    assert qwen.latin_regions(lyrics, rows, 30, reliable, main.is_latin_dominant_lyric) == [
        {"index": 1, "start": 9, "end": 13, "language": "auto"}]
    lyrics[1]["currentStart"] = 8
    assert qwen.latin_regions(lyrics, rows, 30, reliable, main.is_latin_dominant_lyric) == []


def test_halfwidth_japanese_in_latin_line_uses_automatic_language():
    lyrics, rows = fixture_rows("Stand by ｷﾐ")
    assert qwen.latin_regions(lyrics, rows, 30, reliable, main.is_latin_dominant_lyric)[0]["language"] == "auto"


@pytest.mark.parametrize("language,expected", [("en", "English"), ("auto", "auto")])
def test_local_commands_receive_only_audio_and_language(tmp_path, monkeypatch, language, expected):
    def run(arguments, **kwargs):
        assert arguments == ["fixture", "transcribe", "first.wav", "second.wav", "--language", expected,
                             "--max-new-tokens", "512", "--chunk-seconds", "30", "--output", str(tmp_path / "out.json")]
        assert kwargs["timeout"] == 5

    monkeypatch.setattr(qwen.subprocess, "run", run)
    from pathlib import Path
    qwen.run_transcription([Path("first.wav"), Path("second.wav")], tmp_path / "out.json", ["fixture"], 5, language)


def test_expired_budget_never_starts_local_inference(tmp_path, monkeypatch):
    audio = audio_fixture(tmp_path / "vocals.wav")
    lyrics, rows = fixture_rows()
    monkeypatch.setattr(qwen, "run_transcription", lambda *args: pytest.fail("unexpected inference"))
    with pytest.raises(subprocess.TimeoutExpired):
        qwen.supplement_latin_regions(audio, 30, ["fixture"], 0, lyrics, rows,
                                     reliable, main.is_latin_dominant_lyric)


@pytest.mark.parametrize("mode", ["match", "hash", "negative", "wrong_words", "repeated", "timeout", "count"])
def test_crop_evidence_is_hashed_bounded_and_unambiguous(tmp_path, monkeypatch, mode):
    audio = audio_fixture(tmp_path / "vocals.wav")
    original_digest = hashlib.sha256(audio.read_bytes()).hexdigest()
    lyrics, rows = fixture_rows(hint=10.03)

    def run(paths, output, command, timeout, language):
        assert language == "en" and len(paths) == 1 and 0 < timeout <= 20
        with wave.open(str(paths[0])) as crop, wave.open(str(audio)) as original:
            original.setpos(71)
            assert crop.readframes(crop.getnframes()) == original.readframes(149)
        if mode == "timeout":
            raise subprocess.TimeoutExpired("fixture", timeout)
        payload = report(paths[0])
        row = payload["results"][0]
        for word in row["timeStamps"]["items"]:
            word["start_time"] += 1
            word["end_time"] += 1
        if mode == "hash":
            row["fileSha256"] = "0" * 64
        if mode == "negative":
            row["timeStamps"]["items"][0]["start_time"] = -1
        if mode == "wrong_words":
            row["text"] = "Stand high"
            row["timeStamps"]["items"][-1]["text"] = "high"
        if mode == "repeated":
            row["text"] += " Stand by"
            row["timeStamps"]["items"] += [dict(word, start_time=word["start_time"] + 1,
                end_time=word["end_time"] + 1) for word in list(row["timeStamps"]["items"])]
        if mode == "count":
            payload["results"] = []
        output.write_text(json.dumps(payload))

    monkeypatch.setattr(qwen, "run_transcription", run)
    if mode in {"hash", "negative", "timeout", "count"}:
        with pytest.raises((ValueError, subprocess.TimeoutExpired)):
            qwen.supplement_latin_regions(audio, 30, ["fixture"], 20, lyrics, rows,
                                          reliable, main.is_latin_dominant_lyric)
    else:
        result = qwen.supplement_latin_regions(audio, 30, ["fixture"], 20, lyrics, rows,
                                              reliable, main.is_latin_dominant_lyric)
        if mode == "match":
            assert result[0]["source"] == "qwen3_asr_word_match"
            assert result[0]["start"] == pytest.approx(10.1)
            assert result[0]["end"] == pytest.approx(10.8)
        else:
            assert result == rows
    assert hashlib.sha256(audio.read_bytes()).hexdigest() == original_digest


def test_words_from_different_crops_cannot_form_a_match(tmp_path, monkeypatch):
    audio = audio_fixture(tmp_path / "vocals.wav")
    lyrics, rows = fixture_rows()
    second_lyrics, second_rows = fixture_rows(lyric_id=16, hint=12)
    lyrics += second_lyrics
    rows += second_rows

    def run(paths, output, *args):
        payload = report(paths[0])
        payload["results"] = []
        for path, text in zip(paths, ["Stand", "by"]):
            row = report(path)["results"][0]
            row.update(text=text, timeStamps={"items": [{"text": text, "start_time": 3, "end_time": 3.5}]})
            payload["results"].append(row)
        output.write_text(json.dumps(payload))

    monkeypatch.setattr(qwen, "run_transcription", run)
    assert qwen.supplement_latin_regions(audio, 30, ["fixture"], 20, lyrics, rows,
                                        reliable, main.is_latin_dominant_lyric) == rows


def test_verified_rows_do_not_start_a_subprocess(tmp_path, monkeypatch):
    lyrics, rows = fixture_rows()
    rows[0]["source"] = "whisper_word_match"
    monkeypatch.setattr(qwen, "run_transcription", lambda *args: pytest.fail("unexpected inference"))
    assert qwen.supplement_latin_regions(tmp_path / "unused", 30, ["fixture"], 20, lyrics, rows,
                                        reliable, main.is_latin_dominant_lyric) == rows


def test_bad_primary_report_does_not_suppress_local_evidence(tmp_path, monkeypatch):
    audio = audio_fixture(tmp_path / "vocals.wav")
    lyrics, rows = fixture_rows()
    transcript = main.TranscribeYouTubeResponse(songUid="fixture", youtubeUrl="https://www.youtube.com/watch?v=qbcqvrV6-2Q",
        language="ja", model="primary", durationSeconds=30, segments=[])
    transcript._vocals_path = audio
    request = main.HighAccuracyAlignRequest(songUid="fixture", youtubeUrl=transcript.youtubeUrl,
        language="ja", lyrics=[main.HighAccuracyLyricLine(**lyrics[0])])
    original = main.AlignedLyricLine(**rows[0])
    monkeypatch.setattr(main, "transcribe_youtube_for_high_accuracy_sync", lambda *args: transcript)
    monkeypatch.setattr(main, "align_high_accuracy_lyrics_to_whisper", lambda *args: [original])
    monkeypatch.setattr(main, "stabilize_unreliable_lines_with_current_start_context", lambda lyrics, rows, duration: rows)
    monkeypatch.setattr(qwen, "transcribe", lambda *args: (_ for _ in ()).throw(ValueError("invalid_word_timing")))

    def local(audio_path, duration, command, timeout, lyric_rows, aligned, *args):
        assert 0 < timeout <= 20
        return [dict(aligned[0], start=10, end=11, source="qwen3_asr_word_match", score=1)]

    monkeypatch.setattr(qwen, "supplement_latin_regions", local)
    response = main.high_accuracy_align_sync(request, main.Settings(api_token="test-only",
        qwen_asr_command=["fixture"], qwen_asr_timeout_seconds=20))
    assert response.matchedCount == 1
    assert "+qwen3-asr-unavailable" in response.model
