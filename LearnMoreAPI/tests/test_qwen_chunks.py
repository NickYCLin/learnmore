import importlib.util
from pathlib import Path
from types import SimpleNamespace
import wave

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


@pytest.mark.parametrize("start,end", [(-0.1, 0.5), (0.2, 2.1), (1.0, 0.5), (float("nan"), 1)])
def test_invalid_local_times_cannot_be_hidden_by_an_absolute_offset(start, end):
    with pytest.raises(ValueError):
        cli.merge_chunks([Path("a.wav")], [(0, Path("part.wav"), 30, 2)], [result(start, end)])
