from __future__ import annotations

import asyncio
import difflib
import html
import json
import math
import os
import re
import shlex
import shutil
import sys
import statistics
import subprocess
import tempfile
import urllib.parse
import urllib.request
from contextlib import asynccontextmanager
from functools import lru_cache
from pathlib import Path
from typing import Any, AsyncIterator

from fastapi import Depends, FastAPI, Header, HTTPException, Query, status
from fastapi.responses import FileResponse
from pydantic import BaseModel, Field, PrivateAttr


class LyricLine(BaseModel):
    lyricId: int = Field(..., ge=1)
    japanese: str = Field(..., min_length=1)


class TranslateLinesRequest(BaseModel):
    songUid: str | None = None
    lines: list[LyricLine] = Field(..., min_length=1, max_length=120)


class TranslationLine(BaseModel):
    lyricId: int
    japanese: str
    chinese: str


class TranslateLinesResponse(BaseModel):
    songUid: str | None
    translations: list[TranslationLine]


class AnnotationLine(BaseModel):
    lyricId: int
    japanese: str
    japaneseRuby: str
    roman: str


class AnnotateLinesResponse(BaseModel):
    songUid: str | None
    annotations: list[AnnotationLine]


class SongAliasCandidate(BaseModel):
    songUid: str = Field(..., min_length=1)
    title: str = Field(..., min_length=1)
    artist: str = ""
    performer: str = ""
    youtubeUrl: str = ""


class GenerateSongAliasesRequest(BaseModel):
    songs: list[SongAliasCandidate] = Field(..., min_length=1, max_length=50)


class GeneratedSongAlias(BaseModel):
    aliasText: str
    aliasType: str
    note: str = ""


class GeneratedSongAliasItem(BaseModel):
    songUid: str
    aliases: list[GeneratedSongAlias]


class GenerateSongAliasesResponse(BaseModel):
    songs: list[GeneratedSongAliasItem]


class TranscribeYouTubeRequest(BaseModel):
    songUid: str | None = None
    youtubeUrl: str = Field(..., min_length=1)
    language: str = Field(default="ja", min_length=2, max_length=8)


class WhisperSegment(BaseModel):
    index: int
    start: float
    end: float
    text: str
    words: list["WhisperWord"] = Field(default_factory=list)
    _asr_anchor_score: float | None = PrivateAttr(default=None)
    _asr_anchor_source: str | None = PrivateAttr(default=None)
    _forced_lyric_alignment: bool = PrivateAttr(default=False)


class WhisperWord(BaseModel):
    index: int
    start: float
    end: float
    text: str


class TranscribeYouTubeResponse(BaseModel):
    songUid: str | None
    youtubeUrl: str
    language: str
    model: str
    durationSeconds: float | None
    segments: list[WhisperSegment]


class SeparateYouTubeRequest(BaseModel):
    songUid: str | None = None
    youtubeUrl: str = Field(..., min_length=1)
    model: str = Field(default="default", min_length=1, max_length=120)


class AudioStem(BaseModel):
    kind: str
    path: str
    fileName: str
    sizeBytes: int
    downloadPath: str | None = None


class SeparateYouTubeResponse(BaseModel):
    songUid: str | None
    youtubeUrl: str
    model: str
    outputDir: str
    stems: list[AudioStem]


class ShazamLyricsRequest(BaseModel):
    shazamUrl: str = Field(..., min_length=1)


class ShazamLyricsResponse(BaseModel):
    shazamUrl: str
    title: str
    artist: str
    lines: list[LyricLine]


class ShazamSearchRequest(BaseModel):
    title: str = Field(..., min_length=1)
    artist: str = ""
    country: str = Field(default="tw", min_length=2, max_length=2)
    limit: int = Field(default=5, ge=1, le=10)


class ShazamSearchCandidate(BaseModel):
    shazamUrl: str
    title: str
    artist: str
    appleMusicId: str
    durationSeconds: float | None = None
    hasLyrics: bool = False
    hasTimeSyncedLyrics: bool = False
    score: float


class ShazamSearchResponse(BaseModel):
    query: str
    candidates: list[ShazamSearchCandidate]


class AlignedLyricLine(BaseModel):
    lyricId: int
    japanese: str
    start: float
    end: float
    source: str
    score: float
    whisperSegmentIndex: int | None = None
    whisperWordStartIndex: int | None = None
    whisperWordEndIndex: int | None = None


class TranscribeAlignShazamRequest(BaseModel):
    songUid: str | None = None
    youtubeUrl: str = Field(..., min_length=1)
    shazamUrl: str = Field(..., min_length=1)
    language: str = Field(default="ja", min_length=2, max_length=8)


class TranscribeAlignShazamResponse(BaseModel):
    songUid: str | None
    youtubeUrl: str
    shazamUrl: str
    title: str
    artist: str
    language: str
    model: str
    durationSeconds: float | None
    whisperSegments: list[WhisperSegment]
    alignedLines: list[AlignedLyricLine]


class HighAccuracyLyricLine(BaseModel):
    lyricId: int = Field(..., ge=1)
    japanese: str = Field(..., min_length=1)
    currentStart: float | None = None


class HighAccuracyAlignRequest(BaseModel):
    songUid: str | None = None
    youtubeUrl: str = Field(..., min_length=1)
    language: str = Field(default="ja", min_length=2, max_length=8)
    lyrics: list[HighAccuracyLyricLine] = Field(..., min_length=1, max_length=200)


class HighAccuracyAlignResponse(BaseModel):
    songUid: str | None
    youtubeUrl: str
    language: str
    model: str
    durationSeconds: float | None
    alignedLines: list[AlignedLyricLine]
    matchedCount: int
    totalCount: int


class Settings(BaseModel):
    api_token: str
    codex_bin: str = "codex"
    codex_model: str | None = None
    codex_timeout_seconds: float = 180.0
    codex_workdir: str = "/tmp/learnmore-api-codex"
    whisper_model: str = "medium"
    whisper_device: str = "auto"
    whisper_compute_type: str = "auto"
    whisper_download_dir: str = "/tmp/learnmore-api-whisper"
    whisper_timeout_seconds: float = 1800.0
    ytdlp_bin: str = "yt-dlp"
    shazam_timeout_seconds: float = 30.0
    uvr_command_template: str = ""
    uvr_output_dir: str = "/tmp/learnmore-api-uvr"
    uvr_timeout_seconds: float = 1800.0
    high_accuracy_use_vocals_stem: bool = True
    whisperx_align_enabled: bool = True


def load_settings() -> Settings:
    api_token = os.getenv("LEARNMORE_API_TOKEN", "").strip()
    codex_bin = os.getenv("CODEX_BIN", "codex").strip() or "codex"
    codex_model = os.getenv("CODEX_MODEL", "").strip() or None
    codex_workdir = os.getenv("CODEX_WORKDIR", "/tmp/learnmore-api-codex").strip()
    whisper_model = os.getenv("WHISPER_MODEL", "medium").strip() or "medium"
    whisper_device = os.getenv("WHISPER_DEVICE", "auto").strip() or "auto"
    whisper_compute_type = os.getenv("WHISPER_COMPUTE_TYPE", "auto").strip() or "auto"
    whisper_download_dir = os.getenv("WHISPER_DOWNLOAD_DIR", "/tmp/learnmore-api-whisper").strip()
    ytdlp_bin = os.getenv("YTDLP_BIN", "yt-dlp").strip() or "yt-dlp"
    shazam_timeout_raw = os.getenv("SHAZAM_TIMEOUT_SECONDS", "30").strip()
    uvr_command_template = os.getenv("UVR_COMMAND_TEMPLATE", "").strip()
    uvr_output_dir = os.getenv("UVR_OUTPUT_DIR", "/tmp/learnmore-api-uvr").strip()
    high_accuracy_use_vocals_stem = parse_bool_env("HIGH_ACCURACY_USE_VOCALS_STEM", True)
    whisperx_align_enabled = parse_bool_env("WHISPERX_ALIGN_ENABLED", True)

    timeout_raw = os.getenv("CODEX_TIMEOUT_SECONDS", "180").strip()
    try:
        timeout = float(timeout_raw)
    except ValueError:
        timeout = 180.0
    whisper_timeout_raw = os.getenv("WHISPER_TIMEOUT_SECONDS", "1800").strip()
    try:
        whisper_timeout = float(whisper_timeout_raw)
    except ValueError:
        whisper_timeout = 1800.0
    try:
        shazam_timeout = float(shazam_timeout_raw)
    except ValueError:
        shazam_timeout = 30.0
    uvr_timeout_raw = os.getenv("UVR_TIMEOUT_SECONDS", "1800").strip()
    try:
        uvr_timeout = float(uvr_timeout_raw)
    except ValueError:
        uvr_timeout = 1800.0

    return Settings(
        api_token=api_token,
        codex_bin=codex_bin,
        codex_model=codex_model,
        codex_timeout_seconds=timeout,
        codex_workdir=codex_workdir or "/tmp/learnmore-api-codex",
        whisper_model=whisper_model,
        whisper_device=whisper_device,
        whisper_compute_type=whisper_compute_type,
        whisper_download_dir=whisper_download_dir or "/tmp/learnmore-api-whisper",
        whisper_timeout_seconds=whisper_timeout,
        ytdlp_bin=ytdlp_bin,
        shazam_timeout_seconds=shazam_timeout,
        uvr_command_template=uvr_command_template,
        uvr_output_dir=uvr_output_dir or "/tmp/learnmore-api-uvr",
        uvr_timeout_seconds=uvr_timeout,
        high_accuracy_use_vocals_stem=high_accuracy_use_vocals_stem,
        whisperx_align_enabled=whisperx_align_enabled,
    )


def parse_bool_env(name: str, default: bool) -> bool:
    value = os.getenv(name)
    if value is None or not value.strip():
        return default
    return value.strip().casefold() in {"1", "true", "yes", "y", "on"}


@asynccontextmanager
async def lifespan(app: FastAPI) -> AsyncIterator[None]:
    settings = load_settings()
    Path(settings.codex_workdir).mkdir(parents=True, exist_ok=True)
    Path(settings.whisper_download_dir).mkdir(parents=True, exist_ok=True)
    Path(settings.uvr_output_dir).mkdir(parents=True, exist_ok=True)
    app.state.settings = settings
    app.state.codex_lock = asyncio.Semaphore(1)
    app.state.whisper_lock = asyncio.Semaphore(1)
    app.state.uvr_lock = asyncio.Semaphore(1)
    app.state.whisper_model = None
    app.state.whisperx_align_models = {}
    yield


app = FastAPI(
    title="LearnMore API",
    version="1.2.0",
    root_path="/LearnMoreAPI",
    lifespan=lifespan,
)


CJK_RE = re.compile(r"[\u3400-\u9fff]")
ALLOWED_ALIAS_TYPES = {"chinese_title", "alternate_title"}


def collect_valid_annotations(codex_result: dict[str, Any]) -> dict[int, dict[str, str]]:
    annotations: dict[int, dict[str, str]] = {}
    for item in codex_result.get("annotations", []):
        if not isinstance(item, dict):
            continue
        if "lyricId" not in item or "japaneseRuby" not in item or "roman" not in item:
            continue
        try:
            lyric_id = int(item["lyricId"])
        except (TypeError, ValueError):
            continue
        japanese_ruby = str(item["japaneseRuby"]).strip()
        roman = str(item["roman"]).strip()
        if not japanese_ruby or not roman:
            continue
        annotations[lyric_id] = {
            "japaneseRuby": japanese_ruby,
            "roman": roman,
        }
    return annotations


def fallback_annotation(line: LyricLine) -> dict[str, str]:
    text = line.japanese.strip()
    return {
        "japaneseRuby": html.escape(text),
        "roman": japanese_romaji(text),
    }


def get_settings() -> Settings:
    return app.state.settings


def require_bearer_token(
    authorization: str | None = Header(default=None),
    settings: Settings = Depends(get_settings),
) -> None:
    if not settings.api_token:
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail="API token is not configured.",
        )

    expected = f"Bearer {settings.api_token}"
    if authorization != expected:
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail="Invalid API token.",
        )


@app.get("/health")
async def health(settings: Settings = Depends(get_settings)) -> dict[str, object]:
    return {
        "ok": True,
        "service": "LearnMoreAPI",
        "backend": "codex",
        "codexConfigured": bool(settings.codex_bin),
        "whisperConfigured": bool(settings.whisper_model),
        "whisperModel": settings.whisper_model,
        "whisperDevice": settings.whisper_device,
        "whisperComputeType": settings.whisper_compute_type,
        "highAccuracyAlignmentEngine": "whisperx",
    }


@app.post(
    "/v1/translate-lines",
    response_model=TranslateLinesResponse,
    dependencies=[Depends(require_bearer_token)],
)
async def translate_lines(
    request: TranslateLinesRequest,
    settings: Settings = Depends(get_settings),
) -> TranslateLinesResponse:
    lock: asyncio.Semaphore = app.state.codex_lock
    async with lock:
        codex_result = await translate_batch(request.lines, settings)

    translations_by_id = {
        int(item["lyricId"]): str(item["chinese"]).strip()
        for item in codex_result.get("translations", [])
        if isinstance(item, dict) and "lyricId" in item and "chinese" in item
    }

    if set(translations_by_id) != {line.lyricId for line in request.lines}:
        raise HTTPException(
            status_code=status.HTTP_502_BAD_GATEWAY,
            detail="Codex response did not include exactly the requested lyric IDs.",
        )

    translations = [
        TranslationLine(
            lyricId=line.lyricId,
            japanese=line.japanese,
            chinese=translations_by_id[line.lyricId],
        )
        for line in request.lines
    ]
    if any(not item.chinese or item.chinese == "翻譯中..." for item in translations):
        raise HTTPException(
            status_code=status.HTTP_502_BAD_GATEWAY,
            detail="Codex returned an empty or placeholder translation.",
        )

    return TranslateLinesResponse(songUid=request.songUid, translations=translations)


@app.post(
    "/v1/annotate-lines",
    response_model=AnnotateLinesResponse,
    dependencies=[Depends(require_bearer_token)],
)
async def annotate_lines(
    request: TranslateLinesRequest,
    settings: Settings = Depends(get_settings),
) -> AnnotateLinesResponse:
    lock: asyncio.Semaphore = app.state.codex_lock
    async with lock:
        codex_result = await annotate_batch(request.lines, settings)

    annotations_by_id = collect_valid_annotations(codex_result)

    missing_lines = [line for line in request.lines if line.lyricId not in annotations_by_id]
    if missing_lines:
        async with lock:
            retry_result = await annotate_batch(missing_lines, settings)
        annotations_by_id.update(collect_valid_annotations(retry_result))

    annotations: list[AnnotationLine] = []
    for line in request.lines:
        annotation = annotations_by_id.get(line.lyricId) or fallback_annotation(line)
        annotations.append(
            AnnotationLine(
                lyricId=line.lyricId,
                japanese=line.japanese,
                japaneseRuby=annotation["japaneseRuby"],
                roman=annotation["roman"],
            )
        )

    return AnnotateLinesResponse(songUid=request.songUid, annotations=annotations)


@app.post(
    "/v1/generate-song-aliases",
    response_model=GenerateSongAliasesResponse,
    dependencies=[Depends(require_bearer_token)],
)
async def generate_song_aliases(
    request: GenerateSongAliasesRequest,
    settings: Settings = Depends(get_settings),
) -> GenerateSongAliasesResponse:
    lock: asyncio.Semaphore = app.state.codex_lock
    async with lock:
        codex_result = await generate_alias_batch(request.songs, settings)

    requested_song_uids = {song.songUid for song in request.songs}
    items = normalize_generated_aliases(codex_result, requested_song_uids)
    if {item.songUid for item in items} != requested_song_uids:
        raise HTTPException(
            status_code=status.HTTP_502_BAD_GATEWAY,
            detail="Codex response did not include exactly the requested song UIDs.",
        )

    return GenerateSongAliasesResponse(songs=items)


@app.post(
    "/v1/transcribe-youtube",
    response_model=TranscribeYouTubeResponse,
    dependencies=[Depends(require_bearer_token)],
)
async def transcribe_youtube(
    request: TranscribeYouTubeRequest,
    settings: Settings = Depends(get_settings),
) -> TranscribeYouTubeResponse:
    lock: asyncio.Semaphore = app.state.whisper_lock
    async with lock:
        try:
            return await asyncio.wait_for(
                asyncio.to_thread(transcribe_youtube_sync, request, settings),
                timeout=settings.whisper_timeout_seconds,
            )
        except TimeoutError as exc:
            raise HTTPException(
                status_code=status.HTTP_504_GATEWAY_TIMEOUT,
                detail="Local Whisper transcription timed out.",
            ) from exc
        except RuntimeError as exc:
            raise HTTPException(
                status_code=status.HTTP_502_BAD_GATEWAY,
                detail=str(exc),
            ) from exc


@app.post(
    "/v1/separate-youtube",
    response_model=SeparateYouTubeResponse,
    dependencies=[Depends(require_bearer_token)],
)
async def separate_youtube(
    request: SeparateYouTubeRequest,
    settings: Settings = Depends(get_settings),
) -> SeparateYouTubeResponse:
    lock: asyncio.Semaphore = app.state.uvr_lock
    async with lock:
        try:
            return await asyncio.wait_for(
                asyncio.to_thread(separate_youtube_sync, request, settings),
                timeout=settings.uvr_timeout_seconds,
            )
        except TimeoutError as exc:
            raise HTTPException(
                status_code=status.HTTP_504_GATEWAY_TIMEOUT,
                detail="UVR separation timed out.",
            ) from exc
        except RuntimeError as exc:
            raise HTTPException(
                status_code=status.HTTP_502_BAD_GATEWAY,
                detail=str(exc),
            ) from exc


@app.get(
    "/v1/audio-stem-file",
    dependencies=[Depends(require_bearer_token)],
)
async def download_audio_stem_file(
    path: str = Query(..., min_length=1),
    settings: Settings = Depends(get_settings),
) -> FileResponse:
    stem_path = resolve_uvr_output_file(settings, path)
    return FileResponse(
        stem_path,
        media_type="application/octet-stream",
        filename=stem_path.name,
    )


@app.post(
    "/v1/shazam-search",
    response_model=ShazamSearchResponse,
    dependencies=[Depends(require_bearer_token)],
)
async def shazam_search(
    request: ShazamSearchRequest,
    settings: Settings = Depends(get_settings),
) -> ShazamSearchResponse:
    try:
        return await asyncio.to_thread(search_shazam_song, request, settings)
    except RuntimeError as exc:
        raise HTTPException(
            status_code=status.HTTP_502_BAD_GATEWAY,
            detail=str(exc),
        ) from exc


@app.post(
    "/v1/shazam-lyrics",
    response_model=ShazamLyricsResponse,
    dependencies=[Depends(require_bearer_token)],
)
async def shazam_lyrics(
    request: ShazamLyricsRequest,
    settings: Settings = Depends(get_settings),
) -> ShazamLyricsResponse:
    try:
        return await asyncio.to_thread(fetch_shazam_lyrics, request.shazamUrl, settings)
    except RuntimeError as exc:
        raise HTTPException(
            status_code=status.HTTP_502_BAD_GATEWAY,
            detail=str(exc),
        ) from exc


@app.post(
    "/v1/transcribe-align-shazam",
    response_model=TranscribeAlignShazamResponse,
    dependencies=[Depends(require_bearer_token)],
)
async def transcribe_align_shazam(
    request: TranscribeAlignShazamRequest,
    settings: Settings = Depends(get_settings),
) -> TranscribeAlignShazamResponse:
    lock: asyncio.Semaphore = app.state.whisper_lock
    async with lock:
        try:
            return await asyncio.wait_for(
                asyncio.to_thread(transcribe_align_shazam_sync, request, settings),
                timeout=settings.whisper_timeout_seconds + settings.shazam_timeout_seconds,
            )
        except TimeoutError as exc:
            raise HTTPException(
                status_code=status.HTTP_504_GATEWAY_TIMEOUT,
                detail="Local Whisper and Shazam alignment timed out.",
            ) from exc
        except RuntimeError as exc:
            raise HTTPException(
                status_code=status.HTTP_502_BAD_GATEWAY,
                detail=str(exc),
            ) from exc


@app.post(
    "/v1/high-accuracy-align",
    response_model=HighAccuracyAlignResponse,
    dependencies=[Depends(require_bearer_token)],
)
async def high_accuracy_align(
    request: HighAccuracyAlignRequest,
    settings: Settings = Depends(get_settings),
) -> HighAccuracyAlignResponse:
    lock: asyncio.Semaphore = app.state.whisper_lock
    async with lock:
        try:
            return await asyncio.wait_for(
                asyncio.to_thread(high_accuracy_align_sync, request, settings),
                timeout=settings.whisper_timeout_seconds,
            )
        except TimeoutError as exc:
            raise HTTPException(
                status_code=status.HTTP_504_GATEWAY_TIMEOUT,
                detail="High-accuracy lyric alignment timed out.",
            ) from exc
        except RuntimeError as exc:
            raise HTTPException(
                status_code=status.HTTP_502_BAD_GATEWAY,
                detail=str(exc),
            ) from exc


async def translate_batch(lines: list[LyricLine], settings: Settings) -> dict[str, Any]:
    prompt = build_codex_prompt(lines)
    return await run_codex(prompt, settings)


async def annotate_batch(lines: list[LyricLine], settings: Settings) -> dict[str, Any]:
    prompt = build_annotation_prompt(lines)
    return await run_codex(prompt, settings)


async def generate_alias_batch(songs: list[SongAliasCandidate], settings: Settings) -> dict[str, Any]:
    prompt = build_song_alias_prompt(songs)
    return await run_codex(prompt, settings)


def transcribe_youtube_sync(request: TranscribeYouTubeRequest, settings: Settings) -> TranscribeYouTubeResponse:
    audio_path = download_youtube_audio(request.youtubeUrl, settings)
    try:
        segments, duration = transcribe_audio_file(audio_path, request.language, settings)
        return TranscribeYouTubeResponse(
            songUid=request.songUid,
            youtubeUrl=request.youtubeUrl,
            language=request.language,
            model=settings.whisper_model,
            durationSeconds=duration,
            segments=segments,
        )
    finally:
        audio_path.unlink(missing_ok=True)


def transcribe_audio_file(audio_path: Path, language: str, settings: Settings) -> tuple[list[WhisperSegment], float | None]:
    model = get_whisper_model(settings)
    segments_iter, info = model.transcribe(
        str(audio_path),
        language=language,
        task="transcribe",
        beam_size=5,
        vad_filter=False,
        word_timestamps=True,
        condition_on_previous_text=False,
    )
    segments: list[WhisperSegment] = []
    word_index = 1
    for index, segment in enumerate(segments_iter, start=1):
        text = str(segment.text).strip()
        if not text:
            continue
        words: list[WhisperWord] = []
        for word in getattr(segment, "words", None) or []:
            word_text = str(getattr(word, "word", "")).strip()
            if not word_text:
                continue
            words.append(
                WhisperWord(
                    index=word_index,
                    start=round(float(getattr(word, "start", segment.start)), 3),
                    end=round(float(getattr(word, "end", segment.end)), 3),
                    text=word_text,
                )
            )
            word_index += 1
        segments.append(
            WhisperSegment(
                index=index,
                start=round(float(segment.start), 3),
                end=round(float(segment.end), 3),
                text=text,
                words=words,
            )
        )
    duration = round(float(info.duration), 3) if getattr(info, "duration", None) else None
    return segments, duration


def transcribe_audio_file_with_whisperx(audio_path: Path, language: str, settings: Settings) -> tuple[list[WhisperSegment], float | None]:
    segments, duration = transcribe_audio_file(audio_path, language, settings)
    if not segments:
        return segments, duration

    try:
        import whisperx
    except ImportError as exc:
        raise RuntimeError("WhisperX is not installed.") from exc

    device = resolve_whisperx_device(settings)
    align_model, align_metadata = get_whisperx_align_model(language, device, settings)
    transcript = [
        {
            "start": float(segment.start),
            "end": float(segment.end),
            "text": segment.text,
        }
        for segment in segments
        if segment.text.strip()
    ]
    if not transcript:
        return segments, duration

    try:
        aligned = whisperx.align(
            transcript,
            align_model,
            align_metadata,
            str(audio_path),
            device,
            return_char_alignments=False,
            print_progress=False,
        )
    except Exception as exc:
        raise RuntimeError(f"WhisperX alignment failed: {truncate(str(exc))}") from exc

    aligned_segments_raw = aligned.get("segments") if isinstance(aligned, dict) else getattr(aligned, "segments", None)
    if not isinstance(aligned_segments_raw, list) or not aligned_segments_raw:
        raise RuntimeError("WhisperX alignment returned no segments.")

    converted = convert_whisperx_segments(aligned_segments_raw)
    if not converted:
        raise RuntimeError("WhisperX alignment returned no usable segments.")
    return converted, duration


def convert_whisperx_segments(aligned_segments_raw: list[Any]) -> list[WhisperSegment]:
    converted: list[WhisperSegment] = []
    word_index = 1
    for index, raw_segment in enumerate(aligned_segments_raw, start=1):
        if not isinstance(raw_segment, dict):
            continue
        text = str(raw_segment.get("text") or "").strip()
        if not text:
            continue
        start = safe_float(raw_segment.get("start"), 0.0)
        end = safe_float(raw_segment.get("end"), start + 0.5)
        words: list[WhisperWord] = []
        for raw_word in raw_segment.get("words") or []:
            if not isinstance(raw_word, dict):
                continue
            word_text = str(raw_word.get("word") or raw_word.get("text") or "").strip()
            if not word_text or raw_word.get("start") is None or raw_word.get("end") is None:
                continue
            word_start = safe_float(raw_word.get("start"), start)
            word_end = safe_float(raw_word.get("end"), word_start + 0.05)
            if word_end <= word_start:
                continue
            words.append(
                WhisperWord(
                    index=word_index,
                    start=round(word_start, 3),
                    end=round(word_end, 3),
                    text=word_text,
                )
            )
            word_index += 1
        if words:
            start = words[0].start
            end = words[-1].end
        converted.append(
            WhisperSegment(
                index=index,
                start=round(start, 3),
                end=round(max(end, start + 0.25), 3),
                text=text,
                words=words,
            )
        )
    return converted


def align_lyric_lines_audio_file_with_whisperx(
    audio_path: Path,
    lyric_lines: list[HighAccuracyLyricLine],
    language: str,
    settings: Settings,
) -> tuple[list[WhisperSegment], float | None]:
    usable_lines = [line for line in lyric_lines if line.japanese.strip()]
    if not usable_lines:
        return [], None

    try:
        import whisperx
    except ImportError as exc:
        raise RuntimeError("WhisperX is not installed.") from exc

    device = resolve_whisperx_device(settings)
    align_model, align_metadata = get_whisperx_align_model(language, device, settings)
    asr_segments, asr_duration = transcribe_audio_file(audio_path, language, settings)
    asr_aligned = align_high_accuracy_lyrics_to_asr_sequence(usable_lines, asr_segments, asr_duration)

    audio = whisperx.load_audio(str(audio_path))
    audio_duration = round(float(len(audio)) / 16000.0, 3) if len(audio) else None
    duration = audio_duration or asr_duration
    transcript = build_whisperx_lyric_transcript(usable_lines, duration, asr_aligned)
    if not transcript:
        return [], duration

    try:
        aligned = whisperx.align(
            transcript,
            align_model,
            align_metadata,
            audio,
            device,
            return_char_alignments=False,
            print_progress=False,
        )
    except Exception as exc:
        raise RuntimeError(f"WhisperX lyric alignment failed: {truncate(str(exc))}") from exc

    aligned_segments_raw = aligned.get("segments") if isinstance(aligned, dict) else getattr(aligned, "segments", None)
    if not isinstance(aligned_segments_raw, list) or not aligned_segments_raw:
        raise RuntimeError("WhisperX lyric alignment returned no segments.")

    converted = convert_whisperx_segments(aligned_segments_raw)
    if not converted:
        raise RuntimeError("WhisperX lyric alignment returned no usable segments.")

    refined = refine_lyric_segments_with_asr_word_anchors(converted, usable_lines, asr_segments, asr_aligned)
    mark_internal_forced_lyric_alignment_segments(refined, usable_lines)
    return refined, duration


def mark_internal_forced_lyric_alignment_segments(
    lyric_segments: list[WhisperSegment],
    lyric_lines: list[HighAccuracyLyricLine],
) -> None:
    lyric_stream, _, _ = build_lyric_sequence_index(lyric_lines)
    segment_stream = "".join(normalize_for_alignment(segment.text) for segment in lyric_segments)
    stream_score = alignment_similarity(lyric_stream, segment_stream) if lyric_stream and segment_stream else 0.0
    for index, segment in enumerate(lyric_segments):
        if stream_score >= 0.90:
            segment._forced_lyric_alignment = True
            continue
        line = lyric_lines[index] if index < len(lyric_lines) else None
        if line is None:
            continue
        if not line.japanese.strip() or not segment.text.strip():
            continue
        score = alignment_similarity(
            normalize_for_alignment(line.japanese),
            normalize_for_alignment(segment.text),
        )
        if score >= 0.90:
            segment._forced_lyric_alignment = True


def refine_lyric_segments_with_asr_word_anchors(
    lyric_segments: list[WhisperSegment],
    lyric_lines: list[HighAccuracyLyricLine],
    asr_segments: list[WhisperSegment],
    asr_aligned_lines: list[AlignedLyricLine] | None = None,
) -> list[WhisperSegment]:
    if not lyric_segments or not lyric_lines or not asr_segments:
        return lyric_segments

    word_alignment = build_word_alignment_index(asr_segments)
    asr_aligned_by_id = {
        line.lyricId: line
        for line in (asr_aligned_lines or [])
        if is_reliable_audio_alignment(line)
    }
    refined: list[WhisperSegment] = []
    previous_start = 0.0
    for index, segment in enumerate(lyric_segments):
        line = lyric_lines[index] if index < len(lyric_lines) else None
        next_segment_start = (
            lyric_segments[index + 1].start
            if index + 1 < len(lyric_segments)
            else None
        )
        next_current_start = next(
            (
                candidate.currentStart
                for candidate in lyric_lines[index + 1 :]
                if candidate.currentStart is not None
            ),
            None,
        )
        match = (
            find_asr_word_anchor_for_lyric_line(
                line.japanese,
                word_alignment,
                segment.start,
                previous_start,
                next_segment_start,
                line.currentStart,
                next_current_start,
            )
            if line is not None
            else None
        )
        if match is None:
            asr_aligned = asr_aligned_by_id.get(line.lyricId) if line is not None else None
            if asr_aligned is not None:
                refined_segment = segment.model_copy(
                    update={
                        "start": round(float(asr_aligned.start), 3),
                        "end": round(max(float(asr_aligned.end), float(asr_aligned.start) + 0.25), 3),
                    }
                )
                refined_segment._asr_anchor_score = asr_aligned.score
                refined_segment._asr_anchor_source = asr_aligned.source
                refined.append(refined_segment)
                previous_start = refined_segment.start
                continue
            refined.append(segment)
            previous_start = segment.start
            continue

        start, end, first_word, last_word, score = match
        refined_words = [
            word
            for word in segment.words
            if word.index != first_word.index and word.index != last_word.index
        ]
        refined_words.insert(0, first_word)
        if last_word.index != first_word.index:
            refined_words.append(last_word)
        refined_words.sort(key=lambda word: (word.start, word.end, word.index))
        refined_segment = segment.model_copy(
            update={
                "start": round(start, 3),
                "end": round(max(end, start + 0.25), 3),
                "words": refined_words,
            }
        )
        refined_segment._asr_anchor_score = score
        refined_segment._asr_anchor_source = "whisper_local_asr_anchor_match"
        refined.append(refined_segment)
        previous_start = refined_segment.start
    return refined


def find_asr_word_anchor_for_lyric_line(
    lyric_text: str,
    word_alignment: dict[str, Any],
    approximate_start: float,
    previous_start: float,
    next_start: float | None,
    current_start: float | None = None,
    next_current_start: float | None = None,
) -> tuple[float, float, WhisperWord, WhisperWord, float] | None:
    normalized = normalize_for_alignment(lyric_text)
    latin_target = build_latin_alignment_target_for_lyric(lyric_text)
    if len(normalized) < 5 and len(latin_target) < 4:
        return None

    stream = str(word_alignment.get("stream") or "")
    char_to_word: list[WhisperWord] = word_alignment.get("charToWord") or []
    if not stream or not char_to_word:
        return None

    if is_latin_dominant_lyric(lyric_text) or latin_target:
        latin_match = find_latin_fuzzy_asr_word_anchor_for_lyric_line(
            lyric_text,
            word_alignment,
            approximate_start,
            previous_start,
            current_start,
            next_current_start,
        )
        if latin_match is not None:
            return latin_match

    candidates = find_asr_anchor_candidates(normalized, stream, char_to_word)
    if not candidates:
        return None

    hint_start = float(current_start if current_start is not None else approximate_start)
    min_start = max(0.0, hint_start - 2.0)
    if previous_start <= hint_start + 1.0:
        min_start = max(min_start, previous_start - 0.1)
    max_start = hint_start + 2.0
    if next_current_start is not None and next_current_start > hint_start + 0.25:
        max_start = min(max_start, next_current_start + 0.25)
    elif next_start is not None and next_start > hint_start + 0.25:
        max_start = min(max_start, next_start - 0.1)

    best: tuple[float, float, WhisperWord, WhisperWord, float] | None = None
    for score, first_word, last_word in candidates:
        if first_word.start < min_start or first_word.start > max_start:
            continue
        distance = abs(first_word.start - hint_start)
        rank = (score, -distance, -first_word.index)
        if best is None or rank > (best[0], best[4], -best[2].index):
            best = (score, last_word.end, first_word, last_word, -distance)

    if best is None:
        return None

    score, end, first_word, last_word, _ = best
    if score < 0.70:
        return None
    start = first_word.start
    return start, end, first_word, last_word, score


def find_latin_fuzzy_asr_word_anchor_for_lyric_line(
    lyric_text: str,
    word_alignment: dict[str, Any],
    approximate_start: float,
    previous_start: float,
    current_start: float | None,
    next_current_start: float | None,
) -> tuple[float, float, WhisperWord, WhisperWord, float] | None:
    lyric_latin = build_latin_alignment_target_for_lyric(lyric_text)
    if len(lyric_latin) < 4:
        return None

    words: list[WhisperWord] = word_alignment.get("words") or []
    if not words:
        return None

    hint_start = float(current_start if current_start is not None else approximate_start)
    min_start = max(0.0, hint_start - 3.0)
    if previous_start <= hint_start + 1.0:
        min_start = max(min_start, previous_start - 0.1)
    max_start = hint_start + 8.0
    if next_current_start is not None and next_current_start > hint_start + 0.25:
        max_start = min(max_start, next_current_start + 1.5)

    target_len = len(lyric_latin)
    best: tuple[float, float, WhisperWord, WhisperWord, float] | None = None
    for start_index, first_word in enumerate(words):
        if first_word.start < min_start or first_word.start > max_start:
            continue

        window_text = ""
        first_latin_word: WhisperWord | None = None
        for end_index in range(start_index, min(len(words), start_index + 16)):
            last_word = words[end_index]
            if last_word.end < first_word.start:
                continue
            if last_word.start > max_start + 2.0:
                break
            piece = normalize_latin_for_alignment(last_word.text)
            if not piece:
                if not window_text:
                    continue
                continue
            if first_latin_word is None:
                first_latin_word = last_word
            window_text += piece
            if len(window_text) < max(3, int(target_len * 0.45)):
                continue
            if len(window_text) > target_len * 1.8 + 6:
                break

            score = difflib.SequenceMatcher(None, lyric_latin, window_text).ratio()
            if lyric_latin in window_text or window_text in lyric_latin:
                score = max(score, min(len(lyric_latin), len(window_text)) / max(len(lyric_latin), len(window_text)))
            if score < 0.58:
                continue

            anchor_word = first_latin_word or first_word
            distance = abs(anchor_word.start - hint_start)
            rank = (score, -distance, -(last_word.end - anchor_word.start), -anchor_word.index)
            if best is None or rank > (best[0], best[4], -(best[1] - best[2].start), -best[2].index):
                best = (score, last_word.end, anchor_word, last_word, -distance)

    if best is None:
        return None

    score, end, first_word, last_word, _ = best
    start = first_word.start
    return start, max(end, start + 0.25), first_word, last_word, min(score, 0.99)


def normalize_latin_for_alignment(text: str) -> str:
    latin_parts = re.findall(r"[A-Za-z]+(?:'[A-Za-z]+)?|'[A-Za-z]+", text)
    normalized = "".join(part.lower().replace("'", "") for part in latin_parts)
    replacements = {
        "gotta": "gotto",
        "wanna": "wantto",
        "alright": "allright",
    }
    for source, target in replacements.items():
        normalized = normalized.replace(source, target)
    return normalized


def build_latin_alignment_target_for_lyric(text: str) -> str:
    latin = normalize_latin_for_alignment(text)
    japanese_count = len(re.findall(r"[\u3040-\u30ff\u3400-\u9fff]", text))
    katakana_latin = normalize_katakana_english_for_alignment(text)
    if not latin:
        return katakana_latin
    if japanese_count > 0 and len(latin) < 10:
        return katakana_latin
    return latin


def normalize_katakana_english_for_alignment(text: str) -> str:
    normalized = re.sub(r"[\s!！?？,，.。()（）「」『』\"'’]+", "", text)
    replacements = {
        "オーライ": "allright",
        "オールライト": "allright",
    }
    parts = [target for source, target in replacements.items() if source in normalized]
    return "".join(parts)


def find_asr_anchor_candidates(
    normalized_lyric: str,
    stream: str,
    char_to_word: list[WhisperWord],
) -> list[tuple[float, WhisperWord, WhisperWord]]:
    candidates: list[tuple[float, WhisperWord, WhisperWord]] = []
    for needle, score in build_asr_anchor_needles(normalized_lyric):
        start = 0
        while True:
            pos = stream.find(needle, start)
            if pos < 0:
                break
            end_pos = min(len(char_to_word) - 1, pos + len(needle) - 1)
            candidates.append((score, char_to_word[pos], char_to_word[end_pos]))
            start = pos + 1
    return candidates


def build_asr_anchor_needles(normalized_lyric: str) -> list[tuple[str, float]]:
    needles: list[tuple[str, float]] = [(normalized_lyric, 1.0)]
    if len(normalized_lyric) >= 10:
        prefix_len = min(14, max(6, len(normalized_lyric) // 2))
        prefix = normalized_lyric[:prefix_len]
        if prefix != normalized_lyric:
            needles.append((prefix, prefix_len / max(len(normalized_lyric), 1)))
    return needles


def build_whisperx_lyric_transcript(
    lyric_lines: list[HighAccuracyLyricLine],
    duration_seconds: float | None,
    anchor_lines: list[AlignedLyricLine] | None = None,
) -> list[dict[str, Any]]:
    duration = duration_seconds or infer_duration_from_current_starts(lyric_lines)
    count = len(lyric_lines)
    starts = infer_lyric_line_starts(lyric_lines, count, duration, anchor_lines)
    transcript: list[dict[str, Any]] = []
    for index, line in enumerate(lyric_lines):
        next_start = next((start for start in starts[index + 1 :] if start > starts[index]), None)
        fallback_next = duration * (index + 1) / max(count, 1)
        raw_end = next_start if next_start is not None else fallback_next
        start = max(0.0, starts[index] - 1.5)
        end = min(duration, max(starts[index] + 0.75, raw_end + 1.5))
        if end <= start:
            end = min(duration, start + 4.0)
        transcript.append(
            {
                "start": round(start, 3),
                "end": round(max(end, start + 0.25), 3),
                "text": line.japanese.strip(),
            }
        )
    return transcript


def infer_lyric_line_starts(
    lyric_lines: list[HighAccuracyLyricLine],
    count: int,
    duration: float,
    anchor_lines: list[AlignedLyricLine] | None,
) -> list[float]:
    anchor_by_id = {
        line.lyricId: line
        for line in anchor_lines or []
        if is_usable_whisperx_transcript_anchor(line)
    }
    anchors = [
        (index, max(0.0, min(float(anchor_by_id[line.lyricId].start), duration)))
        for index, line in enumerate(lyric_lines)
        if line.lyricId in anchor_by_id
    ]
    if not anchors:
        return [infer_lyric_line_start(line, index, count, duration) for index, line in enumerate(lyric_lines)]
    if should_use_current_start_hints(lyric_lines, count, duration, anchors):
        return [infer_lyric_line_start(line, index, count, duration) for index, line in enumerate(lyric_lines)]

    starts: list[float | None] = [None] * count
    for index, start in anchors:
        starts[index] = start

    first_index, first_start = anchors[0]
    if first_index > 0:
        for index in range(first_index):
            starts[index] = first_start * index / first_index

    for (left_index, left_start), (right_index, right_start) in zip(anchors, anchors[1:], strict=False):
        span = max(right_start - left_start, 0.25)
        steps = max(right_index - left_index, 1)
        for index in range(left_index + 1, right_index):
            starts[index] = left_start + span * (index - left_index) / steps

    last_index, last_start = anchors[-1]
    if last_index < count - 1:
        steps = count - last_index
        span = max(duration - last_start, 0.25)
        for index in range(last_index + 1, count):
            starts[index] = last_start + span * (index - last_index) / steps

    completed: list[float] = []
    previous = 0.0
    for index, start in enumerate(starts):
        fallback = infer_lyric_line_start(lyric_lines[index], index, count, duration)
        value = float(start if start is not None else fallback)
        value = max(previous, min(value, duration))
        completed.append(value)
        previous = value
    return completed


def should_use_current_start_hints(
    lyric_lines: list[HighAccuracyLyricLine],
    count: int,
    duration: float,
    anchors: list[tuple[int, float]],
) -> bool:
    anchor_ratio = len(anchors) / max(count, 1)
    if anchor_ratio < 0.35 and len(anchors) < 20:
        return False

    current_starts = [infer_lyric_line_start(line, index, count, duration) for index, line in enumerate(lyric_lines)]
    deltas = [
        abs(current_starts[index] - anchor_start)
        for index, anchor_start in anchors
        if lyric_lines[index].currentStart is not None
    ]
    required = min(len(anchors), max(2, int(len(anchors) * 0.5)))
    if len(deltas) < required:
        return False

    explicit_starts = [
        float(line.currentStart)
        for line in lyric_lines
        if line.currentStart is not None
    ]
    explicit_gaps = [
        later - earlier
        for earlier, later in zip(explicit_starts, explicit_starts[1:], strict=False)
        if later >= earlier
    ]
    if explicit_gaps:
        tiny_gap_ratio = sum(1 for gap in explicit_gaps if gap < 0.35) / len(explicit_gaps)
        if tiny_gap_ratio > 0.20:
            return False

    sorted_deltas = sorted(deltas)
    p90 = sorted_deltas[int((len(sorted_deltas) - 1) * 0.9)]
    return statistics.median(sorted_deltas) <= 4.0 and p90 <= 8.0


def is_usable_whisperx_transcript_anchor(line: AlignedLyricLine) -> bool:
    if line.source in {"current_timestamp_context", "proportional_fallback"}:
        return False
    return is_reliable_audio_alignment(line)


def infer_duration_from_current_starts(lyric_lines: list[HighAccuracyLyricLine]) -> float:
    starts = [float(line.currentStart) for line in lyric_lines if line.currentStart is not None]
    if starts:
        return max(starts) + 8.0
    return float(max(len(lyric_lines), 1) * 4)


def infer_lyric_line_start(
    line: HighAccuracyLyricLine,
    index: int,
    count: int,
    duration: float,
) -> float:
    if line.currentStart is not None:
        return max(0.0, min(float(line.currentStart), duration))
    return duration * index / max(count, 1)


def safe_float(value: Any, fallback: float) -> float:
    try:
        return float(value)
    except (TypeError, ValueError):
        return float(fallback)


def resolve_whisperx_device(settings: Settings) -> str:
    requested = settings.whisper_device.strip().lower()
    if requested in {"cpu", "cuda"}:
        return requested
    try:
        import torch
    except ImportError:
        return "cpu"
    return "cuda" if torch.cuda.is_available() else "cpu"


def get_whisperx_align_model(language: str, device: str, settings: Settings) -> tuple[Any, dict[str, Any]]:
    if not settings.whisperx_align_enabled:
        raise RuntimeError("WhisperX align model is disabled.")

    cache: dict[tuple[str, str], tuple[Any, dict[str, Any]]] = app.state.whisperx_align_models
    language_code = normalize_whisperx_language(language)
    key = (language_code, device)
    cached = cache.get(key)
    if cached is not None:
        return cached
    try:
        import whisperx
    except ImportError as exc:
        raise RuntimeError("WhisperX is not installed.") from exc

    try:
        align_model, align_metadata = whisperx.load_align_model(
            language_code=language_code,
            device=device,
            model_dir=settings.whisper_download_dir,
        )
    except Exception as exc:
        raise RuntimeError(f"WhisperX align model could not be loaded: {truncate(str(exc))}") from exc

    cache[key] = (align_model, align_metadata)
    return cache[key]


def normalize_whisperx_language(language: str) -> str:
    normalized = (language or "ja").strip().lower()
    if normalized.startswith("ja"):
        return "ja"
    return normalized.split("-", 1)[0] or "ja"


def separate_youtube_sync(request: SeparateYouTubeRequest, settings: Settings) -> SeparateYouTubeResponse:
    if not settings.uvr_command_template:
        raise RuntimeError("UVR_COMMAND_TEMPLATE is not configured.")

    audio_path = download_youtube_audio(request.youtubeUrl, settings)
    output_dir = build_uvr_output_dir(settings, request)
    if output_dir.exists():
        shutil.rmtree(output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)
    try:
        args = build_uvr_command_args(settings.uvr_command_template, audio_path, output_dir, request)
        try:
            result = subprocess.run(
                args,
                text=True,
                capture_output=True,
                timeout=settings.uvr_timeout_seconds,
            )
        except subprocess.TimeoutExpired as exc:
            raise RuntimeError("UVR command timed out.") from exc
        except OSError as exc:
            raise RuntimeError("UVR command could not be started.") from exc

        if result.returncode != 0:
            raise RuntimeError(f"UVR command failed: {truncate(result.stderr or result.stdout)}")

        stems = discover_audio_stems(output_dir)
        if not stems:
            raise RuntimeError("UVR command finished but produced no audio stems.")

        return SeparateYouTubeResponse(
            songUid=request.songUid,
            youtubeUrl=request.youtubeUrl,
            model=request.model,
            outputDir=str(output_dir),
            stems=stems,
        )
    finally:
        audio_path.unlink(missing_ok=True)


def build_uvr_output_dir(settings: Settings, request: SeparateYouTubeRequest) -> Path:
    safe_id = sanitize_path_segment(request.songUid or extract_youtube_id(request.youtubeUrl) or "song")
    return Path(settings.uvr_output_dir) / safe_id


def build_uvr_command_args(
    command_template: str,
    input_path: Path,
    output_dir: Path,
    request: SeparateYouTubeRequest,
) -> list[str]:
    replacements = {
        "input": str(input_path),
        "output_dir": str(output_dir),
        "song_uid": request.songUid or "",
        "model": request.model,
    }
    try:
        command = command_template.format(**replacements)
    except KeyError as exc:
        raise RuntimeError(f"UVR_COMMAND_TEMPLATE contains an unknown placeholder: {exc}") from exc
    args = shlex.split(command)
    if not args:
        raise RuntimeError("UVR_COMMAND_TEMPLATE produced an empty command.")
    return args


def discover_audio_stems(output_dir: Path) -> list[AudioStem]:
    audio_suffixes = {".wav", ".flac", ".mp3", ".m4a", ".ogg"}
    stems: list[AudioStem] = []
    for path in sorted(output_dir.rglob("*")):
        if not path.is_file() or path.suffix.lower() not in audio_suffixes:
            continue
        stems.append(
            AudioStem(
                kind=classify_stem_kind(path),
                path=str(path),
                fileName=path.name,
                sizeBytes=path.stat().st_size,
                downloadPath="v1/audio-stem-file?" + urllib.parse.urlencode({"path": str(path)}),
            )
        )
    return stems


def resolve_uvr_output_file(settings: Settings, requested_path: str) -> Path:
    root = Path(settings.uvr_output_dir).resolve()
    candidate = Path(requested_path)
    if not candidate.is_absolute():
        candidate = root / candidate
    try:
        resolved = candidate.resolve()
        resolved.relative_to(root)
    except (OSError, ValueError) as exc:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail="Audio stem file was not found.",
        ) from exc
    if not resolved.is_file():
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND,
            detail="Audio stem file was not found.",
        )
    return resolved


def classify_stem_kind(path: Path) -> str:
    name = path.stem.casefold()
    if any(token in name for token in ("instrumental", "no_vocals", "no-vocals", "karaoke", "inst")):
        return "instrumental"
    if any(token in name for token in ("vocals", "vocal", "voice", "voix")):
        return "vocals"
    return "other"


def sanitize_path_segment(value: str) -> str:
    sanitized = re.sub(r"[^A-Za-z0-9_.-]+", "-", value.strip())
    sanitized = sanitized.strip(".-")
    return sanitized[:120] or "song"


def extract_youtube_id(youtube_url: str) -> str | None:
    try:
        parsed = urllib.parse.urlparse(youtube_url)
    except ValueError:
        return None
    if "youtu.be" in parsed.netloc:
        value = parsed.path.strip("/")
        return value[:11] if value else None
    if "youtube.com" in parsed.netloc:
        value = urllib.parse.parse_qs(parsed.query).get("v", [""])[0]
        return value[:11] if value else None
    return None


def transcribe_align_shazam_sync(request: TranscribeAlignShazamRequest, settings: Settings) -> TranscribeAlignShazamResponse:
    shazam = fetch_shazam_lyrics(request.shazamUrl, settings)
    timed_lines = fetch_shazam_timed_lyrics(request.shazamUrl, settings)
    if timed_lines:
        transcript = transcribe_youtube_sync(
            TranscribeYouTubeRequest(
                songUid=request.songUid,
                youtubeUrl=request.youtubeUrl,
                language=request.language,
            ),
            settings,
        )
        verified_lines = verify_timed_lyrics_against_whisper(timed_lines, transcript.segments)
        verified_lines = correct_timed_lyrics_offset_from_whisper(
            verified_lines,
            transcript.segments,
            transcript.durationSeconds,
        )
        if should_use_shazam_timed_lyrics(verified_lines):
            reject_duration_mismatched_timed_lyrics(verified_lines, transcript.durationSeconds)
            return TranscribeAlignShazamResponse(
                songUid=request.songUid,
                youtubeUrl=request.youtubeUrl,
                shazamUrl=request.shazamUrl,
                title=shazam.title,
                artist=shazam.artist,
                language=request.language,
                model=f"{transcript.model}+shazam-timed-lyrics",
                durationSeconds=transcript.durationSeconds,
                whisperSegments=transcript.segments,
                alignedLines=verified_lines,
            )

        aligned = align_lyrics_to_whisper(shazam.lines, transcript.segments, transcript.durationSeconds)
        return TranscribeAlignShazamResponse(
            songUid=request.songUid,
            youtubeUrl=request.youtubeUrl,
            shazamUrl=request.shazamUrl,
            title=shazam.title,
            artist=shazam.artist,
            language=request.language,
            model=f"{transcript.model}+shazam-text-fallback",
            durationSeconds=transcript.durationSeconds,
            whisperSegments=transcript.segments,
            alignedLines=aligned,
        )
    transcript = transcribe_youtube_sync(
        TranscribeYouTubeRequest(
            songUid=request.songUid,
            youtubeUrl=request.youtubeUrl,
            language=request.language,
        ),
        settings,
    )
    aligned = align_lyrics_to_whisper(shazam.lines, transcript.segments, transcript.durationSeconds)
    return TranscribeAlignShazamResponse(
        songUid=request.songUid,
        youtubeUrl=request.youtubeUrl,
        shazamUrl=request.shazamUrl,
        title=shazam.title,
        artist=shazam.artist,
        language=request.language,
        model=transcript.model,
        durationSeconds=transcript.durationSeconds,
        whisperSegments=transcript.segments,
        alignedLines=aligned,
    )


def high_accuracy_align_sync(request: HighAccuracyAlignRequest, settings: Settings) -> HighAccuracyAlignResponse:
    lyric_lines = [line for line in request.lyrics if line.japanese.strip()]
    if not lyric_lines:
        raise RuntimeError("High-accuracy alignment request did not contain usable lyrics.")

    transcript = transcribe_youtube_for_high_accuracy_sync(request, settings, lyric_lines)
    if "+whisperx-lyrics" in transcript.model:
        aligned = align_high_accuracy_lyrics_to_whisperx_lyrics(
            lyric_lines,
            transcript.segments,
            transcript.durationSeconds,
        )
    else:
        aligned = align_high_accuracy_lyrics_to_whisper(lyric_lines, transcript.segments, transcript.durationSeconds)
    aligned = stabilize_unreliable_lines_with_current_start_context(
        lyric_lines,
        aligned,
        transcript.durationSeconds,
    )
    if "+whisperx" in transcript.model:
        aligned = label_whisperx_alignment_sources(aligned)
    aligned = label_low_confidence_latin_alignments(aligned)
    matched_count = sum(1 for line in aligned if is_reliable_audio_alignment(line))
    return HighAccuracyAlignResponse(
        songUid=request.songUid,
        youtubeUrl=request.youtubeUrl,
        language=request.language,
        model=f"{transcript.model}+high-accuracy-align",
        durationSeconds=transcript.durationSeconds,
        alignedLines=aligned,
        matchedCount=matched_count,
        totalCount=len(aligned),
    )


def transcribe_youtube_for_high_accuracy_sync(
    request: HighAccuracyAlignRequest,
    settings: Settings,
    lyric_lines: list[HighAccuracyLyricLine] | None = None,
) -> TranscribeYouTubeResponse:
    if not settings.high_accuracy_use_vocals_stem or not settings.uvr_command_template:
        raise RuntimeError("High-accuracy alignment requires vocals-stem transcription.")

    return transcribe_youtube_vocals_stem_sync(
        song_uid=request.songUid,
        youtube_url=request.youtubeUrl,
        language=request.language,
        settings=settings,
        lyric_lines=lyric_lines,
    )


def transcribe_youtube_vocals_stem_sync(
    song_uid: str | None,
    youtube_url: str,
    language: str,
    settings: Settings,
    lyric_lines: list[HighAccuracyLyricLine] | None = None,
) -> TranscribeYouTubeResponse:
    separated = separate_youtube_sync(
        SeparateYouTubeRequest(
            songUid=song_uid,
            youtubeUrl=youtube_url,
        ),
        settings,
    )
    vocals = select_vocals_stem(separated.stems)
    if vocals is None:
        raise RuntimeError("UVR separation did not produce a vocals stem.")

    vocals_path = Path(vocals.path)
    if lyric_lines:
        try:
            segments, duration = align_lyric_lines_audio_file_with_whisperx(vocals_path, lyric_lines, language, settings)
            model = f"{settings.whisper_model}+vocals-stem+whisperx-lyrics+asr-refined"
        except RuntimeError:
            segments, duration = transcribe_audio_file(vocals_path, language, settings)
            model = f"{settings.whisper_model}+vocals-stem+faster-whisper-words+whisperx-fallback"
    else:
        segments, duration = transcribe_audio_file_with_whisperx(vocals_path, language, settings)
        model = f"{settings.whisper_model}+vocals-stem+whisperx"
    return TranscribeYouTubeResponse(
        songUid=song_uid,
        youtubeUrl=youtube_url,
        language=language,
        model=model,
        durationSeconds=duration,
        segments=segments,
    )


def select_vocals_stem(stems: list[AudioStem]) -> AudioStem | None:
    for stem in stems:
        if stem.kind == "vocals":
            return stem
    return None


def should_use_shazam_timed_lyrics(timed_lines: list[AlignedLyricLine]) -> bool:
    if not timed_lines:
        return False
    verified_count = sum(
        1
        for line in timed_lines
        if line.source.startswith("shazam_timed_lyrics_verified")
    )
    return verified_count / len(timed_lines) >= 0.45


def search_shazam_song(request: ShazamSearchRequest, settings: Settings) -> ShazamSearchResponse:
    title = request.title.strip()
    artist = request.artist.strip()
    query = " ".join(part for part in [artist, title] if part)
    if not query:
        raise RuntimeError("Shazam search query is empty.")
    country = request.country.lower()
    url = (
        f"https://www.shazam.com/services/amapi/v1/catalog/{urllib.parse.quote(country)}/search?"
        + urllib.parse.urlencode({"types": "songs", "term": query, "limit": request.limit})
    )
    http_request = urllib.request.Request(
        url,
        headers={
            "User-Agent": "Mozilla/5.0 LearnMoreAPI/1.0",
            "Accept": "application/json",
        },
    )
    try:
        with urllib.request.urlopen(http_request, timeout=settings.shazam_timeout_seconds) as response:
            payload = json.loads(response.read().decode("utf-8", "replace"))
    except Exception as exc:
        raise RuntimeError("Shazam search could not be fetched.") from exc

    hits = (((payload.get("results") or {}).get("songs") or {}).get("data") or [])
    candidates: list[ShazamSearchCandidate] = []
    normalized_title = normalize_for_similarity(title)
    normalized_artist = normalize_for_similarity(artist)
    for hit in hits:
        if not isinstance(hit, dict):
            continue
        attributes = hit.get("attributes") if isinstance(hit.get("attributes"), dict) else {}
        song_id = str(hit.get("id") or "").strip()
        name = str(attributes.get("name") or "").strip()
        artist_name = str(attributes.get("artistName") or "").strip()
        if not song_id or not name:
            continue
        score = difflib.SequenceMatcher(None, normalized_title, normalize_for_similarity(name)).ratio()
        if normalized_artist:
            score = (score * 0.75) + (
                difflib.SequenceMatcher(None, normalized_artist, normalize_for_similarity(artist_name)).ratio() * 0.25
            )
        duration_ms = attributes.get("durationInMillis")
        duration_seconds = None
        if isinstance(duration_ms, (int, float)) and duration_ms > 0:
            duration_seconds = round(float(duration_ms) / 1000.0, 3)
        candidates.append(
            ShazamSearchCandidate(
                shazamUrl=f"https://www.shazam.com/zh-tw/song/{song_id}/{slugify_shazam_title(name)}",
                title=name,
                artist=artist_name,
                appleMusicId=song_id,
                durationSeconds=duration_seconds,
                hasLyrics=bool(attributes.get("hasLyrics")),
                hasTimeSyncedLyrics=bool(attributes.get("hasTimeSyncedLyrics")),
                score=round(score, 4),
            )
        )
    candidates.sort(key=lambda item: (item.hasLyrics, item.score), reverse=True)
    return ShazamSearchResponse(query=query, candidates=candidates)


def fetch_shazam_timed_lyrics(shazam_url: str, settings: Settings) -> list[AlignedLyricLine]:
    separator = "&" if "?" in shazam_url else "?"
    request = urllib.request.Request(
        f"{shazam_url}{separator}tab=lyrics",
        headers={
            "User-Agent": "Mozilla/5.0 LearnMoreAPI/1.0",
            "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
        },
    )
    try:
        with urllib.request.urlopen(request, timeout=settings.shazam_timeout_seconds) as response:
            page_html = response.read().decode("utf-8", "replace")
    except Exception:
        return []

    matches = re.findall(
        r'\{\\"content\\":\\"(.*?)\\",\\"startTimeInSeconds\\":\\"(.*?)\\",\\"endTimeInSeconds\\":\\"(.*?)\\"\}',
        page_html,
        flags=re.DOTALL,
    )
    lines: list[AlignedLyricLine] = []
    seen: set[tuple[str, float, float]] = set()
    for raw_content, raw_start, raw_end in matches:
        content = decode_nextjs_string(raw_content).strip()
        if not is_valid_shazam_timed_lyric_content(content):
            continue
        start = parse_shazam_timecode(decode_nextjs_string(raw_start))
        end = parse_shazam_timecode(decode_nextjs_string(raw_end))
        if start is None or end is None or end <= start:
            continue
        key = (content, start, end)
        if key in seen:
            continue
        seen.add(key)
        lines.append(
            AlignedLyricLine(
                lyricId=len(lines) + 1,
                japanese=content,
                start=round(start, 3),
                end=round(end, 3),
                source="shazam_timed_lyrics",
                score=1.0,
            )
        )
    return lines


def is_valid_shazam_timed_lyric_content(content: str) -> bool:
    if not content:
        return False
    lowered = content.lower()
    contamination_markers = (
        "<script",
        "</script",
        "__next_f",
        "self.__next",
        "starttimeinseconds",
        "endtimeinseconds",
        "lyriclines",
    )
    return not any(marker in lowered for marker in contamination_markers)


def decode_nextjs_string(value: str) -> str:
    try:
        return json.loads(f'"{value}"')
    except json.JSONDecodeError:
        return value.replace(r"\/", "/").replace(r"\"", '"').replace(r"\\", "\\")


def parse_shazam_timecode(value: str) -> float | None:
    text = value.strip()
    if not text:
        return None
    try:
        if ":" not in text:
            return float(text)
        parts = [float(part) for part in text.split(":")]
    except ValueError:
        return None
    if len(parts) == 2:
        return parts[0] * 60 + parts[1]
    if len(parts) == 3:
        return parts[0] * 3600 + parts[1] * 60 + parts[2]
    return None


def verify_timed_lyrics_against_whisper(
    timed_lines: list[AlignedLyricLine],
    whisper_segments: list[WhisperSegment],
) -> list[AlignedLyricLine]:
    verified: list[AlignedLyricLine] = []
    for line in timed_lines:
        score, segment_index = score_timed_line_against_whisper(line, whisper_segments)
        source = "shazam_timed_lyrics_verified" if score >= 0.52 else "shazam_timed_lyrics_unverified"
        verified.append(
            line.model_copy(
                update={
                    "source": source,
                    "score": round(score, 4),
                    "whisperSegmentIndex": segment_index,
                }
            )
        )
    return verified


def correct_timed_lyrics_offset_from_whisper(
    timed_lines: list[AlignedLyricLine],
    whisper_segments: list[WhisperSegment],
    duration_seconds: float | None,
) -> list[AlignedLyricLine]:
    segment_by_index = {segment.index: segment for segment in whisper_segments}
    lines_by_segment: dict[int, list[AlignedLyricLine]] = {}
    for line in timed_lines:
        if line.source != "shazam_timed_lyrics_verified" or line.whisperSegmentIndex is None:
            continue
        if line.whisperSegmentIndex not in segment_by_index:
            continue
        lines_by_segment.setdefault(line.whisperSegmentIndex, []).append(line)

    offsets: list[float] = []
    for segment_index, segment_lines in lines_by_segment.items():
        segment = segment_by_index.get(segment_index)
        if segment is None:
            continue
        line = min(segment_lines, key=lambda candidate: candidate.start)
        offset = segment.start - line.start
        if abs(offset) <= 12.0:
            offsets.append(offset)

    if len(offsets) < 3:
        return timed_lines

    offset = statistics.median(offsets)
    if abs(offset) < 1.25:
        return timed_lines

    max_duration = duration_seconds or (whisper_segments[-1].end if whisper_segments else None)
    corrected: list[AlignedLyricLine] = []
    for line in timed_lines:
        start = max(0.0, line.start + offset)
        end = max(start + 0.25, line.end + offset)
        if max_duration is not None:
            end = min(end, max_duration)
            start = min(start, max(0.0, end - 0.25))
        corrected.append(
            line.model_copy(
                update={
                    "start": round(start, 3),
                    "end": round(end, 3),
                    "source": f"{line.source}_offset_corrected",
                }
            )
        )
    return corrected


def reject_duration_mismatched_timed_lyrics(
    timed_lines: list[AlignedLyricLine],
    duration_seconds: float | None,
) -> None:
    if not duration_seconds or not timed_lines:
        return
    max_line_start = max(line.start for line in timed_lines)
    if max_line_start <= duration_seconds + 15.0:
        return

    verified_count = sum(
        1
        for line in timed_lines
        if line.source.startswith("shazam_timed_lyrics_verified")
    )
    min_verified = max(3, int(len(timed_lines) * 0.35))
    if verified_count >= min_verified:
        return

    raise RuntimeError(
        "Shazam timed lyrics are much longer than the YouTube audio and could not be verified against the same video."
    )


def score_timed_line_against_whisper(
    line: AlignedLyricLine,
    whisper_segments: list[WhisperSegment],
) -> tuple[float, int | None]:
    expected = normalize_for_alignment(line.japanese)
    if not expected:
        return 0.0, None
    window_start = max(0.0, line.start - 2.5)
    window_end = line.end + 2.5
    best_score = 0.0
    best_segment_index: int | None = None
    window_texts: list[str] = []
    first_window_segment_index: int | None = None
    for segment in whisper_segments:
        if segment.end < window_start or segment.start > window_end:
            continue
        if first_window_segment_index is None:
            first_window_segment_index = segment.index
        texts: list[str] = []
        for word in segment.words:
            if word.end >= window_start and word.start <= window_end:
                texts.append(word.text)
        if not texts:
            texts.append(segment.text)
        window_texts.extend(texts)
        observed = normalize_for_alignment("".join(texts))
        if not observed:
            continue
        score = alignment_similarity(expected, observed)
        if score > best_score:
            best_score = score
            best_segment_index = segment.index
    window_observed = normalize_for_alignment("".join(window_texts))
    if window_observed:
        window_score = alignment_similarity(expected, window_observed)
        if window_score > best_score:
            best_score = window_score
            best_segment_index = first_window_segment_index
    return best_score, best_segment_index


def alignment_similarity(expected: str, observed: str) -> float:
    candidates = [sequence_similarity(expected, observed)]
    expected_script = strip_latin_for_alignment(expected)
    observed_script = strip_latin_for_alignment(observed)
    if expected_script and observed_script:
        candidates.append(sequence_similarity(expected_script, observed_script))
    return max(candidates)


def sequence_similarity(expected: str, observed: str) -> float:
    if expected in observed:
        return len(expected) / max(len(observed), 1)
    if observed in expected:
        return len(observed) / max(len(expected), 1)
    return difflib.SequenceMatcher(None, expected, observed).ratio()


def strip_latin_for_alignment(value: str) -> str:
    return re.sub(r"[a-z0-9]+", "", value, flags=re.IGNORECASE)


def fetch_shazam_lyrics(shazam_url: str, settings: Settings) -> ShazamLyricsResponse:
    request = urllib.request.Request(
        shazam_url,
        headers={
            "User-Agent": "Mozilla/5.0 LearnMoreAPI/1.0",
            "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
        },
    )
    try:
        with urllib.request.urlopen(request, timeout=settings.shazam_timeout_seconds) as response:
            page_html = response.read().decode("utf-8", "replace")
    except Exception as exc:
        raise RuntimeError("Shazam lyrics page could not be fetched.") from exc

    metadata = extract_shazam_json_ld(page_html)
    lyrics_text = str(metadata.get("lyricsText") or "").strip()
    if not lyrics_text:
        raise RuntimeError("Shazam lyrics were not found in the page metadata.")
    lines = [
        LyricLine(lyricId=index, japanese=line)
        for index, line in enumerate(split_lyrics_lines(lyrics_text), start=1)
    ]
    if not lines:
        raise RuntimeError("Shazam lyrics did not contain usable lyric lines.")
    return ShazamLyricsResponse(
        shazamUrl=shazam_url,
        title=str(metadata.get("title") or "").strip(),
        artist=str(metadata.get("artist") or "").strip(),
        lines=lines,
    )


def extract_shazam_json_ld(page_html: str) -> dict[str, str]:
    for raw_script in re.findall(
        r'<script[^>]+type=["\']application/ld\+json["\'][^>]*>(.*?)</script>',
        page_html,
        flags=re.IGNORECASE | re.DOTALL,
    ):
        script = html.unescape(raw_script).strip()
        try:
            payload = json.loads(script)
        except json.JSONDecodeError:
            continue
        found = find_shazam_lyrics_payload(payload)
        if found:
            return found
    return {}


def find_shazam_lyrics_payload(value: Any) -> dict[str, str] | None:
    if isinstance(value, list):
        for item in value:
            found = find_shazam_lyrics_payload(item)
            if found:
                return found
    if not isinstance(value, dict):
        return None

    recording = value.get("recordingOf")
    lyrics = recording.get("lyrics") if isinstance(recording, dict) else value.get("lyrics")
    lyrics_text = lyrics.get("text") if isinstance(lyrics, dict) else None
    if isinstance(lyrics_text, str) and lyrics_text.strip():
        artist = ""
        by_artist = value.get("byArtist")
        if isinstance(by_artist, dict):
            artist = str(by_artist.get("name") or "")
        elif isinstance(by_artist, list) and by_artist and isinstance(by_artist[0], dict):
            artist = str(by_artist[0].get("name") or "")
        if not artist and isinstance(recording, dict):
            composers = recording.get("composer")
            if isinstance(composers, list) and composers and isinstance(composers[0], dict):
                artist = str(composers[0].get("name") or "")
            elif isinstance(composers, dict):
                artist = str(composers.get("name") or "")
        return {
            "title": str(value.get("name") or ""),
            "artist": artist,
            "lyricsText": lyrics_text,
        }

    for item in value.values():
        found = find_shazam_lyrics_payload(item)
        if found:
            return found
    return None


def split_lyrics_lines(lyrics_text: str) -> list[str]:
    lines: list[str] = []
    for raw in lyrics_text.splitlines():
        line = raw.strip()
        if not line or re.fullmatch(r"\[[^\]]+\]", line):
            continue
        lines.append(line)
    return lines


def normalize_for_similarity(value: str) -> str:
    return re.sub(r"[\W_]+", "", value, flags=re.UNICODE).casefold()


def slugify_shazam_title(value: str) -> str:
    normalized = value.strip().lower()
    normalized = re.sub(r"['’]", "", normalized)
    normalized = re.sub(r"[^a-z0-9\u3040-\u30ff\u3400-\u9fff]+", "-", normalized, flags=re.UNICODE)
    normalized = normalized.strip("-")
    return urllib.parse.quote(normalized or "song")


def align_lyrics_to_whisper(
    lyric_lines: list[LyricLine],
    whisper_segments: list[WhisperSegment],
    duration_seconds: float | None,
) -> list[AlignedLyricLine]:
    if not lyric_lines:
        return []
    duration = duration_seconds or (whisper_segments[-1].end if whisper_segments else float(len(lyric_lines) * 4))
    word_alignment = build_word_alignment_index(whisper_segments)
    matched: list[AlignedLyricLine] = []
    current_segment = 0
    current_char = 0
    for index, line in enumerate(lyric_lines):
        word_match = find_word_time_match(line.japanese, word_alignment, current_char)
        if word_match is not None:
            start, end, score, first_word, last_word, next_char = word_match
            source = "whisper_word_match"
            current_char = next_char
            best_index = None
            current_segment = find_segment_index_for_word(whisper_segments, first_word) or current_segment
            whisper_word_start = first_word
            whisper_word_end = last_word
        else:
            best_index, score = find_best_segment(line.japanese, whisper_segments, current_segment)
            if best_index is not None and score >= 0.32:
                segment = whisper_segments[best_index]
                start = segment.start
                end = max(segment.end, start + 0.5)
                source = "whisper_segment_match"
                current_segment = best_index
                current_char = find_stream_position_after_segment(word_alignment, segment)
            else:
                start = duration * index / len(lyric_lines)
                end = duration * (index + 1) / len(lyric_lines)
                source = "proportional_fallback"
                best_index = None
            whisper_word_start = None
            whisper_word_end = None
        if best_index is not None and source == "whisper_segment_match":
            segment = whisper_segments[best_index]
            start = segment.start
            end = max(segment.end, start + 0.5)
        matched.append(
            AlignedLyricLine(
                lyricId=line.lyricId,
                japanese=line.japanese,
                start=round(float(start), 3),
                end=round(float(end), 3),
                source=source,
                score=round(float(score), 4),
                whisperSegmentIndex=best_index + 1 if best_index is not None else None,
                whisperWordStartIndex=whisper_word_start,
                whisperWordEndIndex=whisper_word_end,
            )
        )
    return enforce_monotonic_times(matched, duration)


def align_high_accuracy_lyrics_to_whisper(
    lyric_lines: list[HighAccuracyLyricLine],
    whisper_segments: list[WhisperSegment],
    duration_seconds: float | None,
) -> list[AlignedLyricLine]:
    if not lyric_lines:
        return []

    plain_lines = [
        LyricLine(lyricId=line.lyricId, japanese=line.japanese)
        for line in lyric_lines
    ]
    greedy_aligned = align_lyrics_to_whisper(plain_lines, whisper_segments, duration_seconds)
    greedy_by_id = {line.lyricId: line for line in greedy_aligned}

    if not any(line.currentStart is not None for line in lyric_lines):
        return greedy_aligned

    duration = duration_seconds or (whisper_segments[-1].end if whisper_segments else float(len(lyric_lines) * 4))
    aligned_by_id: dict[int, AlignedLyricLine] = {}
    verified_ids: set[int] = set()
    previous_start = 0.0
    for line in lyric_lines:
        hint_match = find_timing_hint_segment_match(line, whisper_segments, previous_start)
        if hint_match is not None and is_reliable_audio_alignment(hint_match):
            aligned_by_id[line.lyricId] = hint_match
            verified_ids.add(line.lyricId)
            previous_start = hint_match.start
            continue

        greedy_line = greedy_by_id.get(line.lyricId)
        if is_usable_greedy_alignment(line, greedy_line):
            aligned_by_id[line.lyricId] = greedy_line
            verified_ids.add(line.lyricId)
            previous_start = greedy_line.start

    verified_ratio = len(verified_ids) / max(len(lyric_lines), 1)
    allow_context_fallback = verified_ratio >= 0.6
    completed: list[AlignedLyricLine] = []
    for index, line in enumerate(lyric_lines):
        aligned = aligned_by_id.get(line.lyricId)
        if aligned is None and allow_context_fallback and line.currentStart is not None:
            next_start = next(
                (
                    candidate.currentStart
                    for candidate in lyric_lines[index + 1 :]
                    if candidate.currentStart is not None
                ),
                None,
            )
            aligned = build_current_timestamp_context_line(line, next_start, duration)
        if aligned is None:
            aligned = greedy_by_id.get(line.lyricId)
        if aligned is None:
            start = duration * index / len(lyric_lines)
            end = duration * (index + 1) / len(lyric_lines)
            aligned = AlignedLyricLine(
                lyricId=line.lyricId,
                japanese=line.japanese,
                start=round(start, 3),
                end=round(max(end, start + 0.25), 3),
                source="proportional_fallback",
                score=0.0,
            )
        completed.append(aligned)

    return enforce_monotonic_times(completed, duration)


def align_high_accuracy_lyrics_to_asr_sequence(
    lyric_lines: list[HighAccuracyLyricLine],
    whisper_segments: list[WhisperSegment],
    duration_seconds: float | None,
) -> list[AlignedLyricLine]:
    if not lyric_lines:
        return []

    duration = duration_seconds or (whisper_segments[-1].end if whisper_segments else float(len(lyric_lines) * 4))
    word_alignment = build_word_alignment_index(whisper_segments)
    asr_stream = str(word_alignment.get("stream") or "")
    char_to_word: list[WhisperWord] = word_alignment.get("charToWord") or []
    lyric_stream, lyric_char_to_id, line_ranges = build_lyric_sequence_index(lyric_lines)
    if not lyric_stream or not asr_stream or not char_to_word:
        return build_unmatched_high_accuracy_lines(lyric_lines, duration)

    matcher = difflib.SequenceMatcher(None, lyric_stream, asr_stream, autojunk=False)
    matches_by_id: dict[int, list[tuple[int, int, WhisperWord]]] = {line.lyricId: [] for line in lyric_lines}
    for block in matcher.get_matching_blocks():
        for offset in range(block.size):
            lyric_pos = block.a + offset
            asr_pos = block.b + offset
            if lyric_pos >= len(lyric_char_to_id) or asr_pos >= len(char_to_word):
                continue
            lyric_id = lyric_char_to_id[lyric_pos]
            matches_by_id.setdefault(lyric_id, []).append((lyric_pos, asr_pos, char_to_word[asr_pos]))

    aligned: list[AlignedLyricLine] = []
    previous_reliable_end = 0.0
    for index, line in enumerate(lyric_lines):
        line_start, line_end, normalized = line_ranges.get(line.lyricId, (0, 0, ""))
        line_matches = [
            match
            for match in matches_by_id.get(line.lyricId, [])
            if line_start <= match[0] < line_end
        ]
        coverage = len({match[0] for match in line_matches}) / max(len(normalized), 1)
        if normalized and is_reliable_global_sequence_coverage(coverage, len(normalized), line_matches):
            first_match = min(line_matches, key=lambda match: (match[0], match[1]))
            last_match = max(line_matches, key=lambda match: (match[0], match[1]))
            prefix_chars = max(0, first_match[0] - line_start)
            suffix_chars = max(0, line_end - 1 - last_match[0])
            estimated_start_char = max(0, first_match[1] - prefix_chars)
            estimated_end_char = min(len(char_to_word) - 1, last_match[1] + suffix_chars)
            first_word = char_to_word[estimated_start_char]
            last_word = char_to_word[estimated_end_char]
            start = max(previous_reliable_end, min(float(first_word.start), duration))
            end = max(start + 0.25, min(float(last_word.end), duration))
            previous_reliable_end = end
            aligned.append(
                AlignedLyricLine(
                    lyricId=line.lyricId,
                    japanese=line.japanese,
                    start=round(start, 3),
                    end=round(end, 3),
                    source="whisper_global_sequence_match",
                    score=round(float(coverage), 4),
                    whisperWordStartIndex=first_word.index,
                    whisperWordEndIndex=last_word.index,
                )
            )
            continue

        next_start = next(
            (
                candidate.currentStart
                for candidate in lyric_lines[index + 1 :]
                if candidate.currentStart is not None
            ),
            None,
        )
        if line.currentStart is not None:
            aligned.append(build_current_timestamp_context_line(line, next_start, duration))
            continue

        start = duration * index / len(lyric_lines)
        end = duration * (index + 1) / len(lyric_lines)
        aligned.append(
            AlignedLyricLine(
                lyricId=line.lyricId,
                japanese=line.japanese,
                start=round(start, 3),
                end=round(max(end, start + 0.25), 3),
                source="proportional_fallback",
                score=0.0,
            )
        )

    return promote_contextual_timestamp_bridges(enforce_monotonic_times(aligned, duration))


def build_lyric_sequence_index(
    lyric_lines: list[HighAccuracyLyricLine],
) -> tuple[str, list[int], dict[int, tuple[int, int, str]]]:
    stream = ""
    char_to_id: list[int] = []
    line_ranges: dict[int, tuple[int, int, str]] = {}
    for line in lyric_lines:
        normalized = normalize_for_alignment(line.japanese)
        start = len(stream)
        stream += normalized
        char_to_id.extend([line.lyricId] * len(normalized))
        line_ranges[line.lyricId] = (start, len(stream), normalized)
    return stream, char_to_id, line_ranges


def is_reliable_global_sequence_coverage(
    coverage: float,
    normalized_length: int,
    matches: list[tuple[int, int, WhisperWord]],
) -> bool:
    if not matches:
        return False
    if normalized_length <= 4:
        return coverage >= 0.85
    if normalized_length <= 8:
        return coverage >= 0.70
    return coverage >= 0.60


def build_unmatched_high_accuracy_lines(
    lyric_lines: list[HighAccuracyLyricLine],
    duration: float,
) -> list[AlignedLyricLine]:
    aligned: list[AlignedLyricLine] = []
    for index, line in enumerate(lyric_lines):
        if line.currentStart is not None:
            next_start = next(
                (
                    candidate.currentStart
                    for candidate in lyric_lines[index + 1 :]
                    if candidate.currentStart is not None
                ),
                None,
            )
            aligned.append(build_current_timestamp_context_line(line, next_start, duration))
            continue
        start = duration * index / len(lyric_lines)
        end = duration * (index + 1) / len(lyric_lines)
        aligned.append(
            AlignedLyricLine(
                lyricId=line.lyricId,
                japanese=line.japanese,
                start=round(start, 3),
                end=round(max(end, start + 0.25), 3),
                source="proportional_fallback",
                score=0.0,
            )
        )
    return promote_contextual_timestamp_bridges(enforce_monotonic_times(aligned, duration))


def align_high_accuracy_lyrics_to_whisperx_lyrics(
    lyric_lines: list[HighAccuracyLyricLine],
    whisper_segments: list[WhisperSegment],
    duration_seconds: float | None,
) -> list[AlignedLyricLine]:
    if not lyric_lines:
        return []

    duration = duration_seconds or (whisper_segments[-1].end if whisper_segments else float(len(lyric_lines) * 4))
    verified_segment_indices = {
        index
        for index, segment in enumerate(whisper_segments[: len(lyric_lines)])
        if segment.words and is_asr_verified_lyric_segment(segment)
    }
    forced_sequence_by_id = build_internal_forced_sequence_alignment_by_id(
        lyric_lines,
        whisper_segments,
        duration,
    )
    verified_ratio = len(verified_segment_indices) / max(len(lyric_lines), 1)
    allow_contextual_forced_alignment = verified_ratio >= 0.60
    aligned: list[AlignedLyricLine] = []
    for index, line in enumerate(lyric_lines):
        segment = whisper_segments[index] if index < len(whisper_segments) else None
        if segment is not None:
            score = alignment_similarity(
                normalize_for_alignment(line.japanese),
                normalize_for_alignment(segment.text),
            )
            has_verified_asr_anchor = segment.words and is_asr_verified_lyric_segment(segment)
            has_line_specific_asr_anchor = segment._asr_anchor_source in {
                "whisper_local_asr_anchor_match",
                "whisper_global_sequence_match",
            }
            if has_verified_asr_anchor and (score >= 0.72 or has_line_specific_asr_anchor):
                anchor_score = segment._asr_anchor_score if segment._asr_anchor_score is not None else score
                anchor_source = segment._asr_anchor_source or "whisper_lyric_forced_alignment_asr_verified"
                aligned.append(
                    AlignedLyricLine(
                        lyricId=line.lyricId,
                        japanese=line.japanese,
                        start=round(float(segment.start), 3),
                        end=round(max(float(segment.end), float(segment.start) + 0.25), 3),
                        source=anchor_source,
                        score=round(float(anchor_score), 4),
                        whisperSegmentIndex=segment.index,
                        whisperWordStartIndex=segment.words[0].index,
                        whisperWordEndIndex=segment.words[-1].index,
                    )
                )
                continue
        forced_sequence_line = forced_sequence_by_id.get(line.lyricId)
        if forced_sequence_line is not None:
            aligned.append(forced_sequence_line)
            continue
        if segment is not None:
            if (
                score >= 0.72
                and segment.words
                and allow_contextual_forced_alignment
                and is_inside_verified_alignment_context(
                    index,
                    segment,
                    whisper_segments,
                    verified_segment_indices,
                    len(lyric_lines),
                )
            ):
                start = float(segment.start)
                if aligned and not any(candidate > index for candidate in verified_segment_indices):
                    start = min(start, aligned[-1].end)
                aligned.append(
                    AlignedLyricLine(
                        lyricId=line.lyricId,
                        japanese=line.japanese,
                        start=round(start, 3),
                        end=round(max(float(segment.end), start + 0.25), 3),
                        source="whisper_lyric_forced_alignment_global_context",
                        score=round(float(verified_ratio), 4),
                        whisperSegmentIndex=segment.index,
                        whisperWordStartIndex=segment.words[0].index,
                        whisperWordEndIndex=segment.words[-1].index,
                    )
                )
                continue
            if is_usable_internal_forced_lyric_alignment(index, line, segment, whisper_segments, duration):
                aligned.append(
                    AlignedLyricLine(
                        lyricId=line.lyricId,
                        japanese=line.japanese,
                        start=round(float(segment.start), 3),
                        end=round(max(float(segment.end), float(segment.start) + 0.25), 3),
                        source="whisper_lyric_forced_alignment_internal",
                        score=round(float(score), 4),
                        whisperSegmentIndex=segment.index,
                        whisperWordStartIndex=segment.words[0].index if segment.words else None,
                        whisperWordEndIndex=segment.words[-1].index if segment.words else None,
                    )
                )
                continue

        next_start = next(
            (
                candidate.currentStart
                for candidate in lyric_lines[index + 1 :]
                if candidate.currentStart is not None
            ),
            None,
        )
        if line.currentStart is not None:
            aligned.append(build_current_timestamp_context_line(line, next_start, duration))
            continue

        start = duration * index / len(lyric_lines)
        end = duration * (index + 1) / len(lyric_lines)
        aligned.append(
            AlignedLyricLine(
                lyricId=line.lyricId,
                japanese=line.japanese,
                start=round(start, 3),
                end=round(max(end, start + 0.25), 3),
                source="proportional_fallback",
                score=0.0,
            )
        )

    return promote_contextual_timestamp_bridges(enforce_monotonic_times(aligned, duration))


def build_internal_forced_sequence_alignment_by_id(
    lyric_lines: list[HighAccuracyLyricLine],
    whisper_segments: list[WhisperSegment],
    duration: float,
) -> dict[int, AlignedLyricLine]:
    if not any(segment._forced_lyric_alignment for segment in whisper_segments):
        return {}
    sequence_aligned = align_high_accuracy_lyrics_to_asr_sequence(lyric_lines, whisper_segments, duration)
    lyric_by_id = {line.lyricId: line for line in lyric_lines}
    aligned_by_id: dict[int, AlignedLyricLine] = {}
    for line in sequence_aligned:
        if not is_reliable_audio_alignment(line):
            continue
        if not is_forced_alignment_near_current_start(lyric_by_id.get(line.lyricId), line.start):
            continue
        aligned_by_id[line.lyricId] = line.model_copy(
            update={
                "source": normalize_internal_forced_sequence_source(line.source),
                "score": max(float(line.score), 0.90),
            }
        )
    return aligned_by_id


def normalize_internal_forced_sequence_source(source: str) -> str:
    if source in {"proportional_fallback", "current_timestamp_context"}:
        return source
    if source == "whisper_contextual_timestamp_bridge":
        return "whisper_lyric_forced_alignment_sequence_bridge"
    return "whisper_lyric_forced_alignment_sequence"


def is_usable_internal_forced_lyric_alignment(
    index: int,
    line: HighAccuracyLyricLine,
    segment: WhisperSegment,
    whisper_segments: list[WhisperSegment],
    duration: float,
) -> bool:
    if not segment._forced_lyric_alignment:
        return False
    if not line.japanese.strip() or not segment.text.strip():
        return False
    if not math.isfinite(float(segment.start)) or not math.isfinite(float(segment.end)):
        return False
    if segment.start < -0.05 or segment.end <= segment.start:
        return False
    if duration > 0 and segment.start > duration + 0.5:
        return False
    if not is_forced_alignment_near_current_start(line, segment.start):
        return False
    previous_segment = whisper_segments[index - 1] if index > 0 and index - 1 < len(whisper_segments) else None
    next_segment = whisper_segments[index + 1] if index + 1 < len(whisper_segments) else None
    if previous_segment is not None and segment.start < previous_segment.start - 0.25:
        return False
    if next_segment is not None and segment.start > next_segment.end + 0.25:
        return False
    if segment.words:
        first_word = segment.words[0]
        last_word = segment.words[-1]
        if first_word.start < segment.start - 0.5 or last_word.end > segment.end + 0.5:
            return False
    return True


def is_forced_alignment_near_current_start(
    line: HighAccuracyLyricLine | None,
    start: float,
    tolerance_seconds: float = 8.0,
) -> bool:
    if line is None or line.currentStart is None:
        return True
    return abs(float(start) - float(line.currentStart)) <= tolerance_seconds


def is_inside_verified_alignment_context(
    index: int,
    segment: WhisperSegment,
    whisper_segments: list[WhisperSegment],
    verified_indices: set[int],
    total_count: int,
    max_gap: int = 8,
    edge_gap: int = 4,
    tolerance_seconds: float = 2.0,
) -> bool:
    previous_verified = max((candidate for candidate in verified_indices if candidate < index), default=None)
    next_verified = min((candidate for candidate in verified_indices if candidate > index), default=None)
    if previous_verified is None or next_verified is None:
        if previous_verified is None and next_verified is None:
            return False
        if previous_verified is None:
            if next_verified >= len(whisper_segments):
                return False
            next_segment = whisper_segments[next_verified]
            return next_verified <= edge_gap and segment.end <= next_segment.start + tolerance_seconds
        if previous_verified >= len(whisper_segments):
            return False
        previous_segment = whisper_segments[previous_verified]
        return (total_count - previous_verified - 1) <= edge_gap and segment.start >= previous_segment.end - tolerance_seconds

    if previous_verified >= len(whisper_segments) or next_verified >= len(whisper_segments):
        return False
    if next_verified - previous_verified - 1 > max_gap:
        return False

    previous_segment = whisper_segments[previous_verified]
    next_segment = whisper_segments[next_verified]
    return (
        segment.start >= previous_segment.end - tolerance_seconds
        and segment.end <= next_segment.start + tolerance_seconds
    )


def stabilize_unreliable_lines_with_current_start_context(
    lyric_lines: list[HighAccuracyLyricLine],
    aligned: list[AlignedLyricLine],
    duration_seconds: float | None,
) -> list[AlignedLyricLine]:
    if not lyric_lines or not aligned:
        return aligned
    duration = duration_seconds or infer_duration_from_current_starts(lyric_lines)
    lyric_by_id = {line.lyricId: line for line in lyric_lines}
    if not should_trust_current_start_timeline_for_stabilization(lyric_by_id, aligned):
        return aligned

    stabilized: list[AlignedLyricLine] = []
    for index, line in enumerate(aligned):
        lyric_line = lyric_by_id.get(line.lyricId)
        if (
            lyric_line is not None
            and lyric_line.currentStart is not None
            and (
                not is_reliable_audio_alignment(line)
                or is_suspicious_alignment_for_current_timeline(lyric_line, line)
            )
        ):
            next_start = next(
                (
                    candidate.currentStart
                    for candidate in lyric_lines[index + 1 :]
                    if candidate.currentStart is not None
                ),
                None,
            )
            stabilized.append(build_current_timestamp_context_line(lyric_line, next_start, duration))
            continue
        stabilized.append(line)

    return promote_contextual_timestamp_bridges(
        enforce_monotonic_times(stabilized, duration),
        min_reliable_ratio=0.15,
        min_audio_verified_ratio=0.15,
        max_gap=len(aligned),
        edge_gap=4,
    )


def should_trust_current_start_timeline_for_stabilization(
    lyric_by_id: dict[int, HighAccuracyLyricLine],
    aligned: list[AlignedLyricLine],
) -> bool:
    deltas = [
        abs(float(line.start) - float(lyric_line.currentStart))
        for line in aligned
        if is_reliable_audio_alignment(line)
        for lyric_line in [lyric_by_id.get(line.lyricId)]
        if lyric_line is not None and lyric_line.currentStart is not None
        if not is_suspicious_alignment_for_current_timeline(lyric_line, line)
    ]
    if len(deltas) < max(8, int(len(aligned) * 0.15)):
        return False
    sorted_deltas = sorted(deltas)
    p90 = sorted_deltas[int((len(sorted_deltas) - 1) * 0.9)]
    return statistics.median(sorted_deltas) <= 4.0 and p90 <= 10.0


def is_suspicious_alignment_for_current_timeline(
    lyric_line: HighAccuracyLyricLine,
    aligned: AlignedLyricLine,
) -> bool:
    if lyric_line.currentStart is None:
        return False
    if abs(float(aligned.start) - float(lyric_line.currentStart)) > 8.0:
        return True
    duration = float(aligned.end) - float(aligned.start)
    normalized = normalize_for_alignment(lyric_line.japanese)
    return duration <= 0.30 and len(normalized) >= 8


def promote_contextual_timestamp_bridges(
    aligned: list[AlignedLyricLine],
    min_reliable_ratio: float = 0.85,
    min_audio_verified_ratio: float = 0.70,
    max_gap: int = 5,
    edge_gap: int = 3,
) -> list[AlignedLyricLine]:
    if not aligned:
        return aligned
    reliable_indices = {
        index
        for index, line in enumerate(aligned)
        if is_reliable_audio_alignment(line)
    }
    forced_bridge_indices = {
        index
        for index, line in enumerate(aligned)
        if is_forced_alignment_bridge_candidate(line)
    }
    reliable_ratio = len(reliable_indices) / len(aligned)
    support_indices = reliable_indices | forced_bridge_indices
    support_ratio = len(support_indices) / len(aligned)
    if reliable_ratio < min_audio_verified_ratio or support_ratio < min_reliable_ratio:
        return aligned

    promoted: list[AlignedLyricLine] = []
    for index, line in enumerate(aligned):
        if (
            line.source not in {"current_timestamp_context", "proportional_fallback"}
            and not is_forced_alignment_bridge_candidate(line)
        ):
            promoted.append(line)
            continue
        if not is_inside_reliable_line_context(
            index,
            line,
            aligned,
            support_indices,
            max_gap=max_gap,
            edge_gap=edge_gap,
        ):
            promoted.append(line)
            continue
        promoted.append(
            line.model_copy(
                update={
                    "source": "whisper_contextual_timestamp_bridge",
                    "score": round(float(max(0.85, min_reliable_ratio, reliable_ratio)), 4),
                }
            )
        )
    return promoted


def is_forced_alignment_bridge_candidate(line: AlignedLyricLine) -> bool:
    if line.source not in {
        "whisper_lyric_forced_alignment_internal",
        "whisperx_lyric_forced_alignment_internal",
    }:
        return False
    if line.score >= 0.90:
        return False
    return math.isfinite(float(line.start)) and math.isfinite(float(line.end)) and line.end > line.start


def is_inside_reliable_line_context(
    index: int,
    line: AlignedLyricLine,
    aligned: list[AlignedLyricLine],
    reliable_indices: set[int],
    max_gap: int = 5,
    edge_gap: int = 3,
    tolerance_seconds: float = 2.0,
) -> bool:
    previous_reliable = max((candidate for candidate in reliable_indices if candidate < index), default=None)
    next_reliable = min((candidate for candidate in reliable_indices if candidate > index), default=None)
    if previous_reliable is None or next_reliable is None:
        if previous_reliable is None and next_reliable is None:
            return False
        if previous_reliable is None:
            next_line = aligned[next_reliable]
            return next_reliable <= edge_gap and line.start <= next_line.start + tolerance_seconds
        previous_line = aligned[previous_reliable]
        return (len(aligned) - previous_reliable - 1) <= edge_gap and line.start >= previous_line.start - tolerance_seconds

    if next_reliable - previous_reliable - 1 > max_gap:
        return False
    previous_line = aligned[previous_reliable]
    next_line = aligned[next_reliable]
    return (
        line.start >= previous_line.start - tolerance_seconds
        and line.start <= next_line.start + tolerance_seconds
    )


def is_asr_verified_lyric_segment(segment: WhisperSegment) -> bool:
    if segment._asr_anchor_score is None:
        return False
    if segment._asr_anchor_source == "whisper_global_sequence_match":
        return segment._asr_anchor_score >= 0.60
    return segment._asr_anchor_score >= 0.70


def find_timing_hint_segment_match(
    line: HighAccuracyLyricLine,
    whisper_segments: list[WhisperSegment],
    previous_start: float,
) -> AlignedLyricLine | None:
    if line.currentStart is None:
        return None
    normalized_lyric = normalize_for_alignment(line.japanese)
    if not normalized_lyric:
        return None

    window_seconds = 18.0
    best: tuple[float, float, int, WhisperSegment] | None = None
    for segment_index, segment in enumerate(whisper_segments):
        if segment.end < line.currentStart - window_seconds:
            continue
        if segment.start > line.currentStart + window_seconds:
            continue
        if segment.start < previous_start - 0.25:
            continue
        normalized_segment = normalize_for_alignment(segment.text)
        if not normalized_segment:
            continue
        score = alignment_similarity(normalized_lyric, normalized_segment)
        if score < 0.32:
            continue
        proximity = abs(segment.start - line.currentStart)
        rank = (score, -proximity, -segment_index)
        if best is None or rank > (best[0], best[1], best[2]):
            best = (score, -proximity, -segment_index, segment)

    if best is None:
        return None

    score, _, _, segment = best
    return AlignedLyricLine(
        lyricId=line.lyricId,
        japanese=line.japanese,
        start=round(float(segment.start), 3),
        end=round(max(float(segment.end), float(segment.start) + 0.5), 3),
        source="whisper_timing_hint_match",
        score=round(float(score), 4),
        whisperSegmentIndex=segment.index,
    )


def is_usable_greedy_alignment(
    line: HighAccuracyLyricLine,
    aligned: AlignedLyricLine | None,
) -> bool:
    if aligned is None:
        return False
    if not is_reliable_audio_alignment(aligned):
        return False
    if line.currentStart is None:
        return True
    return abs(aligned.start - line.currentStart) <= 18.0


def build_current_timestamp_context_line(
    line: HighAccuracyLyricLine,
    next_start: float | None,
    duration: float,
) -> AlignedLyricLine:
    start = max(0.0, min(float(line.currentStart or 0.0), duration))
    if next_start is not None and next_start > start:
        end = min(duration, max(start + 0.25, float(next_start)))
    else:
        end = min(duration, start + 4.0)
    return AlignedLyricLine(
        lyricId=line.lyricId,
        japanese=line.japanese,
        start=round(start, 3),
        end=round(max(end, start + 0.25), 3),
        source="current_timestamp_context",
        score=0.34,
    )


def is_reliable_audio_alignment(line: AlignedLyricLine) -> bool:
    source = line.source or ""
    if source in {"proportional_fallback", "current_timestamp_context"} or "unverified" in source:
        return False
    if is_latin_dominant_lyric(line.japanese) and not is_reliable_latin_alignment_source(source):
        return False
    if source.startswith("shazam_timed_lyrics_verified"):
        return line.score >= 0.52
    if source in {"whisper_word_match", "whisperx_word_match"}:
        return line.score >= 0.70
    if source in {"whisper_segment_match", "whisperx_segment_match"}:
        return line.score >= 0.58
    if source in {"whisper_timing_hint_match", "whisperx_timing_hint_match"}:
        return line.score >= 0.72
    if source in {"whisper_global_sequence_match", "whisperx_global_sequence_match"}:
        return line.score >= 0.60
    if source in {"whisper_local_asr_anchor_match", "whisperx_local_asr_anchor_match"}:
        return line.score >= 0.70
    if source in {
        "whisper_lyric_forced_alignment_global_context",
        "whisperx_lyric_forced_alignment_global_context",
    }:
        return line.score >= 0.60
    if source in {"whisper_contextual_timestamp_bridge", "whisperx_contextual_timestamp_bridge"}:
        return line.score >= 0.85
    if source in {
        "whisper_lyric_forced_alignment_asr_verified",
        "whisperx_lyric_forced_alignment_asr_verified",
    }:
        return line.score >= 0.70
    if source in {
        "whisper_lyric_forced_alignment_internal",
        "whisperx_lyric_forced_alignment_internal",
    }:
        return line.score >= 0.90
    if source in {
        "whisper_lyric_forced_alignment_sequence",
        "whisperx_lyric_forced_alignment_sequence",
        "whisper_lyric_forced_alignment_sequence_bridge",
        "whisperx_lyric_forced_alignment_sequence_bridge",
    }:
        return line.score >= 0.90
    if source in {"whisper_lyric_forced_alignment", "whisperx_lyric_forced_alignment"}:
        return False
    return line.score >= 0.80


def label_low_confidence_latin_alignments(lines: list[AlignedLyricLine]) -> list[AlignedLyricLine]:
    labeled: list[AlignedLyricLine] = []
    for line in lines:
        source = line.source or ""
        if (
            is_latin_dominant_lyric(line.japanese)
            and source not in {"proportional_fallback", "current_timestamp_context"}
            and "unverified" not in source
            and not is_reliable_latin_alignment_source(source)
        ):
            labeled.append(line.model_copy(update={"source": f"{source}_unverified_latin"}))
            continue
        labeled.append(line)
    return labeled


def is_latin_dominant_lyric(text: str) -> bool:
    latin_count = len(re.findall(r"[A-Za-z]", text))
    if latin_count < 4:
        return False
    japanese_count = len(re.findall(r"[\u3040-\u30ff\u3400-\u9fff]", text))
    if japanese_count == 0:
        return True
    return latin_count / max(latin_count + japanese_count, 1) >= 0.45


def is_reliable_latin_alignment_source(source: str) -> bool:
    if source.startswith("shazam_timed_lyrics_verified"):
        return True
    return source in {
        "whisper_word_match",
        "whisperx_word_match",
        "whisper_local_asr_anchor_match",
        "whisperx_local_asr_anchor_match",
        "whisper_contextual_timestamp_bridge",
        "whisperx_contextual_timestamp_bridge",
        "whisper_lyric_forced_alignment_asr_verified",
        "whisperx_lyric_forced_alignment_asr_verified",
    }


def label_whisperx_alignment_sources(lines: list[AlignedLyricLine]) -> list[AlignedLyricLine]:
    labeled: list[AlignedLyricLine] = []
    for line in lines:
        source = line.source or ""
        if source.startswith("whisper_"):
            source = "whisperx_" + source[len("whisper_") :]
            labeled.append(line.model_copy(update={"source": source}))
        else:
            labeled.append(line)
    return labeled


def build_word_alignment_index(whisper_segments: list[WhisperSegment]) -> dict[str, Any]:
    stream = ""
    char_to_word: list[WhisperWord] = []
    words: list[WhisperWord] = []
    word_end_by_index: dict[int, int] = {}
    for segment in whisper_segments:
        source_words = segment.words or [
            WhisperWord(index=len(words) + 1, start=segment.start, end=segment.end, text=segment.text)
        ]
        contextual = build_contextual_word_reading(source_words)
        if contextual is not None:
            normalized, mapped_words = contextual
            stream += normalized
            char_to_word.extend(mapped_words)
            mapped_indices = {word.index for word in mapped_words}
            words.extend(word for word in source_words if word.index in mapped_indices)
            segment_offset = len(stream) - len(normalized)
            for offset, word in enumerate(mapped_words, 1):
                word_end_by_index[word.index] = segment_offset + offset
            continue
        for word in source_words:
            normalized = normalize_for_alignment(word.text)
            if not normalized:
                continue
            words.append(word)
            stream += normalized
            char_to_word.extend([word] * len(normalized))
            word_end_by_index[word.index] = len(stream)
    return {"stream": stream, "charToWord": char_to_word, "words": words, "wordEndByIndex": word_end_by_index}


def build_contextual_word_reading(
    words: list[WhisperWord],
) -> tuple[str, list[WhisperWord]] | None:
    # Reading isolated ASR words changes inflected kanji (e.g. 光 + る).
    # Convert the complete segment, then map its reading back to word times.
    converter = get_kakasi_converter()
    text = "".join(word.text for word in words)
    if converter is None or not text or not re.search(r"[\u3040-\u30ff\u3400-\u9fff]", text):
        return None
    try:
        tokens = converter.convert(text)
    except Exception:
        return None
    if "".join(str(token.get("orig") or "") for token in tokens) != text:
        return None

    source_mapping = [word for word in words for _ in word.text]
    stream = ""
    mapped_words: list[WhisperWord] = []
    source_offset = 0
    for token in tokens:
        original = str(token.get("orig") or "")
        reading = normalize_for_alignment(str(token.get("hira") or original))
        if original and reading:
            stream += reading
            for index in range(len(reading)):
                # Preserve both ends of a token spanning multiple ASR words.
                offset = round(index * (len(original) - 1) / max(len(reading) - 1, 1))
                mapped_words.append(source_mapping[source_offset + offset])
        source_offset += len(original)
    return stream, mapped_words


def find_stream_position_after_segment(word_alignment: dict[str, Any], segment: WhisperSegment) -> int:
    words: list[WhisperWord] = word_alignment.get("words") or []
    word_end_by_index: dict[int, int] = word_alignment.get("wordEndByIndex") or {}
    segment_words = [word for word in words if word.start >= segment.start - 0.05 and word.end <= segment.end + 0.05]
    if not segment_words:
        return 0
    return int(word_end_by_index.get(segment_words[-1].index, 0))


def find_word_time_match(
    lyric_text: str,
    word_alignment: dict[str, Any],
    start_char: int,
) -> tuple[float, float, float, int, int, int] | None:
    normalized = normalize_for_alignment(lyric_text)
    stream = str(word_alignment.get("stream") or "")
    char_to_word: list[WhisperWord] = word_alignment.get("charToWord") or []
    if not normalized or not stream or not char_to_word:
        return None
    pos = stream.find(normalized, max(0, start_char - 8))
    score = 1.0
    if pos < 0:
        window_start = max(0, start_char - 20)
        window_end = min(len(stream), start_char + max(120, len(normalized) * 8))
        window = stream[window_start:window_end]
        match = difflib.SequenceMatcher(None, normalized, window).find_longest_match(
            0, len(normalized), 0, len(window)
        )
        if match.size < max(4, int(len(normalized) * 0.55)):
            return None
        pos = window_start + match.b
        score = match.size / max(1, len(normalized))
    end_pos = min(len(char_to_word) - 1, pos + max(1, len(normalized)) - 1)
    first_word = char_to_word[pos]
    last_word = char_to_word[end_pos]
    return (
        first_word.start,
        max(last_word.end, first_word.start + 0.25),
        score,
        first_word.index,
        last_word.index,
        end_pos + 1,
    )


def find_segment_index_for_word(whisper_segments: list[WhisperSegment], word_index: int) -> int | None:
    for segment_index, segment in enumerate(whisper_segments):
        if any(word.index == word_index for word in segment.words):
            return segment_index
    return None


def find_best_segment(
    lyric_text: str,
    whisper_segments: list[WhisperSegment],
    start_index: int,
) -> tuple[int | None, float]:
    normalized_lyric = normalize_for_alignment(lyric_text)
    if not normalized_lyric:
        return None, 0.0
    best_index: int | None = None
    best_score = 0.0
    for index in range(start_index, len(whisper_segments)):
        normalized_segment = normalize_for_alignment(whisper_segments[index].text)
        if not normalized_segment:
            continue
        if normalized_lyric in normalized_segment or normalized_segment in normalized_lyric:
            score = 1.0
        else:
            score = difflib.SequenceMatcher(None, normalized_lyric, normalized_segment).ratio()
        if score > best_score:
            best_index = index
            best_score = score
    return best_index, best_score


def normalize_for_alignment(value: str) -> str:
    reading = japanese_reading(value)
    return re.sub(r"[\s　、。,.!?！？「」『』（）()・…ー~〜\\-\\[\\]]+", "", reading).lower()


def japanese_reading(value: str) -> str:
    if not re.search(r"[\u3040-\u30ff\u3400-\u9fff]", value):
        return value
    converter = get_kakasi_converter()
    if converter is None:
        return value
    try:
        converted = converter.convert(value)
    except Exception:
        return value
    return "".join(str(item.get("hira") or item.get("orig") or "") for item in converted)


def japanese_romaji(value: str) -> str:
    text = value.strip()
    if not text:
        return ""
    if not re.search(r"[\u3040-\u30ff\u3400-\u9fff]", text):
        return text
    converter = get_kakasi_converter()
    if converter is None:
        return text
    try:
        converted = converter.convert(text)
    except Exception:
        return text
    roman = " ".join(
        str(item.get("hepburn") or item.get("orig") or "").strip()
        for item in converted
        if str(item.get("hepburn") or item.get("orig") or "").strip()
    )
    return roman or text


@lru_cache(maxsize=1)
def get_kakasi_converter() -> Any:
    try:
        import pykakasi
    except ImportError:
        return None
    return pykakasi.kakasi()


def enforce_monotonic_times(lines: list[AlignedLyricLine], duration: float) -> list[AlignedLyricLine]:
    previous_start = 0.0
    adjusted: list[AlignedLyricLine] = []
    for line in lines:
        start = max(previous_start, min(line.start, duration))
        end = max(start + 0.25, min(line.end, duration))
        previous_start = start
        adjusted.append(line.model_copy(update={"start": round(start, 3), "end": round(end, 3)}))
    return adjusted


def download_youtube_audio(youtube_url: str, settings: Settings) -> Path:
    ytdlp_bin = shutil.which(settings.ytdlp_bin) or settings.ytdlp_bin
    output_template = str(Path(settings.whisper_download_dir) / "audio-%(id)s-%(epoch)s.%(ext)s")
    args = [
        ytdlp_bin,
        "--no-playlist",
        "--extract-audio",
        "--audio-format",
        "wav",
        "--audio-quality",
        "0",
        "--output",
        output_template,
        "--print",
        "after_move:filepath",
        youtube_url,
    ]
    try:
        result = subprocess.run(args, text=True, capture_output=True, timeout=600)
    except (OSError, subprocess.TimeoutExpired) as exc:
        raise RuntimeError("yt-dlp could not download the YouTube audio.") from exc
    if result.returncode != 0:
        raise RuntimeError(f"yt-dlp failed: {truncate(result.stderr or result.stdout)}")

    candidates = [Path(line.strip()) for line in result.stdout.splitlines() if line.strip()]
    for candidate in reversed(candidates):
        if candidate.exists() and candidate.is_file():
            return candidate
    raise RuntimeError("yt-dlp did not produce an audio file.")


def get_whisper_model(settings: Settings) -> Any:
    loaded = app.state.whisper_model
    if loaded is not None:
        return loaded
    try:
        from faster_whisper import WhisperModel
    except ImportError as exc:
        raise RuntimeError("faster-whisper is not installed.") from exc

    model = WhisperModel(
        settings.whisper_model,
        device=settings.whisper_device,
        compute_type=settings.whisper_compute_type,
        download_root=settings.whisper_download_dir,
    )
    app.state.whisper_model = model
    return model


def build_codex_prompt(lines: list[LyricLine]) -> str:
    payload = [
        {"lyricId": line.lyricId, "japanese": line.japanese.strip()}
        for line in lines
    ]
    return (
        "你是 LearnMore 的日文歌曲繁體中文翻譯助手。\n"
        "請把每一行 japanese 翻成自然、白話、適合台灣使用者閱讀的繁體中文。\n"
        "歌詞中混用的英文、羅馬字或片假名外來語也要盡量翻成繁體中文；"
        "除非是歌手名、專輯名、品牌名、無法翻譯的擬聲詞，或保留原文會比中文更自然，否則不要留下英文單字或片語。\n"
        "保留原本 lyricId，不要新增、刪除、合併或拆分任何行。\n"
        "只輸出有效 JSON，不要 Markdown，不要註解，不要多餘文字。\n"
        '輸出格式必須是：{"translations":[{"lyricId":1,"chinese":"..."}]}\n'
        f"要翻譯的資料如下：\n{json.dumps(payload, ensure_ascii=False)}"
    )


def build_annotation_prompt(lines: list[LyricLine]) -> str:
    payload = [
        {"lyricId": line.lyricId, "japanese": line.japanese.strip()}
        for line in lines
    ]
    return (
        "你是 LearnMore 的日文歌詞假名與羅馬拼音標註助手。\n"
        "請替每一行 japanese 產生兩個欄位：\n"
        "1. japaneseRuby：可直接放入 HTML 的字串。只替漢字或需要讀音輔助的詞加 <ruby>原文<rt>ひらがな</rt></ruby>；"
        "平假名、片假名、英文字、數字、符號與空白請盡量保留原樣。不要使用 <rp>。\n"
        "2. roman：整行日文的 Hepburn 羅馬拼音。英文字原樣保留，詞之間用空白分隔。\n"
        "保留原本 lyricId，不要新增、刪除、合併或拆分任何行。\n"
        "只輸出有效 JSON，不要 Markdown，不要註解，不要多餘文字。\n"
        '輸出格式必須是：{"annotations":[{"lyricId":1,"japaneseRuby":"...","roman":"..."}]}\n'
        f"要處理的資料如下：\n{json.dumps(payload, ensure_ascii=False)}"
    )


def build_song_alias_prompt(songs: list[SongAliasCandidate]) -> str:
    payload = [
        {
            "songUid": song.songUid,
            "title": song.title.strip(),
            "artist": song.artist.strip(),
            "performer": song.performer.strip(),
            "youtubeUrl": song.youtubeUrl.strip(),
        }
        for song in songs
    ]
    return (
        "你是 LearnMore 日文歌曲搜尋關鍵字維護助手。\n"
        "請替每首歌曲產生繁體中文搜尋 alias，讓使用者可以用中文找到日文歌。\n"
        "規則：\n"
        "1. 只輸出有效 JSON，不要 Markdown、註解或多餘文字。\n"
        "2. 保留原本 songUid，不要新增、刪除、合併或拆分歌曲。\n"
        "3. 每首歌回傳 1 到 3 個 aliases，每個 aliasText 最多 255 字。\n"
        "4. aliasType 只能是 chinese_title 或 alternate_title。\n"
        "5. 如果有官方或常見繁中歌名，用 chinese_title。\n"
        "6. 如果沒有常見中文歌名，不要硬翻成誤導歌名；改用 alternate_title，例如「歌手中文名 歌名 中文歌詞」、"
        "「原歌名 中文歌詞」或使用者合理會搜尋的繁中詞。\n"
        "7. 每首歌至少要有一個 aliasText 含中文漢字，且必須使用繁體中文，不要簡體中文。\n"
        "輸出格式必須是："
        '{"songs":[{"songUid":"...","aliases":[{"aliasText":"...","aliasType":"chinese_title|alternate_title","note":"簡短原因"}]}]}\n'
        f"要處理的資料如下：\n{json.dumps(payload, ensure_ascii=False)}"
    )


def normalize_generated_aliases(
    codex_result: dict[str, Any],
    requested_song_uids: set[str],
) -> list[GeneratedSongAliasItem]:
    raw_items = codex_result.get("songs")
    if not isinstance(raw_items, list):
        raise HTTPException(
            status_code=status.HTTP_502_BAD_GATEWAY,
            detail="Codex response did not include a songs array.",
        )

    normalized: list[GeneratedSongAliasItem] = []
    seen_song_uids: set[str] = set()
    for raw_item in raw_items:
        if not isinstance(raw_item, dict):
            raise HTTPException(status_code=status.HTTP_502_BAD_GATEWAY, detail="Codex returned an invalid song item.")

        song_uid = str(raw_item.get("songUid") or raw_item.get("SongUid") or "").strip()
        if song_uid not in requested_song_uids or song_uid in seen_song_uids:
            raise HTTPException(status_code=status.HTTP_502_BAD_GATEWAY, detail="Codex returned an unexpected song UID.")
        seen_song_uids.add(song_uid)

        raw_aliases = raw_item.get("aliases")
        if not isinstance(raw_aliases, list) or not raw_aliases:
            raise HTTPException(status_code=status.HTTP_502_BAD_GATEWAY, detail="Codex returned a song without aliases.")

        aliases: list[GeneratedSongAlias] = []
        seen_aliases: set[str] = set()
        has_cjk = False
        for raw_alias in raw_aliases:
            if not isinstance(raw_alias, dict):
                raise HTTPException(status_code=status.HTTP_502_BAD_GATEWAY, detail="Codex returned an invalid alias.")

            alias_text = normalize_alias_text(raw_alias.get("aliasText") or raw_alias.get("AliasText"))
            if not alias_text:
                continue
            alias_key = alias_text.casefold()
            if alias_key in seen_aliases:
                continue

            alias_type = str(raw_alias.get("aliasType") or raw_alias.get("AliasType") or "alternate_title").strip()
            if alias_type not in ALLOWED_ALIAS_TYPES:
                raise HTTPException(status_code=status.HTTP_502_BAD_GATEWAY, detail="Codex returned an unsupported alias type.")

            if CJK_RE.search(alias_text):
                has_cjk = True
            seen_aliases.add(alias_key)
            aliases.append(
                GeneratedSongAlias(
                    aliasText=alias_text,
                    aliasType=alias_type,
                    note=str(raw_alias.get("note") or raw_alias.get("Note") or "").strip()[:160],
                )
            )

        if not aliases or not has_cjk:
            raise HTTPException(status_code=status.HTTP_502_BAD_GATEWAY, detail="Codex returned aliases without Traditional Chinese search text.")
        normalized.append(GeneratedSongAliasItem(songUid=song_uid, aliases=aliases[:3]))

    return normalized


def normalize_alias_text(value: Any) -> str:
    text = str(value or "").strip()
    text = re.sub(r"^[\"'「『《]+|[\"'」』》]+$", "", text).strip()
    text = re.sub(r"\s+", " ", text)
    return text[:255]


async def run_codex(prompt: str, settings: Settings) -> dict[str, Any]:
    output_path = make_output_path(settings.codex_workdir)
    args = [
        settings.codex_bin,
        "exec",
        "--skip-git-repo-check",
        "--ephemeral",
        "-s",
        "read-only",
        "-C",
        settings.codex_workdir,
        "-o",
        str(output_path),
    ]
    if settings.codex_model:
        args.extend(["-m", settings.codex_model])
    args.append("-")

    try:
        process = await asyncio.create_subprocess_exec(
            *args,
            stdin=asyncio.subprocess.PIPE,
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE,
        )
    except OSError as exc:
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE,
            detail="Codex command could not be started.",
        ) from exc

    try:
        stdout, stderr = await asyncio.wait_for(
            process.communicate(prompt.encode("utf-8")),
            timeout=settings.codex_timeout_seconds,
        )
    except asyncio.TimeoutError as exc:
        process.kill()
        await process.wait()
        raise HTTPException(
            status_code=status.HTTP_504_GATEWAY_TIMEOUT,
            detail="Codex translation timed out.",
        ) from exc

    stdout_text = stdout.decode("utf-8", errors="replace")
    stderr_text = stderr.decode("utf-8", errors="replace")
    if process.returncode != 0:
        raise HTTPException(
            status_code=status.HTTP_502_BAD_GATEWAY,
            detail=f"Codex command failed: {truncate(stderr_text or stdout_text)}",
        )

    raw_output = read_codex_output(output_path, stdout_text)
    try:
        return extract_json_object(raw_output)
    except ValueError as exc:
        raise HTTPException(
            status_code=status.HTTP_502_BAD_GATEWAY,
            detail="Codex response was not valid JSON.",
        ) from exc
    finally:
        output_path.unlink(missing_ok=True)


def make_output_path(workdir: str) -> Path:
    output_dir = Path(workdir)
    output_dir.mkdir(parents=True, exist_ok=True)
    handle = tempfile.NamedTemporaryFile(
        prefix="codex-output-",
        suffix=".json",
        dir=output_dir,
        delete=False,
    )
    handle.close()
    return Path(handle.name)


def read_codex_output(output_path: Path, stdout_text: str) -> str:
    if output_path.exists():
        content = output_path.read_text(encoding="utf-8").strip()
        if content:
            return content
    return stdout_text.strip()


def extract_json_object(raw_output: str) -> dict[str, Any]:
    decoder = json.JSONDecoder()
    text = raw_output.strip()
    for index, char in enumerate(text):
        if char != "{":
            continue
        try:
            value, _ = decoder.raw_decode(text[index:])
        except json.JSONDecodeError:
            continue
        if isinstance(value, dict):
            return value
    raise ValueError("No JSON object found.")


def truncate(value: str, limit: int = 400) -> str:
    text = " ".join(value.split())
    if len(text) <= limit:
        return text
    return f"{text[:limit]}..."
