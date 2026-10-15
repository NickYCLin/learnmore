# LearnMore API

Small API service for LearnMore cross-host jobs.

This service uses the local Codex CLI as the translation backend. It does not call the OpenAI API with an `OPENAI_API_KEY`.

Public base path:

```text
https://example.com/LearnMoreAPI
```

Runtime settings:

```text
LEARNMORE_API_TOKEN=<shared bearer token>
CODEX_BIN=codex
CODEX_MODEL=
CODEX_TIMEOUT_SECONDS=180
CODEX_WORKDIR=/tmp/learnmore-api-codex
WHISPER_MODEL=medium
WHISPER_DEVICE=cuda
WHISPER_COMPUTE_TYPE=float16
WHISPER_DOWNLOAD_DIR=/tmp/learnmore-api-whisper
WHISPER_TIMEOUT_SECONDS=1800
WHISPERX_ALIGN_ENABLED=false
YTDLP_BIN=yt-dlp
SHAZAM_TIMEOUT_SECONDS=30
UVR_COMMAND_TEMPLATE='demucs --mp3 --two-stems vocals -n htdemucs -o {output_dir} {input}'
UVR_OUTPUT_DIR=/tmp/learnmore-api-uvr
UVR_TIMEOUT_SECONDS=1800
HIGH_ACCURACY_USE_VOCALS_STEM=true
```

High-accuracy lyric alignment uses the vocals stem plus WhisperX forced
alignment against the submitted formal lyric lines when `WHISPERX_ALIGN_ENABLED`
is true, then refines line starts with a vocals-only ASR word anchor pass. Set
`WHISPERX_ALIGN_ENABLED=false` when the WhisperX alignment model is not already
cached; the API will use vocals-stem faster-whisper word timestamps instead of
blocking on model download. A successful WhisperX response model is reported as
`<WHISPER_MODEL>+vocals-stem+whisperx-lyrics+asr-refined+high-accuracy-align`.

Endpoints:

```text
GET  /LearnMoreAPI/health
POST /LearnMoreAPI/v1/translate-lines
POST /LearnMoreAPI/v1/annotate-lines
POST /LearnMoreAPI/v1/generate-song-aliases
POST /LearnMoreAPI/v1/transcribe-youtube
POST /LearnMoreAPI/v1/separate-youtube
POST /LearnMoreAPI/v1/shazam-lyrics
POST /LearnMoreAPI/v1/transcribe-align-shazam
```

`POST /v1/translate-lines` requires:

```text
Authorization: Bearer <LEARNMORE_API_TOKEN>
```

Request:

```json
{
  "songUid": "optional-source-id",
  "lines": [
    { "lyricId": 1, "japanese": "日文歌詞" }
  ]
}
```

Response:

```json
{
  "songUid": "optional-source-id",
  "translations": [
    { "lyricId": 1, "japanese": "日文歌詞", "chinese": "繁體中文翻譯" }
  ]
}
```

`POST /v1/annotate-lines` uses the same request shape and returns ruby HTML plus romaji:

```json
{
  "songUid": "optional-source-id",
  "annotations": [
    {
      "lyricId": 1,
      "japanese": "日文歌詞",
      "japaneseRuby": "<ruby>日文<rt>にほん</rt></ruby>歌詞",
      "roman": "nihon kashi"
    }
  ]
}
```

`POST /v1/generate-song-aliases` generates Traditional Chinese search aliases
for `dbo.SongAliases`. It is intended for background/operator backfills, not the
LearnMore upload request path.

Request:

```json
{
  "songs": [
    {
      "songUid": "song-1",
      "title": "日文歌名",
      "artist": "歌手",
      "performer": "原唱",
      "youtubeUrl": "https://www.youtube.com/watch?v=..."
    }
  ]
}
```

Response:

```json
{
  "songs": [
    {
      "songUid": "song-1",
      "aliases": [
        {
          "aliasText": "繁體中文搜尋關鍵字",
          "aliasType": "chinese_title",
          "note": "常見中文歌名"
        }
      ]
    }
  ]
}
```

`POST /v1/transcribe-youtube` downloads YouTube audio locally and runs local
Whisper through `faster-whisper`. It does not call OpenAI APIs.

Request:

```json
{
  "songUid": "optional-source-id",
  "youtubeUrl": "https://www.youtube.com/watch?v=...",
  "language": "ja"
}
```

`POST /v1/separate-youtube` downloads YouTube audio locally and runs the
configured UVR-compatible command. `UVR_COMMAND_TEMPLATE` must include the
`{input}` and `{output_dir}` placeholders; `{song_uid}` and `{model}` are also
available. The endpoint is intended for operator/batch jobs, not direct UI
requests.

Request:

```json
{
  "songUid": "optional-source-id",
  "youtubeUrl": "https://www.youtube.com/watch?v=...",
  "model": "UVR-MDX-NET-Inst_HQ_3"
}
```

Response:

```json
{
  "songUid": "optional-source-id",
  "youtubeUrl": "https://www.youtube.com/watch?v=...",
  "model": "UVR-MDX-NET-Inst_HQ_3",
  "outputDir": "/tmp/learnmore-api-uvr/optional-source-id",
  "stems": [
    { "kind": "vocals", "path": "...", "fileName": "song_(Vocals).wav", "sizeBytes": 123 },
    { "kind": "instrumental", "path": "...", "fileName": "song_(Instrumental).wav", "sizeBytes": 456 }
  ]
}
```

`POST /v1/shazam-lyrics` fetches formal lyrics from Shazam page metadata:

```json
{
  "shazamUrl": "https://www.shazam.com/zh-tw/song/1838374870/belt-of-venus"
}
```

`POST /v1/transcribe-align-shazam` runs local Whisper and aligns Shazam formal
lyric lines to the Whisper time anchors:

```json
{
  "songUid": "optional-source-id",
  "youtubeUrl": "https://www.youtube.com/watch?v=...",
  "shazamUrl": "https://www.shazam.com/zh-tw/song/1838374870/belt-of-venus",
  "language": "ja"
}
```

Response:

```json
{
  "songUid": "optional-source-id",
  "youtubeUrl": "https://www.youtube.com/watch?v=...",
  "language": "ja",
  "model": "medium",
  "durationSeconds": 240.0,
  "segments": [
    { "index": 1, "start": 0.0, "end": 3.2, "text": "日文辨識結果" }
  ]
}
```
