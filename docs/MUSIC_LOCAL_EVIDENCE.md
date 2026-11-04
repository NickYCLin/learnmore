# 本機音訊證據與服務端驗證

## 分塊與診斷

Qwen CLI 的 PCM 分塊將不足一秒的最後片段併入前一塊，使用音框邊界，
不裁切、不補零。每塊原始結果保存在輸出檔旁的 `.chunks.json`，不是驗收結果。
`--resume-chunks` 僅在輸入與分塊 SHA256、固定模型版本、語言及 token 上限全部
相同時重用成功塊；失敗塊重新辨識，舊失敗結果保留於 attempts。
同一 journal 的 `.lock` 存在時拒絕並發；程序意外中斷留下鎖時須由操作者先確認
程序已終止再處理，不能自動推定過期。任一塊失敗仍繼續保存其他獨立塊結果，
但不產生冒充完整成功的最終輸出。零長詞仍不能通過服務端逐詞驗證。

API 子程序紀錄存於 `QWEN_DIAGNOSTICS_DIR`（預設
`/tmp/learnmore-qwen-diagnostics`），每次使用私人子目錄保存原始輸出、分塊 journal
及 `diagnostic.json`。只記安全錯誤類型與退出碼，不保存命令、環境或 stderr。
正式環境應將診斷目錄掛載持久磁碟，並限制操作帳號及安排保存期限。

## 可信證據契約 v1

這不是任意音檔或客戶端報告上傳 API。處理憑證不能自行建立信任。
`LOCAL_EVIDENCE_ROOT` 必須是服務端受控的絕對目錄，容器唯讀掛載。
根目錄包含 `registry.json`，其內容為 evidenceId（32 個小寫十六進位字元）
到 manifest 原始檔 SHA256（64 個小寫十六進位字元）的對應；不可群組／公開寫入。
未設定、未登記、路徑連結、檔案或快照變動一律拒絕。不提供 HTTP 登記入口。

受信任操作者必須先確認執行環境、精確 YouTube 來源、原始音訊取得紀錄、人聲分離
來源與固定模型的盲辨識過程，再透過既有檔案管理權限放入不可變 bundle 並登記
manifest SHA。僅有客戶端自報的雜湊、模型名稱、accepted 或分數都不能完成這步。
目前七首本機候選不因本功能部署自動取得信任；來源審核與登記仍須完成。
登記者本身是信任根，若無合法受信任操作者或可信執行證明，應停在未登記，不能
改成接受任意客戶端產物。

每個 `<root>/<evidenceId>/manifest.json`：

```json
{
  "version": 1,
  "evidenceId": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
  "snapshotHash": "網站 evidence-snapshot 回傳的完整 SHA256",
  "songUid": "網站快照的精確 SongUid",
  "youtubeUrl": "網站快照的完整來源網址，不自行正規化",
  "language": "en",
  "lyrics": [{"lyricId": 1, "japanese": "Stand by", "currentStart": 2.1}],
  "source": {"file": "source.wav", "sha256": "原始 PCM WAV 雜湊"},
  "vocals": {"file": "vocals.wav", "sha256": "人聲 PCM WAV 雜湊"},
  "raw": {"file": "raw.json", "sha256": "固定模型完整逐詞原始輸出雜湊"}
}
```

lyrics 必須是快照全部行，順序、ID、原文與原秒數精確相同（最多 200 行，沿既有
高精度 API 上限）。snapshotHash 綁定網站完整五欄歌詞、來源及歌曲狀態；
不由客戶端選擇排除中文、假名或羅馬字。音訊不接受裁短版本，原音與人聲長度差
不得超過 0.05 秒。raw 沿既有固定 Qwen 模型／revision、CUDA backend、
unreviewed 及完整逐詞契約重驗；零長詞、越界、缺詞、重複段歧義仍拒絕。
en 必須有 English ASR 證據；ja 保留日英混合辨識。逐行結果由服務端從逐詞證據
重建，不讀取 manifest 的自報 alignedLines、accepted 或 matchedCount。

## 入口與交付界線

- MCP `get_local_evidence_snapshot(song_uid)`：讀正式完整快照及 snapshotHash。
- MCP `revalidate_local_alignment_evidence(request_id, song_uid, evidence_id, snapshot_hash, language)`：
  只重驗已登記證據，不執行 ASR、不套用歌曲。沿專用處理憑證；套用仍需獨立憑證。
- 網站 GET `/api/music-automation/songs/{uid}/evidence-snapshot`。
- 網站 POST `/api/music-automation/local-evidence/jobs`，只接受上述五個識別欄位。
- API POST `/v1/local-alignment-evidence/verify`，僅供網站向受信任證據服務重驗。

匯入交易重新鎖定並比對完整快照；同 request 必須同來源／語言／證據，否則拒絕；
同歌已有 queued/processing/verified 工作時拒絕其他匯入。新工作直接保存
verified/needs_review/failed，不進推論佇列、不改 Songs 或歌詞表。
原 report → previewHash → 明確授權 apply → 交易備份及正式讀回流程不變。
未通過所有行不會提供可套用秒數。部署測試不等於歌曲已校正。

## 執行模型不可靜默降級

受保護 GET `/v1/alignment-capabilities` 回報實際 model/device/computeType、
WhisperX 開關、證據契約與是否符合日文預期 profile；不載入模型或執行推論。
`HIGH_ACCURACY_REQUIRED_PROFILE=kotoba-whisper-cuda-whisperx` 時，不符合的
`/v1/high-accuracy-align` 在推論前回 503 `high_accuracy_profile_mismatch`，
不能以 medium/CPU/WhisperX 關閉冒充預期管線。未設定時保留既有相容行為。
此 profile 檢查是配置核對，不聲稱模型已載入或 GPU 推論成功。

GB10 既有 compose 明記 aarch64 CTranslate2 wheel 是 CPU-only、Whisper 工作應指向
原 x86 GPU 服務；不能只把 WHISPER_DEVICE 改 cuda 來製造假修復。
可信本機 Qwen 證據重驗不依賴這個 primary profile，但仍須逐行通過原驗證器。
要恢復自動主管線，須先有合法可用且實測符合 profile 的服務，再修改正式路由；
本次不啟動真歌曲來測試。
