# 管理清單分頁

- `/Media/Manage` 的主負責與協同合作各自每頁 20 首。
  `producerPage`、`collaborationPage` 為正整數，預設為 1。
  換頁保留另一欄位置，並以錨點回到該欄。
- 歌曲在 SQL Server 計算總數與擷取當頁，依 `SongID DESC, SongUid` 排序。
  重複或不存在的歌曲關聯不計入總數；超出範圍回到最後有效頁。
  空清單顯示第 1 頁、共 0 首；無效頁碼回應 HTTP 400。
- UID 集合使用 XML 參數，避免歌曲超過 2,100 首時撞到 SQL 參數數量限制。
  計數與當頁查詢使用同一個短交易，避免期間增刪造成總數不一致。
- `/Management/ReviewQueue` 與 `/Management/ErrorReports` 保留既有伺服器端
  分頁與 `pageSize`（預設 25、最多 100），新增首頁、末頁與目前頁前後各兩頁。
  過大的頁碼保留原有空清單及回到第一頁提示；JSON 契約不變。
- 頁碼在手機上自動換行，並提供目前頁與停用按鈕的無障礙標記。
- 校正清單與重試流程仍使用完整歌曲查詢，不受畫面分頁限制。
  分頁不修改原始歌曲、回報、人工處理狀態或歷程。

驗證：`dotnet test LearnMore.Tests/LearnMore.Tests.csproj`。
真 SQL 測試須設定 `LEARNMORE_SQL_AUDIT` 為本機回環位址的 master 連線，
測試只建立並清除自己的隨機資料庫，不可指向正式環境。
