# 部署與儲存庫同步

GitLab 與 GitHub 的 `main` 使用同一份受版本控制的內容。兩邊提交歷史不同，commit SHA 可以不同；檔案內容與 Git tree 應相同。

## 既有站台第一次更新

過去 GitLab 的 `appsettings.json` 包含環境專用設定；現在兩邊都使用不含憑證的設定範本。**覆蓋站台檔案前，先備份伺服器原本的設定。**

1. 備份伺服器目前的 `appsettings.json` 與既有本機設定檔。
2. 尚無 `appsettings.Local.json` 時，將原本 `appsettings.json` 複製為同目錄下的 `appsettings.Local.json`。若已存在，保留其中的設定並合併缺少的值。
3. 確認資料庫、Google 登入、金流、API Key、音訊工具路徑，以及 `AudioStemProcessing.Enabled` 等功能開關符合該站需求。
4. 更新新產生的發布檔，保留 `appsettings.Local.json`、`appsettings.<Environment>.local.json`、上傳內容與音軌資料。

本機設定檔已排除於 Git 與 `dotnet publish`，請在伺服器上管理並限制讀取權限。本次儲存庫同步不會改動線上設定或資料庫。

## 建置網站

兩邊都從乾淨的 checkout 執行以下命令：

```powershell
npm ci --prefix LearnMore/wwwroot/js
dotnet restore LearnMore.sln
dotnet test LearnMore.sln --configuration Release --no-restore
dotnet publish LearnMore/LearnMore.csproj --configuration Release --no-restore --output artifacts/publish
```

將 `artifacts/publish` 作為部署來源。不要使用歷史提交中附帶的 `publish` 成品。Kuromoji 字典隨 npm 套件還原，不需要初始化舊的 `dict/kuromoji.js` gitlink。

兩邊程式相同仍須搭配相同的資料庫、設定、外部服務和媒體檔，才會有相同的上線行為。Git 裡的範本預設關閉音軌分離；需要這項功能時，在正式設定啟用並配置本機或遠端處理服務。

## Mika 角色服務

角色嵌入頁、設定 API 與備援圖片都依賴 Mika 服務。若面板顯示「角色暫時無法載入」，先確認該服務可連線，再按重新載入；網站的歌曲播放仍可使用。

服務遷移時，在伺服器 `appsettings.Local.json` 設定 `MikaAvatar:BaseUrl`（HTTPS、包含服務路徑）與選用的 `MikaAvatar:WebSocketUrl`（WSS）。網站會同步使用對應的 CSP 來源；Mika 端仍需允許 LearnMore 網站的 CORS 與 iframe 嵌入來源。

iOS App 專案、API 路徑、建置方式及驗收範圍見 [mobile/README.md](../mobile/README.md)。

## 遠端處理服務

`LearnMoreAPI/` 包含 FastAPI 服務、Dockerfile 和測試；使用方式見 [API 說明](../LearnMoreAPI/README.md)。複製 `.env.example` 為 `.env` 後填入服務 token 與工具位置，透過容器環境變數注入。請勿提交 `.env`。

網站端使用 `HighAccuracyRemoteApi` 與 `AudioStemProcessing` 的遠端 API 設定連接服務。Mika 角色服務、SQL Server、模型與音訊工具仍須另外準備。

## 維運腳本

`scripts/` 內的資料回填與登入驗證工具會依指令修改資料，執行前先確認目標站台並使用腳本提供的 dry-run 選項。SSH 相關預設指向 localhost，請明確指定正式目標。

| 環境變數 | 用途 |
| --- | --- |
| `LEARNMORE_SSH_HOST`、`LEARNMORE_SSH_PORT` | 維運 SSH 主機與連接埠 |
| `LEARNMORE_SSH_USER`、`LEARNMORE_SSH_CONFIG` | SSH 使用者與憑證檔位置 |
| `LEARNMORE_APPSETTINGS` | 遠端設定檔位置，預設為 `D:\Web\LearnMore\appsettings.Local.json` |
| `LEARNMORE_TEST_EMAIL` | 登入驗證使用者 |
| `LEARNMORE_API_BASE_URL` | 翻譯、音軌處理 API 位址 |
| `MIKA_AVATAR_BASE_URL` | Mika 驗證工具使用的服務位址 |
| `NODE_BIN`、`NODE_MODULES` | 瀏覽器驗證工具使用的 Node 與 Playwright 套件目錄 |
| `LEARNMORE_NAS_CREDENTIAL_FILE`、`LEARNMORE_NAS_SHARE` | 音軌回填時選用的 NAS 設定，設於遠端執行環境 |

## 後續同步方式

1. 兩邊先檢查工作目錄並 `git pull --ff-only`，比較其他人的改動。
2. 在各自歷史上提交同一份已驗證內容，勿將 GitLab 的舊歷史合併到公開 GitHub。
3. 使用自然、簡潔的繁體中文提交訊息，例如 `fix(mika): 同步角色切換與歌曲動作控制`。
4. push 前再次 pull；若遠端新增提交，先整合並重跑相關檢查。
5. 推送後重新 fetch，確認各自本機與遠端一致，再比較兩邊檔案樹：

```powershell
git -C <GitLab工作目錄> rev-parse 'origin/main^{tree}'
git -C <GitHub工作目錄> rev-parse 'origin/main^{tree}'
```

兩個 tree hash 必須相同。不使用 force push 覆蓋他人的提交。
