# iPhone 試用驗證紀錄

## 2026-09-10 接續進度

以 `main` 的 `e643ff4`（補上測試版圖示與 TestFlight 安裝說明）為基礎，接續 `mobile/` 的 Capacitor App。本次修正登入狀態與私人畫面的清理。

原本登入到期只會將按鈕改成「登入」，收藏清單仍留在畫面上。若舊請求在重新登入後才回傳 401，還會清掉新登入；晚回傳的群組資料也可能在登出後重新打開視窗。現在會核對請求送出時的憑證，忽略已結束工作階段的回應，並在登出或到期時清除收藏、群組與未送出的群組名稱。

## 本機已驗證

- Node.js 24.19.0：`npm test` 兩項通過。
- `npm run test:ui`：WebKit／iPhone 13 畫面設定，五項通過、零跳過。
- `npm run ios:sync`：TypeScript 檢查、Vite 正式建置與 Capacitor iOS 資源同步通過；Info.plist 和 Xcode project 格式檢查通過。
- 三個主要回歸情境均先在原程式重現失敗，再確認修正後通過。

| 情境 | 結果 |
| --- | --- |
| 收藏頁遇到登入到期 | 清除私人清單，切回探索歌曲 |
| 舊帳號請求在新登入後回傳 401 | 新登入保留，不顯示舊請求的到期訊息 |
| 登出後收到舊群組回應 | 不打開收藏視窗，不保留私人群組內容 |
| 目前帳號移除收藏 | 送出目前憑證與正確群組操作，成功後更新選取狀態 |
| 收藏視窗內遇到登入到期 | 關閉並清空群組視窗、重設輸入內容，返回後可讀公開歌曲 |

上述測試使用原創例句、模擬 API 和原生 bridge 替身，未驗證真正的 Google 授權、原生回呼、SQL 寫入或 YouTube 播放。測試替身只由獨立的測試 Vite 設定載入；正式建置使用 Capacitor。CI 已加入相同測試，失敗時保留 trace 七天。

## 2026-09-12 雲端驗證與建置成品

[PR #2](https://github.com/NickYCLin/learnmore/pull/2) 的程式 commit 為 `14b8021`。以下工作流程使用 GitHub 產生的合併 commit `9deec24`，已確認其檔案內容與 `14b8021` 相同。

- [iOS 工作流程](https://github.com/NickYCLin/learnmore/actions/runs/34686424724)：兩項單元測試、五項 WebKit UI 測試及資源同步通過；Xcode 26.3 的 Release 模擬器編譯成功。
- [CI](https://github.com/NickYCLin/learnmore/actions/runs/34686424752)：Windows .NET 測試 529 項通過、零跳過；Python／API／腳本工作也通過，後端 publish 成功。
- [模擬器成品](https://github.com/NickYCLin/learnmore/actions/runs/34686424724/artifacts/10295917057)：Bundle ID 為 `tw.learnmore.app`，版本 1.0／build 1。這是 iOS Simulator 的 `App.app`，不能直接安裝到 iPhone。
- [Windows 後端部署包](https://github.com/NickYCLin/learnmore/actions/runs/34686424752/artifacts/10295502713)：已確認包含 `LearnMore.dll`、`web.config`、mobile 播放頁腳本與 kuromoji 字典，沒有伺服器本機設定檔。

兩份成品均已下載，SHA-256 與 GitHub 回報相符。GitHub 成品保留至 2026-09-26；本機副本放在不納入 Git 的 `artifacts/ios/` 與 `artifacts/backend/`。

## 尚待完成

- 已確認個人 Apple Developer Program 會員有效；`tw.learnmore.app` 尚未註冊，App Store Connect 也尚未建立 LearnMore 紀錄。
- 2026-09-12 再次查詢正式站 `/LearnMore/api/mobile/v1/status`、`songs`、`groups`，仍均為 HTTP 404；App 尚無可用的正式 mobile API。
- 本機 Xcode 16.4，未達本專案記載的 Xcode 26 建置要求；本機可用 codesigning identity 為零。本次未產生已簽章 archive、上傳 build 或發送 TestFlight 邀請。
- 先依 [後端部署說明](README.md#後端部署)更新網站，確認 status 版本為 1、歌曲可讀、未登入的 groups 回傳 401。
- 再依 [iPhone 個人試用](DEVICE_TESTING.md)設定 Xcode、Team、Bundle ID 及簽章，透過 TestFlight 安裝。
- 使用測試帳號在真機驗證登入返回與取消、重新開啟 App、收藏與網站同步、播放和背景暫停。這些項目保持未完成。
