# 後端與登入設定

## 先在 staging 驗證

1. 備份資料庫及 ASP.NET Data Protection 金鑰目錄。使用正式 schema 的去識別化 staging 副本，不要拿正式會員測試刪除。
2. 執行 `docs/sql/000-mobile-preflight.sql`。確認欄位、型別、額外外鍵、非外鍵的 Email/UserId 關聯、重複 Email／收藏、`Songs.AddedByUserId` 可為 NULL。檢查 Users 的 identity/default、SongGroup.GroupUid default，以及網站部署的 `/LearnMore/` PathBase。
3. 執行 `docs/sql/001-mobile-accounts.sql`。此 migration 只新增 MobileIdentities、MobileSessions、MobileFileDeletionJobs，沒有改動舊表或資料。
4. 發佈 .NET 8 後端，設定以下資料。初次維持 `MobileAuth:Enabled=false`，確認 schema 與相依服務可用後再啟用。任何未知刪除相依性都必須先補實作與測試。
5. 依 VALIDATION.md 的測試矩陣驗證，再部署正式站。此工作階段尚未存取 staging／正式 DB 或部署帳號。

## 設定位置

伺服器秘密只放在部署環境變數、秘密管理器或已忽略的 `LearnMore/appsettings.Local.json`。不要放 App、Git、商店審查欄位或聊天。

| Key | 設定 |
|---|---|
| `MobileAuth:Enabled` | schema、provider、刪除測試完成後 true |
| `MobileAuth:GoogleServerClientId` | 與 iOS `GOOGLE_SERVER_CLIENT_ID` 相同的 Web OAuth client ID |
| `MobileAuth:GoogleClientSecret` | 該 Web OAuth client 的 secret，僅伺服器 |
| `MobileAuth:AppleBundleId` | App 的正式 Bundle ID |
| `MobileAuth:AppleTeamId` | Apple Developer Team ID |
| `MobileAuth:AppleKeyId` | 開通 Sign in with Apple 的 key ID |
| `MobileAuth:ApplePrivateKeyPath` | 僅伺服器可讀的 Apple `.p8` 檔完整路徑，網站根目錄外 |
| `MobilePublication:OperatorName` | 公開營運者名稱 |
| `MobilePublication:SupportEmail` | 確實有人收信的公開支援信箱 |
| `MobilePublication:LogRetentionDays` | 實際設定並驗證的紀錄保留天數 |
| `MobilePublication:BackupRetentionDays` | 實際設定並驗證的備份輪替天數 |

環境變數使用雙底線，例如 `MobileAuth__AppleBundleId`。Apple private key 檔案需由你在伺服器安全放置；我不需要看到私鑰內容。網站原有 `GoogleApiClientId` 仍需設定，與 Web OAuth client 相符。

IIS/反向代理必須提供 HTTPS、正確 PathBase、保留 Authorization header。不要記錄 HTTP Authorization、OAuth code、token 回應、request body。存取紀錄可能含搜尋字詞，需依政策保留週期清理。若前面有反向代理，rate limiter 的 client IP 必須使用明確信任的代理設定，不能任意信任 X-Forwarded-For。

Data Protection 金鑰需跨部署保留、限制讀取並備份；遺失後既有 Apple refresh token 不能解密撤銷，必須先復原金鑰。伺服器時間需正確同步，且能連線 Google OAuth、Apple token/keys/revoke endpoints。

## Apple 與 Google

1. Apple Developer 註冊正式 Bundle ID，啟用 Sign in with Apple。在 Xcode 加入 Team 與簽章，安裝 Xcode 26 以上。
2. 在 App Store Connect 建立 iOS App 記錄，Bundle ID 完全相符。App 保留 iOS 17 為最低系統版本；建置 SDK 必須符合當日要求。
3. Google Cloud 使用與網站同一專案，建立 iOS OAuth client（Bundle ID 相符）及 Web OAuth client，完成 OAuth consent screen 的公開狀態、支援資訊與必要驗證。
4. 複製 `ios/Config/Local.example.xcconfig` 為 `Local.xcconfig`，填公開 ID。反轉 Google iOS client ID 作為 callback scheme。
5. 原網站 Google 帳號可讀取同一 Users.Id 與歌單。Google 僅可自動銜接由 Google 管理的已驗證 Gmail／Workspace 地址；使用第三方信箱的既有帳號需要另外確認信箱所有權，現在會拒絕自動銜接。不可為了通過測試直接解除這個保護。
6. Apple 首次登入建立獨立身份；既有會員請先 Google 登入後「連結其他登入方式」。Apple relay email 不會自動合併。App 內連結 Google 後，網站 Google 登入會依 provider subject 找回同一帳號。

## 刪除與回復

刪除必須重新取得一次性 Google／Apple authorization code，伺服器驗證後確認 provider subject 屬於本人。Apple token 撤銷失敗會回傳錯誤並保留帳號；資料清理失敗則回滾 SQL transaction，不回報成功。若 Apple 已撤銷但 SQL 清理失敗，先修復 schema／服務，再重新驗證重試刪除。

已知清理範圍：Users、MobileSessions、MobileIdentities、SongGroup／Mapping、Comments／Replies、Wish／Replies、Feedbacks、ErrorReports、頭像；Songs 依 AddedByUserId／舊 Producer 歸屬刪除，同時清除動態歌詞表、別名、統計、音軌紀錄與工作、相關收藏引用。刪除使用者留言時，附屬回覆一併刪除。其他使用者上傳的歌曲保留；他人歌單中指向已刪歌曲的關聯會移除。額外 schema、備份、安全紀錄及基礎設施副本必須依正式資料盤點完成處理。

頭像透過 SQL outbox 排入 `MobileFileDeletionJobs`，背景工作每分鐘重試。監控該表未完成筆數與 cleanup 錯誤。備份復原程序需從獨立的受控刪除紀錄重套刪除作業，不能讓已刪除會員因復原備份重新出現；此為營運設定，尚未核實。

回滾應用程式時保留新增資料表及金鑰，關閉新的登入入口。不可直接回退到不驗證已刪除 User ID 的網站版本，否則舊 Cookie 可能重新被接受。先在 staging 演練回滾與資料復原。

## 發佈

`python3 ios/scripts/preflight.py --stage archive` 檢查 Xcode、ID、簽章及資產；通過後可 `bash ios/scripts/archive.sh` 封存。預設不自動上傳。

完成 TestFlight、實機、隱私、內容權利、截圖與審查資料後，更新 `release-status.json` 並記錄證據，再執行 `python3 ios/scripts/preflight.py`。只有所有檢查通過，才能視為準備好送審；Apple 是否核准仍由實際審查決定。

官方依據（2026-09-06 核對）：
- https://developer.apple.com/news/upcoming-requirements/
- https://developer.apple.com/app-store/review/guidelines/
- https://developer.apple.com/support/offering-account-deletion-in-your-app/
- https://developer.apple.com/documentation/technotes/tn3194-handling-account-deletions-and-revoking-tokens-for-sign-in-with-apple
- https://developers.google.com/identity/sign-in/ios/backend-auth
- https://developers.google.com/identity/sign-in/ios/offline-access
