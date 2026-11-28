# 翻譯補件與歌曲公開

新歌在翻譯來源比對期間以 `IsPublished=0`、`translation_source_pending` 儲存。比對完成後，網站在同一筆交易內寫入歌詞與公開狀態。每一行都需要有原文及有效中文。空白、`翻譯中...`、`翻譯中…`、`待翻譯`、`翻譯待補` 都視為未完成，歌曲會留在管理頁並轉入 `translation_pending_codex`。中斷的上傳會在離開請求時排入補件。若程序意外停止，worker 會接手超過兩小時且仍停在來源比對的草稿。

Linux worker 用容器內的 Codex CLI 執行既有 Python 驗證流程。它每輪最多處理一首，透過 SQL Server 的 `LearnMore.CodexBackfill` session lock 互斥。寫入前重新比對歌曲與歌詞快照，確認期間沒有其他人修改。只補缺漏或不合規的欄位，原文、秒數、行號、順序與已有效的中文都保留。逐行驗證與資料庫讀回均通過才公開草稿。舊歌維持原公開狀態。秒數依既有流程進入背景校正或人工待校正。

重試狀態保存在 `codex-worker-state` volume，請保留該 volume 並只執行一個 worker。每份歌詞最多三次，分別記錄 5、15、45 分鐘的下次時間。前兩次失敗維持翻譯待補，第三次明確失敗轉人工待校正，仍保留原公開狀態。若第三次執行意外中斷，45 分鐘後才轉待校正。登入或額度錯誤暫停整批 30 分鐘。管理者修正待校正草稿的原文或缺漏欄位並儲存後，worker 可針對新內容重新補件。

## 上線順序

1. 在目標資料庫確認目前備有 `dbo.Songs`，以有 DDL 權限的維護帳號執行 [009-song-publication.sql](sql/009-song-publication.sql)。使用 `sqlcmd -b -i docs/sql/009-song-publication.sql` 時，資料庫、驗證方式與連線目標由部署環境提供。確認 `IsPublished` 是 `bit NOT NULL DEFAULT (1)`，既有歌曲皆視為已公開。若原庫尚無高精度狀態欄位，這份 SQL 也會補齊。網站與 worker 都應在 SQL 成功後再更新。
2. 從本儲存庫用既有部署環境執行 Compose。以下的 `lm` 是縮寫，`LEARNMORE_DEPLOY_ENV` 指向部署環境原有的設定檔。需提供 `LEARNMORE_API_TOKEN` 與 `CODEX_WORKER_DB_USER`、`CODEX_WORKER_DB_PASSWORD`。worker 的 DB 帳號需要讀取歌曲及其動態歌詞表，並能更新補件欄位、狀態與公開位元。
3. 若同一資料庫有舊的 Windows 補件排程，先停止該排程。Linux worker 負責新草稿驗證與公開。回滾網站前也應先暫停公開入口或確認所有草稿均已妥善處理，舊網站版本沒有公開狀態過濾。

```bash
lm() {
  docker compose --env-file "$LEARNMORE_DEPLOY_ENV" -f deploy/learnmore/docker-compose.yml "$@"
}

# Web
lm --profile web up -d --no-deps --build web

# API 與 worker 映像。codex-base 是 worker 唯一的共用建置相依。
lm --profile codex-api --profile codex-worker build codex-api codex-worker
lm --profile codex-api run --rm --no-deps codex-api codex --version
lm --profile codex-worker run --rm --no-deps codex-worker codex --version
```

API 與 worker 各有獨立登入 volume。用各自的部署身分完成 Codex CLI 登入，由 CLI 將登入狀態保存在該 volume。容器以非 root 使用者執行。可使用容器中的 `codex login --device-auth`，並用 `codex login status` 確認狀態。更換憑證時重新登入。請勿掛載主機的 Codex 可執行檔或 `node_modules`，Codex 架構套件由映像安裝，Linux x64 會取得 `@openai/codex-linux-x64`。

```bash
lm --profile codex-api run --rm --no-deps codex-api codex login --device-auth
lm --profile codex-worker run --rm --no-deps codex-worker codex login --device-auth
lm --profile codex-api up -d codex-api
lm --profile codex-worker up -d --no-deps codex-worker
lm --profile codex-worker logs --tail=30 codex-worker
```

`codex-api` 使用獨立的服務名稱，預設只在主機 `127.0.0.1:18091` 提供服務。現有高精度服務連線設定沿用原值。確認 `GET /health` 回應後，用已設定的 bearer token 對 `POST /v1/translate-lines` 傳送一行合成資料（例如 `{"lines":[{"lyricId":1,"japanese":"テスト"}]}`）。應回 200 且保留相同的 `lyricId`。此 API 不連資料庫。登入狀態檢查只驗證 CLI 登入，實際請求才能驗證模型與額度。worker 每輪會記錄固定結果代碼，空佇列為 `idle`，成功提交為 `applied`，登入或額度問題為 `auth_or_quota` 或 `cooldown`。worker 直接使用 CLI，因此 API 與 worker 可以各自啟動。

## 本機測試

```bash
dotnet test LearnMore.Tests
python3 -m unittest discover -s scripts -p 'test_learnmore_codex_*.py' -v
cd LearnMoreAPI
python -m pytest -q tests
```

.NET SQL 測試只接受以 `LEARNMORE_SQL_AUDIT` 提供的隔離 loopback SQL Server。Linux worker SQL 測試使用 `LEARNMORE_WORKER_TEST_SQL_PORT` 與 `LEARNMORE_WORKER_TEST_SQL_PASSWORD`，自行建立與移除合成資料庫。Python API 測試依賴列於 `LearnMoreAPI/requirements-test.txt`。
