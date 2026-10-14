# PR1 保留與整合紀錄

核對日期：2026-10-14。此文件記錄原始碼整合，不代表正式部署或送審驗收。

## 本輪共同修正

PR2 的共同修正已透過合併提交 `eded4ffd0917dc84051336b70ca53a328acd7256`
進入 GitHub main；PR2 最終 head 為 `02838cd54e5a887f746b5f319f3a7b23ee9b70fc`。

- Google 登入等待非同步驗證，拒絕空白 credential、CSRF 與 client ID，檢查 EmailVerified。
- 保留 MobileLoginReturnUrl，只允許站內 LocalRedirect，消耗返回意圖後移除 Session 值。
- 音軌處理器先在交易中鎖定並確認歌曲存在，再複製公開檔案與登記結果。
- .NET 559 項測試通過；Windows CI 38532973182 與 iOS CI 38532973190 成功。
- OAuth 使用 fake 驗證依賴；未執行真實 OAuth、SQL Server 並行刪除或音樂分離。

PR1 保留原 identity linking、帳號刪除與 file-deletion-outbox，沒有直接換成 PR2 的整個登入或音軌 Service。
未來合併仍需保留 PR1 的帳號語意，以及 main 已有的登入返回與先檢查再發布流程。
檔案複製和 SQL 並非同一種交易；部分複製或 SQL 登記失敗的清理，不在本輪保證範圍。

## 九筆獨有歷史提交

原 PR1 head 為 `2d6b6041a49f704ee7838f2af1e84d6e41a49852`，共同祖先為
`ff06366a837f6d7d5734736fd2685827ac134761`。本輪採追加提交，沒有重寫下列歷史。

| 提交 | 分類與保留範圍 |
| --- | --- |
| fe8ac46f8e3dad66c6315083f0868472d8c76b36 | SwiftUI、共用會員收藏、登入與發佈基礎；API／帳號語意需整組驗證 |
| a9a5d3aaa7eb75908380a004f4540e1022ad439b | API 過期失敗與重新登入行為；保留原生客戶端處理 |
| 9c913b5e9376339b2d98c5fad4dffc0d813d1b5d | UI 驗證產物與 late-worker 清理；不能只靠完成後清理取代發布前防護 |
| 624ccc2f6ac334084343a758f6ef79fb6b020cc1 | Debug 與 Swift 套件架構一致性；不涉及客戶端路線選擇 |
| 5aa4018a8dee02118edad6bef680f5967b845296 | 有界模擬器 CI 與發佈證據；須與 Capacitor workflow 分開保留 |
| be3f37e8366ec285e6b1316f1fecf004ab57c655 | 無影片歌曲的歌詞閱讀；保留 SwiftUI 功能修正 |
| 3f48d7171713718128de9d5e4dcf9c49f49a396e | 閱讀設定 UI 行為；保留真實互動驗證 |
| 0b9921a04f82f95f499734c318ae967b42ffc7cb | iOS 巢狀 Switch 的互動測試修正 |
| 2d6b6041a49f704ee7838f2af1e84d6e41a49852 | 當時驗證紀錄；歷史證據不等於本輪真機或正式服務驗收 |

## 可獨立完成的技術整理

- 根目錄 .gitignore 與 PR2 一致；SwiftUI 特有的 DerivedData、Local.xcconfig、xcuserstate 移到 ios/.gitignore，兩路原始檔均保留。
- LearnMore/LearnMore.csproj 保留 PR1 JWT 依賴，納入 main 的本機設定不發布規則，以及 node_modules Content Include。
- 對共同祖先與 PR2 做三方文字檢查，上述 .gitignore 與 csproj 均無衝突。
- PR1 本輪本機完整 .NET 測試 546 項通過，0 失敗、0 跳過。

## 仍不能直接合併的範圍

1. `LearnMore/Controllers/API/MobileApiController.cs`（main）與
   `LearnMore/Controllers/API/MobileCatalogController.cs`（PR1）同時宣告
   `GET /api/mobile/v1/songs`。前者回傳陣列，後者回傳 `{songs, hasMore}`。
   不能同時啟用，也不能擅自刪除其中一個。最小待決事項：是否維持既有陣列端點，
   將 SwiftUI 分頁契約放在另一個版本／路徑；或授權兩客戶端一起遷移到同一契約。
2. `LearnMore/Controllers/MobileController.cs` 必須同時保留 main 的
   Connect／PKCE 回呼與 PR1 的 Privacy／Support 頁，不能整檔二選一。
3. `LearnMore/Program.cs` 必須整合兩套 DI 與例外處理，並驗證 main 的
   8 小時記憶體 token 與 PR1 的 30 天 hashed session／Keychain，不默默互換。
4. `LearnMore/Controllers/LoginController.cs` 的 identity linking、重新驗證、
   `docs/sql` migration、帳號刪除、Apple revoke 與 outbox，仍缺相容 schema 和
   真實 SQL 並行驗證。本輪沒有把這些破壞性流程移植至 main。
5. `README.md` 需保留 main 的網站／部署文件，以及 PR1 的 SwiftUI 驗證說明；
   `.github/workflows/ios.yml` 需保留兩路建置，不得用其中一路取代另一路。

PR1 維持開啟、Draft；不為清理清單而關閉，也不把未決契約硬合入 main。
Apple 匯出日誌、真機音訊中斷／耳機／自動鎖定與 TestFlight 均仍待驗證。
診斷功能預設關閉，沒有新增正式曲庫內容、簽署憑證或部署操作。
