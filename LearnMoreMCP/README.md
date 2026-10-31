# LearnMore 音樂 MCP

將已建檔日文歌曲交給 Demucs、Kotoba-Whisper 與 WhisperX 處理，透過網站原有驗證器產生逐行報告。MCP 使用標準 STDIO 協定，適用支援本機 MCP 程序的 AI 客戶端；不另開公開 MCP 連接埠。

## 安裝

在此目錄執行 `python -m pip install .`，或用獨立環境 `uv tool install .`。
將儲存庫的 `skills/learnmore-music-alignment` 複製到 AI 客戶端支援的 Skill 目錄。
Codex 可放在 `~/.codex/skills/learnmore-music-alignment`；其他客戶端不支援 Skill 時，可將 `SKILL.md` 當作該機器人的操作說明。

建立僅自己可讀的私有設定檔，例如 `~/.config/learnmore-music-mcp/config.json`：

```json
{
  "base_url": "https://example.com/LearnMore",
  "read_token": "由管理者安全提供的專用處理憑證"
}
```

需要套用權限時，由管理者另外配置 `write_token`。兩種憑證必須不同，至少 32 字元；不要使用 GPU API 的推論憑證。Linux/macOS 設定檔權限用 `chmod 600`，Windows 以 ACL 限制讀取。不要將私有設定檔加入儲存庫或貼進對話。

AI 客戶端的 MCP 設定範例（只有程式及私有設定路徑，沒有密鑰）：

```json
{
  "mcpServers": {
    "learnmore-music": {
      "command": "learnmore-music-mcp",
      "env": { "LEARNMORE_MUSIC_CONFIG": "/private/path/config.json" }
    }
  }
}
```

若客戶端的 PATH 找不到該命令，改填安裝環境中執行檔的絕對路徑。
新對話須重新載入 MCP；此設定不會自動安裝到其他電腦或其他 AI 帳號。

## 工具與狀態

| 工具 | 功能 |
| --- | --- |
| `start_music_alignment` | 指定 `song_uid` 或已建檔的 `youtube_url`，加上穩定的 `request_id`，建立分析工作 |
| `get_music_job` | 查詢工作；處理中依 `nextPollAfterSeconds` 等待 |
| `get_alignment_report` | 取得逐行原秒數、聲學時間、來源、分數及網站接受結果 |
| `preview_alignment_changes` | 檢閱通過後的變更與 `previewHash` |
| `apply_verified_alignment` | 用 `preview_hash` 套用；只在配置獨立套用憑證時提供 |

`queued → processing → verified / needs_review / failed`；只有套用完成才是 `applied`。
`verified` 不會自動回寫歌曲。程序中斷後租約到期會變成 `interrupted`，不會自動重跑推論。
相同 `request_id` 重試會回傳原工作；不同 ID 的同一歌曲、相同快照若已有排隊、執行中或通過的工作，也會共用原工作。
如果請求逾時或回應遺失，先查原 `job_id`，不要建立新 ID 重送。

只分析現有歌曲與正式歌詞；陌生影片會回覆 `upload_song_first`，不會自行將辨識稿建檔。
本版不接受本機音檔上傳，不處理翻譯補件；這些仍使用既有網站流程。

## 網站端啟用

更新含此功能的網站程式後，在網站私有設定中配置 `MusicAutomation`：

```json
{
  "MusicAutomation": {
    "Enabled": true,
    "ReadToken": "專用處理憑證",
    "WriteToken": "另外產生的套用憑證"
  }
}
```

不設定 `Enabled` 或缺少處理憑證時，工具 API 拒絕存取，背景佇列也不執行。
此服務是管理者的自動化介面，不是一般會員權限；憑證持有人可處理本站歌曲，不應分享給未授權的機器人。可只發放處理憑證，讓它只能分析及預覽；套用由管理者執行。

首次啟用會以內嵌的 `docs/sql/005-music-automation.sql` 冪等建立三個工作、請求對應及備份資料表，不修改既有歌曲表結構。也可由資料庫管理者先執行相同 migration，再以較低權限執行網站。
工作與備份含原歌詞及翻譯，依網站資料權限保護與備份。此版不自動刪除稽核紀錄。

網站必須已配置 GPU 高精度 API。逐行報告沿用 `RemoteHighAccuracyAlignmentClient` 與完整歌詞投影門檻，再檢查來源、歌詞 ID、原文、音訊範圍與遞增秒數。API 自報命中數不作為套用依據。
套用以 SQL 交易保存備份、鎖定並核對快照，只更新秒數與校正狀態，完整讀回一致才提交；同一工作重複套用只回傳原收據。收藏與管理權限表不會寫入。

可直接請 AI：

> 按照 learnmore-music-alignment 流程分析這個已建檔影片，先給我逐行驗證報告與秒數差異。

> 將剛才通過驗證的這首歌套用，保留翻譯、讀音與羅馬拼音。

Codex CLI 也可使用：

```sh
codex mcp add learnmore-music --env LEARNMORE_MUSIC_CONFIG=/private/path/config.json -- /absolute/path/learnmore-music-mcp
```

語法依據 [OpenAI Docs：MCP 設定](https://learn.chatgpt.com/docs/extend/mcp?surface=cli)。這只註冊本機客戶端，不會把工具安裝到其他裝置或帳號。
