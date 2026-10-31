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
WHISPER_MODEL=kotoba-tech/kotoba-whisper-v2.0-faster
WHISPER_DEVICE=cuda
WHISPER_COMPUTE_TYPE=float16
WHISPER_DOWNLOAD_DIR=/tmp/learnmore-api-whisper
WHISPER_TIMEOUT_SECONDS=1800
WHISPERX_ALIGN_ENABLED=true
YTDLP_BIN=yt-dlp
SHAZAM_TIMEOUT_SECONDS=30
UVR_COMMAND_TEMPLATE='python -m learnmore_api.demucs_pcm --two-stems vocals -n htdemucs --jobs 0 --shifts 0 -o {output_dir} {input}'
UVR_OUTPUT_DIR=/tmp/learnmore-api-uvr
UVR_TIMEOUT_SECONDS=1800
HIGH_ACCURACY_USE_VOCALS_STEM=true
```

日文高精度流程預設採用 Demucs `htdemucs` 分離的無損 PCM WAV 人聲，
再以 `kotoba-tech/kotoba-whisper-v2.0-faster` 辨識文字，最後交由 WhisperX
產生 ASR 字詞時間並對齊正式歌詞，保留原有網站逐行驗證門檻。
Kotoba 使用 15 秒辨識片段，關閉 faster-whisper 的 `word_timestamps`，
避免蒸餾模型不相容的時間戳頭造成原生程序崩潰。

`WHISPERX_ALIGN_ENABLED=true` 是 Kotoba 流程的必要條件；WhisperX 失敗時
回報失敗，保留歌曲待校正狀態，不改用不相容的時間戳頭或宣告完成。
既有明確設定 `WHISPER_MODEL=medium` 的服務仍可沿用舊流程。
Docker image 內建 NLTK `punkt_tab`；部署時須預熱 Kotoba、日文 WhisperX
及 Demucs 模型快取，確認 GPU 可用後再切換網站高精度 API 位址。
若有 compose 的 `environment` 區塊，須一併更新其中模型、WhisperX 開關
與 `UVR_COMMAND_TEMPLATE`，否則會蓋過 `.env`。

成功回應的模型名稱包含
`<WHISPER_MODEL>+vocals-stem+whisperx-lyrics+asr-refined+high-accuracy-align`。
`/health` 回報設定的模型、裝置、WhisperX 開關與部署提交編號；
健康檢查不代表模型已載入或所有歌曲皆可命中，仍須以實際音訊請求驗收。

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
