"""獨立的本機辨識工具；不讀網站憑證，也不寫入歌曲資料。"""
from __future__ import annotations

import argparse
import dataclasses
import hashlib
import json
import os
from pathlib import Path
import sys

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
    if dataclasses.is_dataclass(value):
        return dataclasses.asdict(value)
    if isinstance(value, (list, tuple)):
        return [serializable(item) for item in value]
    if isinstance(value, dict):
        return {key: serializable(item) for key, item in value.items()}
    if value is None or isinstance(value, (str, int, float, bool)):
        return value
    raise TypeError("unsupported_timestamp_result")


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
    args = parser.parse_args(argv)
    if args.command == "download":
        model_paths(download=True)
        print(json.dumps({"downloaded": list(MODELS), "revisions": MODELS}))
        return 0

    if args.command == "transcribe":
        if not 1 <= args.max_new_tokens <= 4096:
            parser.error("max-new-tokens 必須介於 1 與 4096")
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
    results = model.transcribe(audio=[str(path) for path in args.audio],
                               language=None if args.language == "auto" else args.language,
                               return_time_stamps=True)
    if len(results) != len(args.audio):
        raise RuntimeError("transcript_count_mismatch")
    if any(file_digest(path) != digest for path, digest in zip(args.audio, digests)):
        raise RuntimeError("audio_changed_during_transcription")
    payload = {"models": MODELS, "backend": "transformers-cuda-bfloat16",
               "verificationStatus": "unreviewed", "results": [
                   {"file": path.name, "fileSha256": digest,
                    "language": result.language, "text": result.text,
                    "timeStamps": serializable(result.time_stamps)}
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
