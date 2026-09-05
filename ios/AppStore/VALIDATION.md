# 發佈驗證紀錄

## 本機已完成（持續更新）

- 原歌曲 Mobile API：11 項測試通過。
- 會員／收藏／網站登入／隱私相關測試第一輪：47 項通過。
- Xcode project、Info.plist、PrivacyInfo.xcprivacy 格式檢查通過。
- 完整 .NET 測試：546 項通過；Swift PlaybackCore：4 項通過。
- iOS 初次 Debug build 因建置中才新增 AppNetwork 檔案而失敗；已將檔案加入專案，待完整重建結果。
- 本機 UI test 尚未執行：Xcode 16.4 找不到已列出的 iOS 18.6 目的裝置，CoreSimulator 查詢亦出現長時間未回應；正在排查，不能視為通過。
- Release build：執行中；另新增 Xcode 26.3 的 GitHub Actions 驗證流程，尚未執行。
- 正式站 `api/mobile/v1/songs?pageSize=1`：HTTP 404，尚未部署。
- Xcode 16.4、macOS 15.7.9；本機 codesigning identities：0。尚不符合提交環境要求。

## staging / 真機必要測試（未執行）

| 情境 | 必須驗證的結果 |
|---|---|
| Google 舊會員登入 | Users.Id 不變，網站既有歌單與收藏出現 |
| Google 新會員登入 | 新 Users row 與 provider subject mapping，App 與網站可互通 |
| Apple 首次登入／隱藏信箱 | 建立獨立帳號，名稱不重複覆寫；再次登入仍是同一帳號 |
| Google 會員連結 Apple | 同一 User ID；Apple 再登入可讀原有收藏 |
| Apple 會員連結 Google | App 與網站 Google 登入仍回到原 Apple 建立的 User ID |
| 同 Email 不同 provider | 未驗證原帳號時不得自動合併 |
| 外部 Email Google 舊會員 | 不得自動銜接，另行完成信箱所有權驗證／遷移方案 |
| 過期／撤銷 session | 回傳 401；iOS 清 Keychain，個人內容不再顯示 |
| 訪客或網站 Cookie 呼叫 Mobile 寫入 | 401，資料不得改變 |
| A 帳號讀／改 B 的歌單 ID | 404，B 的資料不變 |
| 重複新增收藏／並行增刪 | 不重複、無遺失、SQL transaction 行為正確 |
| App 增刪／網站增刪 | 另一端重新整理後一致 |
| 刪除取消／錯誤身分 | 帳號與所有資料保留 |
| 刪除 Google／Apple／雙 provider 帳號 | 已知個資清除、所有 App session 撤銷、舊網站 Cookie 失效、Apple revoke 成功、頭像 outbox 清空 |
| Apple revoke 失敗／SQL constraint 失敗 | 回傳失敗，無部分 SQL 刪除；修復後可重新驗證重試 |
| 刪除與並行登入／連結／收藏 | 不得出現刪除後仍有效的 session、孤兒收藏或未撤銷 Apple 身份 |
| 重建相同 Email 帳號 | 舊 Cookie 不可存取新帳號 |
| 影片一般／禁止嵌入／地區限制／斷線 | 正常播放或可理解的錯誤與重試；不繞過限制 |
| 前後跳轉／背景／離開／Share sheet | 歌詞反白正確；背景與離開暫停，影片保持可見 |
| iPhone 小螢幕／iPad 分割畫面／橫向 | 影片最少 200×200、控制項可操作、歌詞可閱讀 |
| VoiceOver／大字／深色／減少動態 | 標籤可理解、排版無遮擋、能完成主要功能 |
| 備份復原與刪除紀錄重套 | 已刪除會員不會因還原而復活 |

UI fixture 截圖僅供內部驗證，不可當作正式 App Store 截圖。登入、刪除及跨平台同步必須使用專用測試帳號與 staging 資料。
