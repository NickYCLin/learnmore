"""獨立的本機辨識工具；不讀網站憑證，也不寫入歌曲資料。"""
from __future__ import annotations

import argparse
import dataclasses
import hashlib
import json
import math
import os
from pathlib import Path
import sys
import tempfile
import wave
from types import SimpleNamespace

MODELS = {
    "Qwen/Qwen3-ASR-1.7B": "7278e1e70fe206f11671096ffdd38061171dd6e5",
    "Qwen/Qwen3-ForcedAligner-0.6B": "c7cbfc2048c462b0d63a45797104fc9db3ad62b7",
}


def model_paths(*, download: bool = False) -> list[str]:
    from huggingface_hub import snapshot_download

    return [snapshot_download(
        repo_id=name, revision=revision, token=False,
        local_files_only=not download, max_workers=2,
        allow_patterns=["*.json", "*.safetensors", "*.txt"],
    ) for name, revision in MODELS.items()]


def serializable(value):
    if isinstance(value, float) and not math.isfinite(value):
        return str(value)
    if dataclasses.is_dataclass(value):
        return dataclasses.asdict(value)
    if isinstance(value, (list, tuple)):
        return [serializable(item) for item in value]
    if isinstance(value, dict):
        return {key: serializable(item) for key, item in value.items()}
    if value is None or isinstance(value, (str, int, float, bool)):
        return value
    raise TypeError("unsupported_timestamp_result")


def chunk_audio(paths: list[Path], seconds: int, directory: Path):
    tasks = []
    for index, path in enumerate(paths):
        with wave.open(str(path)) as source:
            rate = source.getframerate()
            starts = list(range(0, source.getnframes(), rate * seconds))
            if len(starts) > 1 and source.getnframes() - starts[-1] < rate:
                starts.pop()
            for part, start in enumerate(starts):
                stop = starts[part + 1] if part + 1 < len(starts) else source.getnframes()
                frames = stop - start
                data = source.readframes(frames)
                target = directory / f"{index}-{part}.wav"
                with wave.open(str(target), "wb") as output:
                    output.setparams(source.getparams())
                    output.writeframes(data)
                tasks.append((index, target, start / rate, frames / rate))
    return tasks


def persist_journal(path: Path, payload):
    descriptor, temporary = tempfile.mkstemp(prefix=path.name, dir=path.parent)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8") as output:
            json.dump(payload, output, ensure_ascii=False, allow_nan=False)
            output.flush()
            os.fsync(output.fileno())
        os.replace(temporary, path)
    finally:
        Path(temporary).unlink(missing_ok=True)


def transcribe_chunks(model, paths, tasks, language, journal_path, *, resume=False, max_new_tokens=512):
    contract = {"models": MODELS, "language": language, "maxNewTokens": max_new_tokens,
                "inputs": [file_digest(path) for path in paths], "chunks": [
                    {"input": index, "sha256": file_digest(path), "offset": offset, "duration": duration}
                    for index, path, offset, duration in tasks]}
    lock = journal_path.with_name(journal_path.name + ".lock")
    descriptor = os.open(lock, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
    try:
        os.close(descriptor)
        if journal_path.is_symlink():
            raise ValueError("invalid_chunk_journal")
        if journal_path.exists():
            if not resume:
                raise ValueError("chunk_journal_exists")
            journal = json.loads(journal_path.read_text(encoding="utf-8"))
            if journal.get("contract") != contract or len(journal.get("chunks", [])) != len(tasks):
                raise ValueError("chunk_journal_contract_mismatch")
        else:
            journal = {"contract": contract, "verificationStatus": "unreviewed", "chunks": [{} for _ in tasks]}
        results = []
        failed = False
        for position, task in enumerate(tasks):
            entry = journal["chunks"][position]
            try:
                if not entry.get("completed"):
                    raw = model.transcribe(audio=[str(task[1])],
                                           language=None if language == "auto" else language,
                                           return_time_stamps=True)
                    if len(raw) != 1:
                        raise ValueError("chunk_result_count_mismatch")
                    history = entry.get("attempts", [])
                    if entry.get("raw"):
                        history = [*history, {"raw": entry["raw"], "error": entry.get("error")}]
                    entry = {"attempts": history, "raw": {"language": raw[0].language, "text": raw[0].text,
                                     "time_stamps": serializable(raw[0].time_stamps)}, "completed": False}
                    journal["chunks"][position] = entry
                    persist_journal(journal_path, journal)
                result = SimpleNamespace(**entry["raw"])
                merge_chunks(paths, [task], [result])
                entry.update(completed=True, error=None)
                results.append(result)
            except Exception as error:
                code = str(error) if isinstance(error, ValueError) and str(error) in {
                    "invalid_chunk_word_timing", "chunk_result_count_mismatch"} else "chunk_transcription_failed"
                entry.update(completed=False, error=code)
                journal["chunks"][position] = entry
                failed = True
                results.append(None)
            persist_journal(journal_path, journal)
        if failed:
            raise ValueError("chunk_transcription_incomplete")
        return results
    finally:
        lock.unlink(missing_ok=True)


def merge_chunks(paths: list[Path], tasks, results):
    merged = [{"language": "", "text": "", "timeStamps": {"items": []}} for _ in paths]
    if len(results) != len(tasks):
        raise RuntimeError("chunk_result_count_mismatch")
    for (index, _, offset, duration), result in zip(tasks, results):
        stamps = serializable(result.time_stamps) or {"items": []}
        previous = -1.0
        for word in stamps["items"]:
            start, end = word["start_time"], word["end_time"]
            if (type(start) not in {int, float} or type(end) not in {int, float}
                    or not math.isfinite(start) or not math.isfinite(end) or start < previous
                    or not 0 <= start < end <= duration):
                raise ValueError("invalid_chunk_word_timing")
            previous = start
            merged[index]["timeStamps"]["items"].append(
                dict(word, start_time=start + offset, end_time=end + offset))
        if result.text.strip():
            merged[index]["text"] += (" " if merged[index]["text"] else "") + result.text
            languages = set(filter(None, merged[index]["language"].split(",")))
            languages.add(result.language)
            merged[index]["language"] = ",".join(sorted(languages))
    return merged


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("download", help="匿名下載固定版本的公開模型")
    commands.add_parser("doctor", help="檢查 GPU 與離線模型快取")
    transcribe = commands.add_parser("transcribe", help="辨識本機音檔並輸出逐字時間戳")
    transcribe.add_argument("audio", nargs="+", type=Path)
    transcribe.add_argument("--language", choices=["auto", "Japanese", "English"], default="auto")
    transcribe.add_argument("--output", type=Path, required=True)
    transcribe.add_argument("--max-new-tokens", type=int, default=512)
    transcribe.add_argument("--chunk-seconds", type=int, default=0,
                            help="PCM WAV 可選擇分成 5–60 秒的獨立辨識片段")
    transcribe.add_argument("--resume-chunks", action="store_true")
    args = parser.parse_args(argv)
    if args.command == "download":
        model_paths(download=True)
        print(json.dumps({"downloaded": list(MODELS), "revisions": MODELS}))
        return 0

    if args.command == "transcribe":
        if not 1 <= args.max_new_tokens <= 4096:
            parser.error("max-new-tokens 必須介於 1 與 4096")
        if args.chunk_seconds != 0 and not 5 <= args.chunk_seconds <= 60:
            parser.error("chunk-seconds 必須為 0 或介於 5 與 60")
        if any(not path.is_file() for path in args.audio):
            parser.error("音檔不存在或不是一般檔案")
        if args.output.exists() or args.output.is_symlink():
            parser.error("輸出檔案已存在，請另選檔名以保留原紀錄")
        if not args.output.parent.is_dir():
            parser.error("輸出目錄不存在")
        digests = [file_digest(path) for path in args.audio]

    # 辨識與檢查均只讀已下載模型；不使用登入資訊或自動連網補檔。
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["HF_HUB_DISABLE_IMPLICIT_TOKEN"] = "1"
    import torch

    if not torch.cuda.is_available():
        raise RuntimeError("cuda_unavailable")
    torch.set_num_threads(4)
    asr_path, aligner_path = model_paths()
    if args.command == "doctor":
        test = torch.ones((2, 2), device="cuda", dtype=torch.bfloat16)
        assert (test @ test).sum().item() == 8
        print(json.dumps({"cuda": torch.version.cuda, "torch": torch.__version__,
                          "device": torch.cuda.get_device_name(), "offlineModelsReady": True,
                          "revisions": MODELS}))
        return 0

    from qwen_asr import Qwen3ASRModel

    kwargs = dict(dtype=torch.bfloat16, device_map="cuda:0", attn_implementation="sdpa",
                  local_files_only=True)
    model = Qwen3ASRModel.from_pretrained(
        asr_path, **kwargs, max_inference_batch_size=1, max_new_tokens=args.max_new_tokens,
        forced_aligner=aligner_path, forced_aligner_kwargs=kwargs,
    )
    with tempfile.TemporaryDirectory(prefix="learnmore-qwen-chunks-") as directory:
        tasks = (chunk_audio(args.audio, args.chunk_seconds, Path(directory)) if args.chunk_seconds
                 else [(i, path, 0, 0) for i, path in enumerate(args.audio)])
        raw_results = (transcribe_chunks(
            model, args.audio, tasks, args.language,
            args.output.with_name(args.output.name + ".chunks.json"),
            resume=args.resume_chunks, max_new_tokens=args.max_new_tokens) if args.chunk_seconds else
            model.transcribe(audio=[str(task[1]) for task in tasks],
                             language=None if args.language == "auto" else args.language,
                             return_time_stamps=True))
        if len(raw_results) != len(tasks):
            raise RuntimeError("transcript_count_mismatch")
        results = (merge_chunks(args.audio, tasks, raw_results) if args.chunk_seconds else [
            {"language": result.language, "text": result.text,
             "timeStamps": serializable(result.time_stamps)} for result in raw_results])
    if any(file_digest(path) != digest for path, digest in zip(args.audio, digests)):
        raise RuntimeError("audio_changed_during_transcription")
    payload = {"models": MODELS, "backend": "transformers-cuda-bfloat16",
               "chunkSeconds": args.chunk_seconds,
               "verificationStatus": "unreviewed", "results": [
                   {"file": path.name, "fileSha256": digest,
                    **result}
                   for path, result, digest in zip(args.audio, results, digests)
               ]}
    # 不覆寫既有轉寫，也不將模型輸出直接當成歌詞驗收通過。
    with args.output.open("x", encoding="utf-8") as output:
        json.dump(payload, output, ensure_ascii=False, indent=2, allow_nan=False)
        output.write("\n")
    print(json.dumps({"transcribedFiles": len(results), "verificationStatus": "unreviewed"}))
    return 0


def file_digest(path: Path) -> str:
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        print(f"qwen_asr_failed: {type(error).__name__}", file=sys.stderr)
        raise SystemExit(1)
