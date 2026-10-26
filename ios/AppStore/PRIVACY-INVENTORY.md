# App Privacy 待確認資料盤點

此表根據目前程式與 Google／YouTube 整合整理，**尚不能直接當作已確認的 App Store Connect 隱私答案**。必須盤點正式伺服器、代理存取紀錄與實際整合 SDK，再由營運者確認。

| 資料 | 用途／儲存位置 | 與會員連結 |
|---|---|---|
| 名稱、Email | Google／Apple 授權，Users | 是 |
| Provider subject、User ID | 身分識別、MobileIdentities | 是 |
| 收藏、歌單名稱 | App 功能，既有 SongGroup／Mapping | 是 |
| Session token | 裝置 Keychain；伺服器只存 SHA256、期限 | 是 |
| Apple refresh token | 伺服器 Data Protection 加密，供刪除撤銷 | 是 |
| 搜尋字詞 | GET q；可能出現在代理／網站存取 log | API 本身不帶會員 bearer，但需核對 log 是否可關聯 |
| IP、請求路徑、時間、裝置資訊 | 網路服務、安全紀錄及第三方服務 | 依正式 logging 設定判定 |
| YouTube 播放／裝置訊號 | 影片、限制與濫用判定，由 Google 處理 | 需依實際播放器、Cookie、SDK 行為確認 |
| 網站既有上傳、留言與頭像 | App 沒有原生新增入口；共用帳號刪除涵蓋 | 是 |

本 App 自行使用的 UserDefaults 僅儲存閱讀切換設定，Privacy Manifest 的理由為 CA92.1。沒有加入自行蒐集分析的 SDK；Google Sign-In 及其相依套件各帶有自己的 manifest。需在 Xcode Organizer 產生彙整 Privacy Report，再檢查 Google／AppCheck／WebView 的實際資料類型與用途。

不得因為 App 沒寫分析程式，就把 YouTube／Google 的第三方資料處理填成「未蒐集」。若正式整合會進行 Apple 定義的跨 App／網站追蹤，必須先消除該用途或實作適用的 ATT 同意流程，再修改 manifest 與 App Privacy 答案。

營運者要完成：support Email、法定／公開名稱、TLS/備份/金鑰管理、紀錄清理排程、備份輪替與刪除紀錄重套、遠端音訊處理服務資料保留與刪除、權利人回報處理。`MobilePublication` 的數字只用來顯示政策，**不會自動改變 IIS 或備份系統的設定**。
