# 網站流量

登入管理員帳號後，選擇「網站流量」（`/Management/Traffic`，部署於 `/LearnMore` 時需加上前綴）。

## 站內瀏覽

- 部署後開始統計，不回補過往紀錄。統計首頁、搜尋、歌詞、群組播放與相關資訊、許願留言板的成功 HTML 瀏覽。不含後台、API、已知機器人與標記為預載的請求；無法完全排除自動化流量。
- 瀏覽次數不是訪客人數，也不是線上人數。來源分類依瀏覽器傳來的參照網址判定，部分瀏覽器或應用程式不會傳送來源。只儲存日期、頁面類別、來源類別、次數及最近瀏覽時間；不保存 IP、帳號、完整網址或站內搜尋字詞。
- 明細保留最近 30 個台灣日曆日。單一 Web 實例每分鐘寫入一次，正常停止時也會嘗試保存；非正常停止可能遺失未寫入的計數。Docker 使用既有的 `/var/lib/learnmore/data` 持久化掛載；其他主機可用 `SiteTraffic:SnapshotPath` 設成可寫入、部署時不會被覆蓋且不可公開存取的絕對路徑。不要讓多個實例寫入同一份快照。
- 快照讀取失敗時保留原檔，停止統計並在後台顯示無法讀取。先排除路徑或權限問題；若檔案毀損，備份後再復原並重新啟動。

## Google 搜尋關鍵字

1. 在 Google Search Console 驗證網站資源，優先使用涵蓋 LearnMore 的網址前置字元資源。
2. 在 Google Cloud 專案啟用 Search Console API，建立專用服務帳戶，將服務帳戶的電子郵件加入該 Search Console 資源並授予讀取成效報表的權限。程式只要求 `webmasters.readonly` OAuth 範圍。
3. 在伺服器私密設定填入 `SiteTraffic:SearchConsole:Property`（完整的 Search Console 資源識別，例如 `https://example.com/LearnMore/`）、`SiteTraffic:SearchConsole:CredentialPath`（服務帳戶 JSON 的絕對路徑）。金鑰檔須放在網站根目錄之外、限制檔案權限；Docker 中需額外以唯讀方式掛入容器。不要提交金鑰或放在 `wwwroot`。
4. 若 Search Console 資源也包含其他網站路徑，另設 `SiteTraffic:SearchConsole:PagePrefix` 為 LearnMore 的正式完整網址前綴（例如 `https://example.com/LearnMore/`）。報表僅顯示 Google 網頁搜尋的前 20 個字詞，期間採太平洋時間，截止於三天前且只讀已定稿資料。Google 不揭露部分字詞，點擊次數也不等於站內瀏覽次數。
