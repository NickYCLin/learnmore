# 錯誤回報的單句假名確認

既有 `Media/EditLyrics` 依製作者／協作者判權，不因 Manager 自動放行；
儲存只更新 Japanese、Chinese、Roman、TimeStamp，不適用本次假名修正。
本功能沿用 `ControllerAccessGuard.RequireManagerAsync` 的正式 Users.Manager，
不變更任何帳號角色。ErrorReports 仍無狀態修改入口。

- GET `/LearnMore/Management/ErrorReports/ruby/{reportId}`：顯示原始列與建議差異。
- POST 同一路徑：Manager 確認及有效反偽造 token 才可送出。
- 本增量僅開放回報 46、48、49，固定綁定水平線
  `69e99493-ac81-4f17-941e-834df328555e`、影片 `iqEr3P78fz8`、
  LyricID 3、13、15；不能接收任意歌曲、欄位或替換文字。
- 僅將兩處 `人` 的 `にん` 改為 `ひと`、`重な` 的 `おもな` 改為 `かさな`。
  literal 必須恰好出現一次；格式不符、已改過、不同來源均拒絕。
- Serializable + UPDLOCK/HOLDLOCK 讀取回報、歌曲、整列歌詞，核對 SHA256
  指紋（含原始 Japanese/JapaneseRuby/Roman 與其他欄位），拒絕過期表單。
  唯一 UPDATE 只有 JapaneseRuby；同交易讀回整列，任何其他欄位變動即回滾。
- 不新增資料表、不寫回報狀態；47 秒數、50 分句需原影片實播後另行確認。

備份寫在 OS CommonApplicationData 下的 `LearnMore/RubyRepairAudit`，
Windows 為 `C:\ProgramData\LearnMore\RubyRepairAudit`，不在網站根目錄。
IIS 執行身分須能寫入此目錄，管理者應限制 ACL 僅應用程式與維運人員可讀。
prepared.json 以 CreateNew、WriteThrough、Flush(true) 在 UPDATE 前保存
整列原值、來源、回報、操作帳號與建議結果；備份失敗即不寫資料。
committed.json 表示 SQL commit 已回傳成功。只有 prepared 不代表成功，
連線中斷／commit 結果未知須以稽核 ID 及正式讀回核實，不可盲目重送。
成功回應也須人工讀回；不自動套用其他回報或視為歌曲已完成。

本輪部署不提交任何正式修正 POST；由既有正式 Browser 驗確認頁，
實際三筆修正由管理員後續明確確認。不得為此提升角色或繞過權限。
