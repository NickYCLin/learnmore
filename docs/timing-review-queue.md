# 全站待校正清單（唯讀）

- 個人 `/Media/ReviewQueue` 仍維持原負責／協作範圍，不能當全站總數。
- `/Management/ReviewQueue` 與 `/Management/ReviewQueue/data` 使用既有
  Session Email 對應正式 `Users.Manager` 權限；未登入 401、非管理員 403。
- `GET /api/music-automation/review-queue` 使用既有音樂自動化 read token
  政策，不接受普通登入替代。兩入口讀取相同服務，不新增權限或寫入功能。
- 查詢參數 `page` 至少 1、`pageSize` 為 1 到 100；回傳 `totalCount`、
  `totalPages`、`scope=all_songs`。依 SongUid 去重、SongID 與 SongUid 排序，
  不按歌名刪除不同影片版本。跨頁期間若有其他人變更狀態，需重核總數及唯一 UID。
- 狀態僅 `high_accuracy_partial`、`high_accuracy_needs_review`、
  `high_accuracy_failed`。缺必要欄位回 503，不把不可讀解釋為零首。
- 回傳精確原始影片來源、可辨識的 YouTube ID、狀態原因及最新 job。
  `requestKeyHash` 是資料庫保存的 SHA256，不是可還原的原始 request_id；
  原始 ID 需依既有工作持久紀錄核對。無 job 不代表可安全重跑。
- 入口不讀歌詞、不啟動推論、不提供 preview/apply/狀態修改，不呼叫建表流程。
  歌曲校正仍由唯一音樂操作人循既有 report／preview／授權 apply 流程處理。

## 驗證

`dotnet test LearnMore.Tests --filter FullyQualifiedName~TimingReviewQueue`

HTTP／Razor 測試涵蓋權限隔離、唯讀路由、分頁界限與編碼；SQL 整合測試只在
既有 `LEARNMORE_SQL_AUDIT` 明確提供 loopback 測試環境時執行，略過不能當通過。
