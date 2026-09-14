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

## 2026-09-14 14:22 正式部署完成

後續已恢復 Windows 遠端控制，完成新版備份、差異準備、部署與正式 API 驗收。此節取代上方傳輸階段的未部署狀態。

- 新備份目錄為 `C:\Users\magicplus\LM-260914-3088612`，保存 2,090 個部署檔案的計畫與 5 份設定檔；舊備份保留。計畫 SHA-256 為 `3fe8450c2113515be0cbf2df0014f05826ddc668dc4eda7ac5f0c56aff827fa0`。
- 部署前檢查通過，更新 469 個差異檔案，其中 445 個有原檔備份。新版部署流程接入已通過 Windows PowerShell 5.1 測試的驗收腳本，並保留失敗還原機制。
- `result.json` 記錄 `Deployed`，完成時間為 `2026-09-14T06:22:58Z`，部署 commit 為 `1b7359f0c389527a6220e62810d2ef1ec006c8d5`。檔案與設定驗證通過，`app_offline.htm` 已移除。
- Windows 驗收通過：30 首歌曲、單曲與歌詞格式、status 版本 1，以及私人群組和收藏的訪客權限。
- 本機獨立執行 `mobile/scripts/check-backend.mjs` 通過：status、songs、單曲均為 200，groups 為 401；另外確認首頁為 200，`songs?favorites=true` 為 401。這些檢查未登入或修改資料。
- [最新 CI](https://github.com/NickYCLin/learnmore/actions/runs/34811689445)與 [iOS 流程](https://github.com/NickYCLin/learnmore/actions/runs/34811689437)均通過，commit 為 `ee6dfe0`。Windows 驗收工具成品已下載至伺服器並核對雜湊。

## 2026-09-14 16:12 TestFlight 上傳完成

已完成 Xcode 登入與 GitHub 儲存庫連結，建立 `LearnMore TestFlight` 工作流程。雲端使用 Xcode 26.3（17C529）、macOS 26.3，建置分支為 `codex/mobile-testflight-followup`。

- [Xcode Cloud build 4](https://appstoreconnect.apple.com/teams/06bfbb3b-fbcb-4682-aa62-454c3473aeb4/apps/6811343218/ci/builds/a4b54f78-fd67-46ef-b2ef-5975a869232f) 使用 `468c68775187647b20b330a7b481787405358f4b`，套件還原、前端單元測試、Capacitor 同步、iOS archive 與 App Store 格式匯出成功。
- 工作流程整體仍顯示失敗：Ad Hoc／Development 匯出需要登記實體裝置，團隊目前沒有裝置，無法產生這兩種 provisioning profile。封存、App Store 成品和診斷紀錄已下載保存。
- 從本機以明確 Team ID `PV3S28HQN7`、自動簽署及 `testFlightInternalTestingOnly` 上傳雲端封存；16:12:04 顯示 `Upload succeeded`，指令結果為 `EXPORT SUCCEEDED`。僅供內部 TestFlight，未提交 App Store 審查。
- App Store Connect 已顯示版本 1.0（build 1），上傳狀態「完成」。出口合規資訊已依本版僅使用系統 HTTPS／Web Crypto 的實作填妥，版本狀態為「準備測試」。
- 已建立「LearnMore 個人測試」內部群組，包含 1 位帳號持有人與 1 個建置版本；已確認測試者狀態為「已邀請」。群組啟用自動分發，這次上傳的 1.0（1）已加入。

## 尚待完成

- 帳號持有人在 iPhone 開啟 TestFlight 邀請並安裝 1.0（1）；目前尚未確認接受邀請或真機安裝。
- 後續可為 Xcode Cloud 配置內部測試散布動作；此次已透過 Mac 上傳並配置群組，雲端的開發用匯出警告仍保留。
- 使用測試帳號在真機驗證登入返回與取消、重新開啟 App、收藏與網站同步、播放和背景暫停。這些項目保持未完成。

## 手機介面重新設計（2026-09-14）

- 參考網頁版靛紫主色，重做首頁、歌曲卡片、底部分頁、練習頁與收藏視窗。
- 搜尋、收藏、羅馬拼音與單句重複保留；底部分頁離開練習時銷毀播放器。
- 正式建置、2 項單元測試、6 項 WebKit iPhone 介面測試通過。
- 介面測試涵蓋 320、390、430 px 寬度、搜尋、歌詞開關與收藏視窗。
- 已執行 Capacitor iOS 同步；這次改版尚未上傳 TestFlight。
- `artifacts/ios/redesign-*.png` 為測試資料截圖，播放器使用測試替身，
  不代表已驗證真機 YouTube 播放。

## TestFlight 1.0（2）已可測試（2026-09-14）

- 程式 `4bea316`，Xcode Cloud Build 6，Xcode 26.3；封存內已確認為新版首頁與 CSS，build number 為 2。
- 16:55 Apple 回報 Upload succeeded / EXPORT SUCCEEDED，17:01 App Store Connect 顯示 build 2「正在測試」，群組為「LearnMore 個人測試」。
- 本次加入 `ITSAppUsesNonExemptEncryption=false`，沿用先前已確認只使用作業系統加密的申報。
- GitHub CI `34824504389` 和 iOS `34824504229` 均通過。
- 歌曲熱門排序的後端修正尚待部署；上傳 App 不會自動更新正式 API。

後端部署阻礙：既有 noVNC 畫面可讀取，但 CUA 連線持續回報 Debugger unattached／逾時；AppleScript 輸入則遺失或改寫字元，連無害的 echo 與編碼命令都不能穩定送達。未執行本次 Prepare／Deploy，最後遠端畫面已回到 PowerShell 提示字元。正式 API 尚維持原版本。

## 網站歌曲排序同步（build 3）

- 公開首頁直接讀取正式網站 `?type=all&page=N` 的卡片資料，依網頁順序呈現，沿用網站分頁。搜尋與收藏仍使用 mobile API。
- 只解析清理後的資料屬性，不執行網站腳本或插入其 HTML；讀取失敗提示重試，不退回舊 API 排序。
- 正式建置、8 項 WebKit 介面測試通過。另讀取正式網站前兩頁，核對手機全部 144 首歌曲順序相同，前五首為 Lemon、打上花火、アイドル、Pretender、白日。
- 此修改讓 App 不必等待前述後端排序修正部署。背景播放尚未實作：目前 YouTube 嵌入播放器不提供此模式，仍待確認獨立音源或外部播放器方案。
