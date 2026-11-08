import importlib.util
from pathlib import Path
from types import SimpleNamespace
import wave
import json

import pytest

spec = importlib.util.spec_from_file_location("qwen_cli", Path(__file__).parents[1] / "qwen-asr/qwen3_asr_cli.py")
cli = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cli)


def test_pcm_chunks_preserve_samples_and_frame_based_offsets(tmp_path):
    source = tmp_path / "source.wav"
    samples = bytes(range(14))
    with wave.open(str(source), "wb") as output:
        output.setnchannels(1)
        output.setsampwidth(2)
        output.setframerate(2)
        output.writeframes(samples)
    directory = tmp_path / "chunks"
    directory.mkdir()
    tasks = cli.chunk_audio([source], 2, directory)
    assert [(task[2], task[3]) for task in tasks] == [(0, 2), (2, 1.5)]
    restored = b""
    for _, path, _, duration in tasks:
        with wave.open(str(path)) as chunk:
            assert chunk.getnframes() / chunk.getframerate() == duration
            restored += chunk.readframes(chunk.getnframes())
    assert restored == samples


def result(start=0.2, end=0.8):
    return SimpleNamespace(language="English", text="Stand by", time_stamps={"items": [
        {"text": "Stand", "start_time": start, "end_time": end}]})


def test_chunk_merging_keeps_each_input_separate_and_restores_absolute_times():
    paths = [Path("a.wav"), Path("b.wav")]
    tasks = [(0, Path("a-30.wav"), 30, 2), (1, Path("b-0.wav"), 0, 2)]
    merged = cli.merge_chunks(paths, tasks, [result(), result()])
    assert merged[0]["timeStamps"]["items"][0]["start_time"] == 30.2
    assert merged[1]["timeStamps"]["items"][0]["start_time"] == 0.2


@pytest.mark.parametrize("start,end", [(-0.1, 0.5), (0.2, 2.1), (1.0, 0.5), (float("nan"), 1), (0.2, 0.2)])
def test_invalid_local_times_cannot_be_hidden_by_an_absolute_offset(start, end):
    with pytest.raises(ValueError):
        cli.merge_chunks([Path("a.wav")], [(0, Path("part.wav"), 30, 2)], [result(start, end)])


@pytest.mark.parametrize("tail_frames", [1067, 9499])
def test_short_tail_is_merged_without_losing_or_padding_audio(tmp_path, tail_frames):
    source = tmp_path / "source.wav"
    samples = b"\x01\x02" * (16000 * 30 + tail_frames)
    with wave.open(str(source), "wb") as output:
        output.setparams((1, 2, 16000, 0, "NONE", "not compressed"))
        output.writeframes(samples)
    tasks = cli.chunk_audio([source], 15, tmp_path)
    assert len(tasks) == 2 and tasks[-1][3] == 15 + tail_frames / 16000
    recovered = b""
    for _, path, _, duration in tasks:
        with wave.open(str(path)) as audio:
            recovered += audio.readframes(audio.getnframes())
    assert recovered == samples
    assert sum(task[3] for task in tasks) == len(samples) / 32000


def test_resume_reuses_valid_chunks_but_rechecks_contract_and_retains_failure(tmp_path):
    source = tmp_path / "source.wav"
    source.write_bytes(b"audio")
    tasks = [(0, source, 0, 2), (0, source, 2, 2), (0, source, 4, 2)]
    journal = tmp_path / "chunks.json"
    calls = []

    def transcribe(**kwargs):
        calls.append(kwargs)
        return [result(end=3 if len(calls) == 2 else 0.8)]

    model = SimpleNamespace(transcribe=transcribe)
    with pytest.raises(ValueError, match="incomplete"):
        cli.transcribe_chunks(model, [source], tasks, "English", journal)
    assert len(calls) == 3
    saved = json.loads(journal.read_text())
    assert [item["completed"] for item in saved["chunks"]] == [True, False, True]
    assert saved["chunks"][1]["error"] == "invalid_chunk_word_timing"
    cli.transcribe_chunks(model, [source], tasks, "English", journal, resume=True)
    assert len(calls) == 4
    assert json.loads(journal.read_text())["chunks"][1]["attempts"][0]["raw"]["time_stamps"]["items"][0]["end_time"] == 3
    with pytest.raises(ValueError, match="contract_mismatch"):
        cli.transcribe_chunks(model, [source], tasks, "Japanese", journal, resume=True)
    assert len(calls) == 4


def test_concurrent_chunk_resume_is_rejected(tmp_path):
    journal = tmp_path / "chunks.json"
    journal.with_name("chunks.json.lock").touch()
    with pytest.raises(FileExistsError):
        cli.transcribe_chunks(None, [], [], "English", journal)


def test_zero_duration_chunk_is_not_completed_and_valid_neighbors_are_reused(tmp_path):
    source = tmp_path / "source.wav"
    source.write_bytes(b"audio")
    tasks = [(0, source, 0, 2), (0, source, 2, 2), (0, source, 4, 2)]
    journal = tmp_path / "chunks.json"
    calls = []

    def transcribe(**kwargs):
        calls.append(kwargs)
        return [result(end=0.2 if len(calls) == 2 else 0.8)]

    model = SimpleNamespace(transcribe=transcribe)
    with pytest.raises(ValueError, match="chunk_transcription_incomplete"):
        cli.transcribe_chunks(model, [source], tasks, "English", journal)
    saved = json.loads(journal.read_text())
    assert [entry["completed"] for entry in saved["chunks"]] == [True, False, True]
    assert saved["chunks"][1]["error"] == "invalid_chunk_word_timing"
    cli.transcribe_chunks(model, [source], tasks, "English", journal, resume=True)
    assert len(calls) == 4
    saved = json.loads(journal.read_text())
    assert all(entry["completed"] for entry in saved["chunks"])
    assert saved["chunks"][1]["attempts"][0]["raw"]["time_stamps"]["items"][0]["end_time"] == 0.2


def test_resume_rejects_legacy_completed_zero_duration_without_inference(tmp_path):
    source = tmp_path / "source.wav"
    source.write_bytes(b"audio")
    tasks = [(0, source, 0, 2)]
    journal = tmp_path / "chunks.json"
    model = SimpleNamespace(transcribe=lambda **kwargs: [result()])
    cli.transcribe_chunks(model, [source], tasks, "English", journal)
    saved = json.loads(journal.read_text())
    saved["chunks"][0]["raw"]["time_stamps"]["items"][0]["end_time"] = 0.2
    journal.write_text(json.dumps(saved))
    with pytest.raises(ValueError, match="chunk_transcription_incomplete"):
        cli.transcribe_chunks(None, [source], tasks, "English", journal, resume=True)
    saved = json.loads(journal.read_text())
    assert saved["chunks"][0]["completed"] is False
    assert saved["chunks"][0]["error"] == "invalid_chunk_word_timing"
