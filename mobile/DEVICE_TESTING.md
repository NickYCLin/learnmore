# iPhone 個人試用

目前先透過 TestFlight 邀請安裝到自己的手機，不發布 App Store。此專案已有 iOS 容器與本機前端，但還沒有完成簽章安裝及真實後端驗收。

## TestFlight 邀請安裝，不需要傳輸線

需要有效的 Apple Developer Program 會員、App Store Connect 的 App 紀錄，以及 Mac 上的 Xcode。免費 Personal Team 不提供 TestFlight。

1. 確認正式 Team 與 Bundle ID，在 App Store Connect 建立相符的 App 紀錄。
2. 在 Mac 執行下節的 `npm ci`、`npm run ios:sync`、`npm run ios:open`，在 Xcode 設定團隊與自動簽章。
3. 確認後端 API 已部署並可讀取歌曲；在 Xcode 選 generic iOS device 執行目標，使用 Product → Archive，不需要實體 iPhone 接線。
4. 在 Organizer 驗證封存，透過 Distribute App 上傳 App Store Connect。每次上傳使用新的 Build number；依實際加密使用情況回答出口合規問題。
5. 等待 Apple 處理完成後，在 TestFlight 建立內部測試群組，加入有該 App 存取權的自己與 build。內部測試者必須是具備合資格角色的 App Store Connect 使用者。
6. iPhone 從 App Store 安裝 TestFlight，使用邀請對應的 Apple 帳號開啟邀請並安裝。驗收下方清單後再擴大測試。

自己是帳號持有人時，可先走內部測試。要邀請一般外部使用者或用公開邀請連結，需走外部測試流程；第一個供外部測試的 build 需要 TestFlight App Review，不能承諾立即可安裝。建立 TestFlight 測試不等於正式公開上架。

2026-09-12 已在個人 Apple Developer Program 團隊建立 App 紀錄：

| 項目 | 設定 |
| --- | --- |
| App 名稱 | ビビ學日語 |
| Team ID | `PV3S28HQN7`（yang chen lin） |
| Bundle ID | `tw.learnmore.app` |
| App Store Connect | [6811343218](https://appstoreconnect.apple.com/apps/6811343218) |
| 主要語言／SKU | 繁體中文／`learnmore-ios` |

目前尚未上傳 build 或發送邀請。Xcode 專案已選用上述團隊；Apple 帳號登入與簽章仍需在自己的 Mac／帳號完成。

官方步驟：[TestFlight](https://developer.apple.com/testflight/)、[內部測試者](https://developer.apple.com/help/app-store-connect/test-a-beta-version/add-internal-testers/)、[外部測試者](https://developer.apple.com/help/app-store-connect/test-a-beta-version/invite-external-testers/)。

## Xcode Cloud 建置

首次設定需從 Xcode 的 Product → Xcode Cloud → Create Workflow 開始，連結 `NickYCLin/learnmore` 儲存庫並選取 App scheme。工作流程使用 Xcode 26 以上，以 Release 封存 iOS App；完成首次設定後，才能在 App Store Connect 管理與啟動工作流程。

`ios/App/ci_scripts/ci_post_clone.sh` 會在雲端安裝 Node.js 24、依 lockfile 還原套件、執行單元測試，再建置前端並同步 Capacitor 資源。這一步會補上 Git 未收錄的 `node_modules` 與網頁資源，供後續原生編譯使用。

此腳本尚待真正的 Xcode Cloud 工作流程驗證。GitHub 的模擬器編譯通過不代表簽章或 TestFlight 上傳完成。

官方說明：[首次設定](https://developer.apple.com/documentation/xcode/configuring-your-first-xcode-cloud-workflow)、[自訂建置腳本](https://developer.apple.com/documentation/xcode/writing-custom-build-scripts)。

## 下載建置成品

GitHub Actions 在 PR、main 更新或手動執行後保留以下成品 14 天。請核對 run 的 commit 與各 job 結果，確認下載的是要測試的版本。

| 成品 | 用途 |
| --- | --- |
| `LearnMore-iOS-Simulator` | 內含 `LearnMore-simulator.zip`、commit 與 Xcode 版本，可安裝在相容的 iOS Simulator；無法直接安裝到 iPhone |
| `LearnMore-Backend` | 已通過 .NET 測試與發布檢查的網站檔案，包含 mobile API；不含正式設定、資料庫或上傳內容 |

在安裝了相容 Xcode／iOS Simulator 的 Mac 上，解開兩層 zip 取得 `App.app`，啟動模擬器後可把 `App.app` 拖進模擬器視窗，或執行：

```sh
xcrun simctl install booted /完整路徑/App.app
xcrun simctl launch booted tw.learnmore.app
```

模擬器 App 仍連接正式 mobile API。若服務未部署，清單會顯示連線錯誤；不會自動切換成測試資料。

部署後端成品前，按 [部署文件](../docs/DEPLOYMENT.md)備份並保留站台原設定、媒體與 Data Protection 金鑰。部署完成後，在 `mobile` 執行 `npm run check:backend`，通過後再測試手機登入與收藏。

## 用指令建立 TestFlight 封存

先安裝 Xcode 26 以上，在 Xcode 登入 Apple 開發者帳號，確認 Team 與 Bundle ID 可用。以下會建立已簽章的 archive，並視需要讓 Xcode 更新 provisioning；上傳仍由 Xcode Organizer 執行。

```sh
cd mobile
npm ci
npm run check:backend
LEARNMORE_APPLE_TEAM_ID=你的十碼TeamID LEARNMORE_BUILD_NUMBER=2 npm run ios:archive
```

每次上傳選一個比前次新的正整數 build number。封存位於 `artifacts/ios/LearnMore-<build number>.xcarchive`；腳本不會覆蓋同名封存。Team ID 是公開識別碼，簽章憑證由 Xcode 管理。

## 用 Mac 直接安裝的替代方式

需要可執行 Xcode 26 的 Mac、Node.js 22 以上、Apple 帳號，以及支援目前專案最低版本 iOS 15 的 iPhone。若手機系統比 Xcode 支援版本更新，需先更新 Xcode。

1. 在 Mac 取得這份專案，進入 `mobile`，執行以下指令。

   ```sh
   npm ci
   npm run ios:sync
   npm run ios:open
   ```

2. 在 Xcode 的 Settings → Accounts 登入自己的 Apple 帳號。
3. 選取 App target → Signing & Capabilities，開啟 Automatically manage signing，選自己的 Personal Team 或開發團隊。
4. 確認 Bundle Identifier 可由該團隊使用。預設為 `tw.learnmore.app`；若更換，需同步 `capacitor.config.json`、Xcode 的 Bundle Identifier，以及後端 `mobile-player.js` 的 `widget_referrer`，再重新建置及部署相關後端。
5. 接上並解鎖 iPhone，依系統提示信任電腦；iOS 16 以上依提示開啟 Developer Mode。
6. 在 Xcode 選擇該 iPhone 為執行目標，按 Run。若手機要求信任開發者，依「設定 → 一般 → VPN 與裝置管理」的提示完成。

免費 Apple 帳號可以使用 Personal Team 做個人實機測試，不必先訂閱 Apple Developer Program。Apple 目前規定此類 provisioning profile 有效七天，到期需重新建置、安裝；每個裝置最多安裝三個這類 App。帳號、憑證與私鑰只在自己的 Mac 管理，不放進 Git。

官方說明：[Apple 個人開發帳號](https://developer.apple.com/help/account/basics/about-your-developer-account)、[Capacitor iOS](https://capacitorjs.com/docs/ios)。

## 只有 Windows

Windows 可建置前端，無法直接執行 Xcode。GitHub CI 編譯成功也不會自動產生可點擊安裝的 iPhone App。

可先讓 iPhone 與電腦連同一個可信任的區域網路，在 `mobile` 執行：

```sh
npm ci
npm run dev -- --host 0.0.0.0
```

在 iPhone 的 Safari 開啟 Vite 顯示的 Network 網址。這是版面與操作預覽，並非已安裝的原生 App；目前預覽不提供原生登入返回流程。只在需要時開放私人網路的開發埠，不將 Vite 開發伺服器暴露到公網，結束後按 Ctrl+C 關閉。

若清單載入失敗，先檢查後端 API 是否已部署，不能據此判定是手機畫面壞掉。正式 TestFlight 是另一條散布流程，需要開發者方案及簽章設定。

## 後端先決條件

App 經由 HTTPS API 取得資料，API 再使用伺服器的 SQL Server 設定。手機不保存資料庫帳密，也不直接連 SQL Server。

先完成 [後端部署與 API 驗收](README.md#後端部署)。目前的 UI 測試資料只用於版面檢查，不能視為正式歌曲或實機驗收。收藏會寫入共用後端，測試時使用自己的測試群組。

## 手機驗收

- 搜尋、捲動、橫直向切換、鍵盤與安全區域正常。
- YouTube 影片能播放，歌詞時間、讀音切換及單句重複正常。
- Google 登入能返回 App；取消、登出、重新開啟 App 後行為合理。
- 收藏與網站同步，其他帳號不能讀寫自己的群組。
- 退到背景及鎖定手機時暫停影片，網路中斷時能顯示錯誤並重試。

網站的 mao_pro 外部服務連線問題仍須處理；目前 iOS 第一版沒有嵌入該角色。
