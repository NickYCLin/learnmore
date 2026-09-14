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

## 2026-09-12 Apple App 設定

已確認個人 Apple Developer Program 會員有效，選用 `yang chen lin`（Team ID `PV3S28HQN7`）。已註冊 `tw.learnmore.app`，並建立 [App Store Connect 紀錄](https://appstoreconnect.apple.com/apps/6811343218)：名稱「ビビ學日語」、主要語言繁體中文、SKU `learnmore-ios`，iOS 1.0 處於準備提交狀態。

Xcode 專案已設定該 Team，並加入共用 App scheme、Swift 套件鎖定檔，以及 Xcode Cloud 的套件還原與 Capacitor 同步腳本。本機 Swift 套件解析通過；Xcode 可辨識 `tw.learnmore.app` 為該團隊的 iOS 封存目標。首次雲端工作流程尚未完成設定，未上傳 build 或發送邀請。

## 2026-09-13 API 欄位與部署檢查

`3088612` 修正 App API 的 JSON 欄位名稱。網站的 JSON 設定會保留 C# 原始大小寫，原本回傳 `Song`、`Lyrics`、`Id`，與 App 讀取的 `song`、`lyrics`、`id` 不符；現在明確指定歌曲、歌詞及登入使用者的欄位名稱。

- 新增兩項 JSON 回歸測試，先在舊實作確認失敗；修正後含登入工作階段測試共 8 項通過。
- 本機 HTTP 檢查確認 status 回傳 200，訪客的 groups 與收藏歌曲回傳 401；省略、空字串及空白搜尋參數均正常。使用獨立設定，未連正式資料庫。
- [Windows CI](https://github.com/NickYCLin/learnmore/actions/runs/34760429987)：531 項測試通過、零跳過，後端 publish 成功。[iOS 流程](https://github.com/NickYCLin/learnmore/actions/runs/34760429920)也已通過。

Windows 站台已完成備份。部署檢查曾因 PowerShell 5.1 將歌曲陣列多包一層，誤把多首歌的 ID 合併後查詢，收到 400 而自動還原。已確認還原後所有網站檔案與設定符合備份，首頁回傳 200；新版尚未完成正式部署，不能視為 App API 驗收通過。

## 2026-09-14 驗收腳本與成品傳輸

新增 `scripts/verify_learnmore_mobile_backend.ps1`，直接接收 JSON 解析結果，避免 PowerShell 5.1 將多首歌曲的 ID 合併。驗收也會檢查欄位大小寫、歌曲與歌詞格式、群組及收藏的訪客權限；HTTP、重新導向或連線失敗會保留 API 路徑。

- 本機 PowerShell 7.4.14 的 15 項 HTTP 回歸測試通過。
- [Windows CI](https://github.com/NickYCLin/learnmore/actions/runs/34809254951)通過，包含真正的 Windows PowerShell 5.1 回歸測試、.NET 測試及後端發布；[iOS 流程](https://github.com/NickYCLin/learnmore/actions/runs/34809255027)也通過。程式 commit 為 `7b861d5`。
- `3088612` 的後端成品已下載至本機 `artifacts/backend/LearnMore-Backend-3088612.zip`，並傳至 Windows 的 `C:\Users\magicplus\backend-3088612.zip`。兩端 SHA-256 均為 `96be9d43f9790dd692a295fa5b737512c2584564d53b8e91987bebf937466d84`，符合 GitHub artifact `10318573496`。包內 commit 為 `1b7359f0c389527a6220e62810d2ef1ec006c8d5`，已比對後端原始碼與 `3088612` 相同。
- 透過既有 DesignWeb 遠端主控台確認主機為 `WIN-6V3VA6LOJAS`，舊部署結果仍為 `RolledBack`，當時不存在 `app_offline.htm`。本次未啟動 Prepare 或 Deploy。

後續瀏覽器控制連線中斷，原分頁重新連線與新分頁復原皆未成功。新版備份腳本僅在本機準備並通過語法檢查；Windows 可能留有未送完的文字替換指令，接續前應先取消該行並確認提示字元。尚未建立新版備份或部署計畫，也尚未把新驗收腳本接入正式部署程序。最後外部查核首頁為 HTTP 200，mobile status、songs、groups 仍為 HTTP 404。

## 尚待完成

- 恢復 Windows 遠端控制後，以新的目錄備份目前站台，保留舊備份與設定，將已通過測試的 PowerShell 驗收接入還原流程，再部署已傳入的成品。
- 2026-09-14 正式 mobile API 仍為 HTTP 404；部署與正式 API 驗收尚未完成。
- 本機 Xcode 16.4，未達本專案記載的 Xcode 26 建置要求；本機可用 codesigning identity 為零。本次未產生已簽章 archive、上傳 build 或發送 TestFlight 邀請。
- 先依 [後端部署說明](README.md#後端部署)更新網站，確認 status 版本為 1、歌曲可讀、未登入的 groups 回傳 401。
- 再依 [iPhone 個人試用](DEVICE_TESTING.md)設定 Xcode、Team、Bundle ID 及簽章，透過 TestFlight 安裝。
- 使用測試帳號在真機驗證登入返回與取消、重新開啟 App、收藏與網站同步、播放和背景暫停。這些項目保持未完成。
