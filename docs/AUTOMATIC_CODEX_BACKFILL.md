# 歌曲文字自動補件

上傳流程原本只把翻譯缺漏的歌曲標為 `translation_pending_codex`，需要另外匯出、翻譯及回填。
現在由 Windows 排程定時處理這個狀態，呼叫主機既有登入的 Codex CLI，補齊中文、缺漏的假名標註及羅馬拼音。
網站原有的字典轉換仍照常執行；已存在且格式有效的內容不會被自動覆寫。

## 執行流程

1. 每兩分鐘掃描一次，每次最多一首。排程忽略重複啟動，SQL session application lock 再防止不同工作同時取件。
   取件後先檢查日文原文；空歌詞或缺漏原文會在鎖定快照後轉為待校正，不呼叫 Codex、不消耗重試次數，也不會中斷後續掃描。補上原文後可重新排入文字補件。
2. 保存完整歌詞快照；資料庫連線設定只留在 PowerShell 記憶體，不傳給 Codex。
3. 每批最多 16 行，以固定 JSON Schema 要求 Codex 回覆。執行時關閉 shell、MCP、Apps、hooks、多代理及網頁搜尋，使用唯讀 sandbox 與暫時 session。
4. 驗證行號、欄位、假名 HTML、原文一致性及羅馬拼音。原文、秒數與歌詞順序沒有模型可寫入的欄位。
5. 交易內重新比對完整快照。若有人修改歌詞或狀態，放棄該次回填，不覆蓋新資料。
6. 更新後逐欄讀回；成功才提交，並留下收據。來源或讀音有疑義時保留待校正。

**文字補完不會直接標記 `high_accuracy_completed`。** 若原狀態明示 `timing_validation_pending`，且設定允許高精度隊列恢復，改為 `high_accuracy_pending`，交由既有背景工作校正秒數；其他情況保持待校正，避免把缺少音訊證據的歌曲當成完成。

## 安裝與啟用

需在可讀取網站設定檔的 Windows 主機執行，使用已登入 Codex 的帳戶。需要 Python 3.11 以上及支援 `exec --output-schema --ephemeral` 的 Codex CLI。本次排程身分探測使用 Codex 0.166.0 通過；舊版 CLI 的預設模型可能已不可用，登入成功仍須以實際探測確認。
可另行安裝專用 CLI，透過 `-CodexPath` 指定原生執行檔，避免影響主機其他工作。
不需要把 API 金鑰、Codex 登入檔或 SQL 密碼複製到儲存庫，也不更換網站執行檔。

先將以下三個檔案放在同一個目錄：

- `Install-LearnMoreCodexBackfill.ps1`
- `Invoke-LearnMoreCodexBackfill.ps1`
- `learnmore_codex_backfill.py`

```powershell
.\Install-LearnMoreCodexBackfill.ps1 -Mode Probe
.\Install-LearnMoreCodexBackfill.ps1 -Mode Status
```

`Probe` 以實際排程身分執行一行合成日文的 Codex 補件，不連線、不寫入歌曲資料庫。
主機會建立受限權限的工作目錄，使用目前帳戶的 S4U 排程，不儲存帳戶密碼。
若探測顯示 `codex_auth_or_quota`，請以排程帳戶重新登入 Codex，或確認可用額度，再重新執行 `Probe`。`codex login status` 顯示已登入，仍不代表實際請求能通過驗證；必須取得本次探測成功收據。
確認探測已成功後啟用：

```powershell
.\Install-LearnMoreCodexBackfill.ps1 -Mode Enable
```

啟用會比對兩支執行檔與探測時的 SHA-256，檔案變動後必須重新探測。
可用 `-InstallRoot`、`-AppSettingsPath`、`-PythonPath`、`-CodexPath` 指定既有路徑；`-SqlDataSource` 只在需要明確指定 SQL TCP 位址時使用。
網站連線字串不出現在命令列。SQL catalog 預設限制為既有的 `language`。

## 重試與觀察

- 單一 Codex 批次最多 240 秒，逾時終止該子程序樹。
- 同一份歌詞資料最多嘗試三次，間隔依序為 5、15、45 分鐘。
- 登入或額度問題會讓整個工作冷卻 30 分鐘，避免連續呼叫。
- 三次失敗轉為待校正；歌詞內容變動會產生新的快照指紋，可以重新嘗試。
- 異常診斷只記固定錯誤代碼，不記 CLI stderr、連線字串或完整例外訊息。

工作目錄預設為 `D:\Data\LearnMoreCodexBackfill`：

| 路徑 | 用途 |
|---|---|
| `state/status.json` | 最近一次掃描／處理結果 |
| `state/events.jsonl` | 固定代碼事件紀錄 |
| `state/<歌曲 ID>/retry.json` | 快照指紋、重試次數與下次時間 |
| `state/<歌曲 ID>/<工作 ID>/before.json` | 回填前備份 |
| `state/<歌曲 ID>/<工作 ID>/receipt.json` | 已提交的更新收據 |

要暫停自動補件，可停用 `LearnMore-CodexBackfill` 排程。備份不會自動刪除。
Codex 仍可能遇到原文污染或特殊讀法；這些情況會留下待校正，不以猜測通過。

## 驗證

```bash
python3 -m unittest discover -s scripts -p test_learnmore_codex_backfill.py -v
```

隔離 SQL 整合測試另以 `LEARNMORE_CODEX_SQL_TEST_CONFIG` 指向測試容器設定檔；設定包含 `port` 與一次性 `password`，只能使用本機容器。
測試自行建立及刪除隨機名稱的測試 catalog，不使用正式歌曲。

```bash
python3 -m unittest discover -s scripts -p test_learnmore_codex_backfill_sql.py -v
```

Codex 非互動執行、唯讀權限、結構化輸出與既有登入的行為依 [OpenAI 官方文件](https://learn.chatgpt.com/docs/non-interactive-mode)。
