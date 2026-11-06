# 最後登入時間

部署前先備份 DefaultConnection 資料庫，再執行
`docs/sql/006-account-last-login.sql`。此 migration 可重複執行，新增
`dbo.Users.LastLoginAt datetime2(7) NULL`，既有資料保留 NULL，不偽造
歷史登入時間。應先完成 migration 再更新應用程式。

網站 Google 登入、受 Smoke Token 保護的測試帳號登入，以及手機
Google／Apple 登入成功時，都使用 SQL Server 的 SYSUTCDATETIME()
更新同一欄位。舊版手機 PKCE 交換驗證成功時也更新；寫入失敗則撤銷
剛產生的 Token，不回傳成功。Google／手機登入的更新與帳號、識別碼、Session 資料
使用同一交易，任何認證、連結檢查或資料寫入失敗都不更新。

此欄位表示最近一次成功登入，不是最近活動時間；持久 Cookie 還原、
手機 Token 驗證及修改暱稱／頭像不更新。顯示時須從 UTC 轉換時區。
目前只儲存時間，不新增後台顯示或完整登入歷程。
