# AI 主機的 Qwen3-ASR

這是獨立的離線命令列工具，使用 Qwen3-ASR-1.7B 與
Qwen3-ForcedAligner-0.6B。它不提供公開 HTTP 服務、不讀網站憑證，
也不修改 LearnMore 的歌詞、秒數或校正狀態。
辨識結果仍須核對；部署成功不代表歌曲已通過驗收。

## 建置與下載

在 GB10 主機使用已驗證 CUDA 可用的 LearnMore API 基底映像，
先核對映像 ID，再建立獨立標籤固定基底版本。BuildKit 的 `FROM`
需要映像名稱，不能直接把 `sha256:...` 映像 ID 當作名稱。
建置程序會保留原有 torch、torchaudio、
numpy 與 transformers 版本，避免替換 GB10 的 CUDA 套件。

```sh
qwen_base_image_id=$(docker inspect --format '{{.Image}}' learnmore-api-gpu)
qwen_base_tag="learnmore-qwen-base:${qwen_base_image_id#sha256:}"
docker image tag "$qwen_base_image_id" "$qwen_base_tag"
docker build --pull=false --build-arg BASE_IMAGE="$qwen_base_tag" \
  -t learnmore-qwen-asr:local LearnMoreAPI/qwen-asr
docker volume create learnmore-qwen-models
docker volume create learnmore-qwen-work
docker run --rm --name learnmore-qwen-download \
  --memory 8g --cpus 4 \
  -v learnmore-qwen-models:/models \
  learnmore-qwen-asr:local download
```

下載只使用公開模型，不需要 Hugging Face 登入或 API 金鑰。
模型版本固定在程式中的 revision；模型資料存於獨立 Docker volume，
不可登記為六小時後刪除的暫存目錄。

## 啟動與使用

```sh
docker run -d --name learnmore-qwen-asr --restart unless-stopped \
  --gpus all --network none --memory 16g --cpus 4 --shm-size 1g \
  --cap-drop ALL --security-opt no-new-privileges \
  -v learnmore-qwen-models:/models:ro \
  -v learnmore-qwen-work:/work \
  --entrypoint sleep learnmore-qwen-asr:local infinity
docker exec learnmore-qwen-asr python /opt/qwen-asr/qwen3_asr_cli.py doctor
docker cp input.wav learnmore-qwen-asr:/work/input.wav
docker exec learnmore-qwen-asr python /opt/qwen-asr/qwen3_asr_cli.py \
  transcribe /work/input.wav --language Japanese --output /work/result.json
docker cp learnmore-qwen-asr:/work/result.json ./result.json
```

音檔需能由容器的 UID 1000 讀取；輸出檔不可已存在。
一次可指定多個音檔，共用一次模型載入，推論批次固定為 1。
未執行推論時，容器不會預載模型占用 GPU。
推論容器沒有網路，直接使用固定版本的唯讀模型快取，
因此不受雲端匿名 GPU 額度影響。

停止並移除容器不會刪除模型與轉寫紀錄：

```sh
docker stop learnmore-qwen-asr
docker rm learnmore-qwen-asr
```

目前未接替正式網站的 Demucs／Kotoba／WhisperX 流程。
JSON 中的 `verificationStatus: unreviewed` 表示待核對的原始轉寫，
不能改成已完成或直接匯入正式歌詞。

新版 CLI 同時記錄音檔 SHA256，並在推論前後核對音檔未變動。
整首歌曲可使用 `--max-new-tokens 2048`，避免短片段預設的 512 token
上限截斷較長的轉寫。API 選用的補充證據模式需要這一版 CLI；
請參考 [API 設定](../README.md#選用離線-qwen-補充證據)。
