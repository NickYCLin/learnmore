# 歌詞或秒數錯誤回報（唯讀）

- HTML：`/LearnMore/Management/ErrorReports?page=1&pageSize=25`
- JSON：`/LearnMore/Management/ErrorReports/data?page=1&pageSize=25`
- 歌曲管理頁提供「錯誤回報（限管理員）」導覽；連結可見不代表具有讀取權。
- 兩入口都先使用既有 `ControllerAccessGuard.RequireManagerAsync`，依 Session Email 查正式 `Users.Manager`。未登入 401、無管理旗標 403；不推定名稱、數字 ID 或登入即管理員，不新增角色權限。
- 全狀態清單、不提供任何變更動作。`Status=0` 僅根據既有 ReportBug 初始值標示新回報；其他狀態原樣保留，不推定已解決。
- 已知來源為 ReportBug 寫入的六欄。唯一鍵欄名不猜測：受管理授權後查 SQL Server metadata，只接受未停用、未篩選、非 NULL 的單欄唯一索引，優先主鍵。缺已知欄位或唯一鍵回 503，不當成空清單；需管理者提供實際 ErrorReports 結構證據再處理，不自動改 schema。
- `reportIdColumn` 回傳實際查得的鍵欄名。歌詞只顯示原始 LyricID，不讀取歌詞正文；依 SongUid 精確取得 Songs.Title、YouTubeVideoUrl，不按歌名合併版本。歌曲已刪除時回報仍保留，歌曲資訊為 NULL。Songs 相同 UID 不唯一則查詢失敗，不任選影片來源。
- page 從 1 起，pageSize 1–100。依 ReportDate DESC、唯一鍵 DESC 排序，回傳 totalCount、totalPages、page、pageSize，空的超界頁仍有總筆數。單次查詢用短唯讀 Serializable 交易確保總數和該頁一致；跨頁是即時資料，新增可能移動位置，盤點時需核對總數並依 ReportId 去重。
- 查詢不篩選狀態，涵蓋既有未解決及新回報。日期是資料庫原始時間，未假定時區。所有使用者文字由 Razor 編碼，不把影片原始值直接插入 href；不回傳回報者 Email。
- 管理角色真實人員對應、正式資料結構、完整分頁內容及實際畫面需由既有合法管理登入驗收。未完成前不得宣稱零筆或全清單已讀。
- `ErrorReportReadOnlyTests` 包含真實 HTTP 路由拒絕、參數綁定、NoStore、僅 GET、Razor 編碼測試。`ErrorReportSqlTests` 依既有 `LEARNMORE_SQL_AUDIT` 僅接受明確設定的 loopback master，建立獨立合成資料庫驗證 SQL 排序、未解決／其他／NULL 狀態、不同影片、孤兒回報、越界頁及真實 Manager 旗標。未提供此隔離環境時略過，不代表已驗正式 schema 或角色。
