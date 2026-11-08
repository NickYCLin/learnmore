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

## GB10 的 CTranslate2 CUDA 執行環境

PyTorch 能使用 GPU，不代表 faster-whisper 使用的 CTranslate2 也支援 CUDA。
若 CTranslate2 回報 `This CTranslate2 package was not compiled with CUDA support`，
不能只把 `WHISPER_DEVICE` 改成 `cuda`，也不能停用高精度配置檢查。

`Dockerfile.ctranslate2-cuda` 可在既有 API 映像上補建 CUDA 版 CTranslate2，
保留該映像中的 API 程式及其他套件。來源固定為 CTranslate2 4.8.2 的完整提交，
預設針對 GB10 的 compute capability 12.1；須在 ARM64 主機建置，
`API_IMAGE` 應指定已核實 digest 的映像來源，或為現行映像建立專用本機標籤
並核對標籤的映像 ID，不使用可變的 `latest` 或裸 `sha256:` 映像 ID。
建置階段也修正 Ubuntu CMake 舊 FindCUDA 模組只接受單位數架構的解析式，
讓 `12.1` 能產生對應的 NVCC 架構旗標；不改動 CTranslate2 或網站的驗證規則。

```sh
docker build --build-arg API_IMAGE=learnmore-api:verified-base \
  -f LearnMoreAPI/Dockerfile.ctranslate2-cuda \
  -t learnmore-api:cuda-candidate LearnMoreAPI
docker run --rm --device nvidia.com/gpu=all --entrypoint python \
  learnmore-api:cuda-candidate -c \
  'import ctranslate2; assert "float16" in ctranslate2.get_supported_compute_types("cuda")'
```

建置不會改動執行中的服務，也不會自動切換模型或網站位址。部署前仍須隔離驗證
Kotoba 模型載入、WhisperX 日文對齊、Demucs 與既有 API 契約，保留回復映像及
完整設定；不得複製或輸出私有環境變數。正式配置須通過 capabilities 檢查，
歌曲則仍經 report、preview/hash、apply 與讀回驗證，不能以建置或 health 成功代替。

## 選用離線 Qwen 補充證據

安裝 [Qwen 容器工具](qwen-asr/README.md) 後，可在同時包含最新 API 程式
與 Qwen 套件的執行環境設定：

```text
QWEN_ASR_COMMAND_JSON=["python","/opt/qwen-asr/qwen3_asr_cli.py"]
QWEN_ASR_TIMEOUT_SECONDS=600
```

可用已建置的 Qwen 工具映像組合最新 API 程式；這個映像仍須明確設定
上述選項才會啟用補充辨識，不會自行接替既有服務：

```sh
docker build --pull=false --build-arg QWEN_IMAGE=learnmore-qwen-asr:local \
  -f LearnMoreAPI/qwen-asr/Dockerfile.api -t learnmore-api:qwen LearnMoreAPI
```

API 容器需掛載唯讀模型快取到 `/models`，並保留原有私有設定與模型快取。
`/health` 的 `qwenSupplementalConfigured` 僅回報是否設定補充命令；
不會回傳命令路徑、憑證，也不代表歌曲已驗收通過。

未設定時沿用原流程。已全部通過的歌曲不執行補充辨識；有未通過行時，
才將同一份 Demucs 人聲交給 Qwen，不提供正式歌詞或候選答案作提示。
全曲辨識沿用原請求語言；仍未通過的 Latin-dominant 行，再擷取相鄰可靠行
之間、現有起點前 3 秒至後 12 秒的人聲片段。純英文片段指定 English，
英日混合片段使用 auto；歌詞只用於選取片段與比對，不傳給辨識模型。
全曲與局部辨識共用逾時預算。全曲報告無效不會阻止剩餘預算內的局部辨識。
每個片段各自驗證音檔 SHA256、模型版本、完整逐字文字與非零時間範圍，
再加回實際取樣起點；不同片段的字詞不得拼接。錯誤或歧義保留待校正，
不放寬網站既有的真實音訊錨點規則，也不保證兩首歌曲已可全數通過。
人聲以無重疊的 30 秒 PCM 片段辨識，時間戳加回片段實際起點；
每段先檢查原始時間戳範圍及順序，無效片段不會截斷或排序成有效證據。
Qwen 與逐字對齊模型須預先下載，推論子程序禁止連網補檔，
也不繼承網站憑證。獨立容器的命令不能直接從另一個容器呼叫；
這不是 Docker socket 或遠端 shell 的授權介面。

只接受固定模型版本、原始音檔 SHA256 一致、轉寫文字完整對應時間戳，
且整行文字逐字吻合、重複句無歧義、時間不跨越相鄰行的證據。
`qwen3_asr_word_match` 的分數是文字吻合度，不是聲學信心機率；
網站沿用逐字來源的 0.70 門檻與整首交易驗證，不覆蓋原已通過的行。
補充辨識失敗時，模型標記包含 `qwen3-asr-unavailable`，保留原結果與待校正狀態。
CLI 的原始 `unreviewed` 轉寫不能直接當作完成或正式歌詞。
分塊與服務端均拒絕起訖相同的零長度字詞，不會補猜結束時間。
續接時會重新驗證已完成片段；舊日誌若含無效時間，該次只改記失敗並中止，
不自動重新推論。後續明確續接才重試失敗片段，保留原始結果與有效鄰段。

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
