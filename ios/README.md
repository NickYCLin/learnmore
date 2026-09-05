# LearnMore iOS

以 App Store 發佈為目標的 SwiftUI 客戶端，最低 iOS 17，與網站共用 .NET 8／SQL Server。
**目前已實作會員與收藏等送審功能，但尚未完成正式部署、真機整合或簽章，不能宣稱可送審。**

## 已實作

- 原生歌曲清單、搜尋、分頁、重新整理、錯誤重試與分享。
- YouTube 官方播放器與同步歌詞，逐句定位、中文／拼音／自動捲動設定；離開或背景化時暫停。
- Google 與 Apple 登入、明確連結登入方式、Keychain session、伺服器撤銷登入。
- 網站共用的收藏與歌單：讀取、建立、加入／移除歌曲及刪除歌單。
- App 內帳號刪除與重新驗證：清除會員、收藏、留言、回報與本人上傳內容，Apple token 撤銷、檔案清理 outbox。
- 獨立隱私政策／支援頁、Privacy Manifest、App icon、商店文案及發佈檢查腳本。

## 開發

1. Xcode 開啟 `ios/LearnMore.xcodeproj`，選擇 `LearnMore` scheme。
2. 複製 `Config/Local.example.xcconfig` 為 `Config/Local.xcconfig`，填正式 Bundle ID、Apple Team ID、Google iOS／Web client ID 與反轉 callback scheme；不填伺服器 secrets。
3. `LearnMore/Info.plist` 的 `LearnMoreServerURL` 預設正式站，保留 HTTPS、`/LearnMore/` 子路徑與尾端斜線。
4. 按 [部署說明](AppStore/DEPLOYMENT.md) 在 staging 檢查 schema、執行 additive migration、設定 OAuth 與公開支援資訊，驗證後再上正式站。
5. Xcode 16.4 可作本機編譯驗證；依 Apple 2026-04-28 起的要求，上傳 App Store 必須 Xcode 26／iOS 26 SDK 以上。

```sh
xcodebuild -project ios/LearnMore.xcodeproj -scheme LearnMore \
  -sdk iphonesimulator -destination 'generic/platform=iOS Simulator' \
  -derivedDataPath /tmp/learnmore-ios-build -jobs 2 CODE_SIGNING_ALLOWED=NO build
swift test --package-path ios/PlaybackCore
dotnet test LearnMore.sln
# 使用 xcrun simctl list devices available 取得模擬器 ID：
xcodebuild -project ios/LearnMore.xcodeproj -scheme LearnMore \
  -destination 'platform=iOS Simulator,id=SIMULATOR_ID' \
  -derivedDataPath /tmp/learnmore-ios-build -parallel-testing-enabled NO \
  -jobs 2 CODE_SIGNING_ALLOWED=NO test
python3 ios/scripts/preflight.py
```

UI test fixtures 僅編入 Debug 且需 `--ui-testing` 參數，Release 不包含測試資料；正式服務失敗時顯示錯誤，不回退假資料。UI 測試截圖不能用於 App Store。

## 發佈追蹤

- [驗證紀錄與真機矩陣](AppStore/VALIDATION.md)
- [後端／OAuth／刪除／發佈設定](AppStore/DEPLOYMENT.md)
- [商店文案與截圖計畫](AppStore/zh-Hant.md)
- [隱私資料盤點](AppStore/PRIVACY-INVENTORY.md)
- [待完成條件](AppStore/release-status.json)
- [圖示來源與產生提示](AppStore/ICON.md)

`preflight.py --stage archive` 只檢查封存前置條件；預設模式另檢查正式 API、公開政策頁、內容權利、TestFlight、截圖與 App Store 資料。沒有完成的條件保持 false，不能僅為讓腳本通過而改值。

本機使用的 `com.learnmore.ios` 是開發預設，尚未註冊。API 尚未部署，Google／Apple 公開 ID、伺服器 secrets、開發者簽章、支援資料、內容使用權證明與營運設定仍待提供／確認。
