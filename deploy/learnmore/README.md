# LearnMore Docker 內網部署（yangchen-MS-7E06）

網站（Kestrel, net8.0）與 SQL Server 2022 都跑在 Docker，只綁 `127.0.0.1`，不對外。

| 服務 | 容器 | 主機埠 |
|------|------|--------|
| SQL Server 2022 (Express) | `learnmore-sqlserver` | `127.0.0.1:14334` → 1433 |
| 網站 | `learnmore-web` | `127.0.0.1:18090` → 8080，網址 `http://127.0.0.1:18090/LearnMore/` |

## 部署目錄

`LEARNMORE_STORAGE_ROOT`（預設 `/data/yangchen/learnmore-deploy`）：

```
.env                       # chmod 600，SA 密碼等機密，不進 git
mssql/{data,log,secrets}   # SQL Server 資料（uid 10001）
mssql/backup               # 放舊站的 .bak，容器內路徑 /var/opt/mssql/backup
web/data                   # Whisper JSON、audio-stems 工作目錄、trace log（原 D:\Data）
web/dataprotection-keys    # 原 C:\ProgramData\LearnMore\DataProtectionKeys
web/cookies                # 原 D:\cookies（可寫掛載到 /var/lib/learnmore/cookies；yt-dlp 會寫回 cookies）
web/uploads                # 原 wwwroot\uploads
web/audio-stems            # 原 wwwroot\audio-stems
web/appsettings.Local.json # 原 D:\Web\LearnMore\appsettings.Local.json（不含連線字串也可，連線字串由 compose 注入）
```

## 設定對照（Windows → 容器）

| 設定 | 舊值 | 容器預設 |
|------|------|----------|
| `FFmpegPath` | `D:\Tools\ffmpeg\...\ffmpeg.exe` | `/usr/bin/ffmpeg`（ffprobe 同目錄） |
| `YtDlpPath` | `C:\Tools\yt-dlp.exe` | `/usr/local/bin/yt-dlp` |
| `YtDlpCookiesPath` | `D:\cookies\...` | `/var/lib/learnmore/cookies/<檔名>` |
| `PersistentSession:DataProtectionKeyPath` | ProgramData | `/var/lib/learnmore/dataprotection-keys` |
| `WhisperJsonPath` | — | `/var/lib/learnmore/data/whisper` |
| `AudioStemProcessing:WorkRoot` | — | `/var/lib/learnmore/data/audio-stems-work` |
| trace log | `D:\Data\*.log` | `LEARNMORE_TRACE_DIR=/var/lib/learnmore/data/trace` |
| `Hosting:PathBase` | IIS 虛擬目錄 | `/LearnMore` |
| `Hosting:UseForwardedHeaders` | — | `true`（信任 172.16.0.0/12、127.0.0.0/8） |
| Whisper / UVR 遠端 API | — | `http://learnmore-api:8080`（加入 `ycspace_apps` 網路） |

注意：舊的 `appsettings.Local.json` 若含 Windows 路徑（FFmpegPath、YtDlpPath、cookies、PythonPath 等），
放進 `web/appsettings.Local.json` 前要刪掉或改成上表的 Linux 路徑，因為它的優先順序高於環境變數以外的設定。

## 啟動

```bash
ENV=/data/yangchen/learnmore-deploy/.env
docker compose --env-file $ENV -f deploy/learnmore/docker-compose.yml up -d sqlserver
# 還原資料庫後才啟動網站（web 在 profile "web"）
docker compose --env-file $ENV -f deploy/learnmore/docker-compose.yml --profile web up -d --build web
```

網站啟動時會連 `language` 資料庫；資料庫不存在會直接結束。專案沒有 EF migrations，
schema 只能靠還原舊庫（`docs/sql/` 只有部分增量腳本）。

## 從舊站還原

```bash
# 1. 把 language.bak 放到 /data/yangchen/learnmore-deploy/mssql/backup/
# 2. 查邏輯檔名
docker exec learnmore-sqlserver bash -c '/opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$MSSQL_SA_PASSWORD" \
  -Q "RESTORE FILELISTONLY FROM DISK=N'"'"'/var/opt/mssql/backup/language.bak'"'"'"'
# 3. 還原（把 <data>/<log> 換成上一步的 LogicalName）
docker exec learnmore-sqlserver bash -c '/opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -Q "
RESTORE DATABASE [language] FROM DISK=N'"'"'/var/opt/mssql/backup/language.bak'"'"'
WITH MOVE N'"'"'<data>'"'"' TO N'"'"'/var/opt/mssql/data/language.mdf'"'"',
     MOVE N'"'"'<log>'"'"' TO N'"'"'/var/opt/mssql/data/language_log.ldf'"'"', STATS=10"'
# 4. 檔案：uploads / audio-stems / DataProtectionKeys / cookies 解壓到 web/ 對應目錄，
#    再 chown -R 1654:1654；cookies 檔要容器 app（uid/gid 1654）可寫，例：chmod 660 web/cookies/*.txt 且群組為 1654
```

## 已知限制與注意事項（搬家後測試整理）

- **yt-dlp**：cookies 不可唯讀掛載，否則 yt-dlp 結束時寫回 cookies 會丟 `OSError: [Errno 30] Read-only file system`、結束代碼非 0，字幕下載會被判定失敗。
  映像內 `/etc/yt-dlp.conf` 設定 `--js-runtimes node`（需 Node 22+，Node 20 會被 yt-dlp 判定 unsupported）。
- **上傳時的「逐句校正歌詞秒數」**：`VocalOnsetDetectionService` 會呼叫本機 `python3` + faster-whisper（`Scripts/faster_whisper_words.py`）。
  容器沒有裝 Python（避免映像多出 1GB 以上與模型下載），而且 OpenAI 備援預設關閉，所以這一步在容器內會失敗並退回歌詞來源原本的時間戳；
  之後的高精度校正仍會透過 learnmore-api（`HighAccuracyRemoteApi`）完成。
  learnmore-api 目前只有 `/v1/transcribe-youtube`（吃 YouTube URL），和這一步吃「本機已前處理音檔路徑」的介面不同，改走遠端需要另外設計，這次先不改。
  若需要本機這一步，可在映像加 python3 + faster-whisper，並設定 `PythonPath`。
- **語系**：映像設 `LANG=zh_TW.UTF-8`，讓日期等格式與舊 Windows（zh-TW）一致，例如許願板的 `CreatedAt`。
- **行動版說明頁**：`/Mobile/Privacy`、`/Mobile/Support` 需要 `MobilePublication:SupportEmail`、`OperatorName`、`LogRetentionDays`、`BackupRetentionDays`，
  可在 `.env` 填 `MOBILE_SUPPORT_EMAIL` 等四項；未填時這兩頁回 503。
- **`/User/CreateOrder`**（舊有問題，未修）：頁面用了 `Vue.createApp` 但沒有載入 Vue，而且表單送往藍新測試環境 `ccore.newebpay.com`；站內沒有連結到這頁，贊助本站目前用綠界連結。
