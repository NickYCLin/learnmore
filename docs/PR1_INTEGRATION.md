# PR1 保留與整合紀錄

核對日期：2026-10-15。此文件記錄原始碼整合，不代表正式部署或送審驗收。

## 本輪共同修正

PR2 的共同修正已透過合併提交 `eded4ffd0917dc84051336b70ca53a328acd7256`
進入 GitHub main；PR2 最終 head 為 `02838cd54e5a887f746b5f319f3a7b23ee9b70fc`。

- Google 登入等待非同步驗證，拒絕空白 credential、CSRF 與 client ID，檢查 EmailVerified。
- 保留 MobileLoginReturnUrl，只允許站內 LocalRedirect，消耗返回意圖後移除 Session 值。
- 音軌處理器先在交易中鎖定並確認歌曲存在，再複製公開檔案與登記結果。
- .NET 559 項測試通過；Windows CI 38532973182 與 iOS CI 38532973190 成功。
- OAuth 使用 fake 驗證依賴；未執行真實 OAuth、SQL Server 並行刪除或音樂分離。

PR1 保留原 identity linking、帳號刪除與 file-deletion-outbox，沒有直接換成 PR2 的整個登入或音軌 Service。
相容整合保留 PR1 的帳號原始碼，以及 main 已有的登入返回與先檢查再發布流程。
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

## 已批准的相容整合

使用者已批准本輪採 Capacitor、保留 SwiftUI，以及另設分頁相容路徑。

- main 的 `GET /api/mobile/v1/songs` 保留陣列回應；SwiftUI 的分頁與歌詞請求移至
  `/api/mobile/v2/songs`，Swift 套件提供共用路徑，preflight 同步更新。
- MVC action discovery 測試檢查兩路 mobile API 的 HTTP method／route 不重複。
- MobileController 同時保留 Connect／PKCE／Player 與 Privacy／Support。
- Program 保留兩路 DI、rate limiter 與 security headers；原生例外 JSON 只作用於
  v2 catalog 與 v1 auth／account／favorites，不攔截 Capacitor v1 session／songs／groups。
- Capacitor 的 8 小時記憶體 session 與 SwiftUI 的 30 天 hashed session／Keychain
  仍獨立。下列隔離 SQL 測試涵蓋合成 schema，並未檢查正式資料庫或真實帳號。
- 保留 `.github/workflows/mobile.yml` 的 Capacitor 檢查與
  `.github/workflows/ios.yml` 的 SwiftUI 檢查；兩套 iOS 原始碼不互相替換。
- Google 登入保留 main 的驗證 seam、CSRF、設定注入、EmailVerified 與 LocalRedirect；
  PR1 已連結 provider subject 的保護邏輯一併保留。
- 音軌發布先鎖定存在的歌曲，再複製公開檔案；不存在時仍保留 PR1 的 durable cleanup。

## 尚未開放的帳號操作

`MobileAccountMutationGate` 在 controller 與 store 的 link／delete 入口均固定回應 503，
沒有可直接解鎖的設定旗標；原本的刪除、revoke、outbox 與 SQL migration 原始碼均保留。
`auth/providers` 明確回應 `accountMutations: false`。不能把原生 UI 中的按鈕當成已驗收功能。

既有 email 不能用來自動建立 provider ownership，包含已驗證的 Gmail／Workspace email。
既有網站帳號若尚無 provider identity，原生登入將回應 409；需另做以原帳號新鮮驗證
引導的 migration／link 流程，不自行重新命名或合併帳號。

解除阻礙仍需要原帳號重新驗證與目標 provider 驗證的實際 OAuth／UI 流程，以及
legacy identity migration。伺服器現在要求兩份 proof 的已驗證 `auth_time` 在五分鐘內；
缺少該 claim、過期、超過三十秒的未來時間或原 provider 不屬於目前帳號都拒絕。
不能用 token 的 `iat` 或同 email 代替重新驗證。SwiftUI 不會送出舊版單 proof link。

Apple revoke 仍在 SQL transaction 中，缺少 durable revoke／retry 狀態；SQL rollback
不能撤回成功的外部 HTTP 請求。因此本輪不開 gate，也不能把停用按鈕當作功能已完成。
帳號 UI、網站隱私頁與商店文案明示限制，登出或聯絡支援不代表資料已刪除。

先前本機 .NET Release 測試 602 項通過，0 失敗、0 跳過。測試包含實際 MVC action discovery、
兩層 mutation gate、email 拒絕，以及合成檔案／outbox acknowledgement 重試與 late-worker。
這些是隔離單元與檔案測試；SQL 鎖定 source 檢查不是 SQL Server 並行驗證，fake OAuth
不是真實 Google／Apple 登入。最終 CI 與合併狀態以 GitHub 的實際 head/run 為準。

Apple 匯出日誌、真機音訊中斷／耳機／自動鎖定與 TestFlight 均仍待驗證。
診斷功能預設關閉，沒有新增正式曲庫內容、簽署憑證或部署操作。

## 2026-10-15 隔離驗證與尚未提交範圍

本輪在既有 `/tmp/learnmore-audit/pr1` 快照修改，未寫入 GitLab checkout，沒有 clone。
遠端同步使用 GitHub MCP 讀取 main／PR1 的 SHA；不是 `git pull`。
main 為 `eded4ffd0917dc84051336b70ca53a328acd7256`，PR1 head 為
`2ccf024da439eabb6ef482fe011254b3bb8b9e86`。下列結果屬本機快照，不是最終提交 SHA 驗證。

- 完整 .NET Release 重編譯測試 617 項通過，0 失敗、0 跳過。
- 8 項 SQL integration tests 使用本機既有 SQL Server 2022 Developer image，
  新容器僅綁 `127.0.0.1:15439`，每項建立／刪除獨立合成 DB，不連正式 DB。
  migration 跑兩次、preflight、FK rollback／刪除重試、雙 proof ownership／過期拒絕、
  Capacitor 多 token／pending code、Swift SQL session、web active／persistent cookie
  與同 email 新帳號，以及 outbox failure／retry、late-worker 交錯均在真 SQL engine 執行。
  同一組 SQL 測試另外連續重跑五輪，每輪 8 項全數通過。
- 測試以 reflection 呼叫固定 gate 後方的 private core；沒有開放正式 link／delete endpoint。
  OAuth verifier 拒絕所有 network／Apple revoke。這不是 Apple revoke 或真登入驗收。
- SQL DMV 實測發現發布與刪除跨索引鎖定造成 deadlock 1205；兩路現在於其他 SQL 鎖之前
  取得 transaction-owned `LearnMore.AudioStemPublication` exclusive app lock。
  鎖定採全曲庫序列化，降低並行吞吐以避免反向鎖定；十五秒逾時拒絕，不重試發布。
  保證只涵蓋遵守同協定的程式路徑，不保證未知直接 SQL writer、檔案／SQL 原子交易。
- Capacitor Read／code exchange 與網站 cookie restore／renew 每次檢查 user Id 存活；
  網站另檢查 email，刪除後新帳號重用 email 不能恢復舊 session。DB 失敗不回退 cache。
- mobile Node 49 pass／1 skip（本機無 Swift），WebKit 9 pass／1 conditional skip，build 成功。
  診斷旗標 on／off 的 WebKit 測試 2 pass／2 conditional skip；Python API 55 pass，publish 成功。
  SwiftUI member restriction UI test 已加入，但本機無 macOS／Xcode，尚未編譯與執行。
- CI 保留 Windows／API、Capacitor 與 SwiftUI 兩路，另加合成 SQL audit job，沒有正式 DB secrets。
  本輪修改尚未取得最終遠端 SHA，因此沒有本輪 CI 通過或 main 包含修改的證據。

GitHub MCP 傳輸間歇失敗，部分 blob／小型 tree 寫入成功，但後續批次 tree 與 blob
又出現 transport error。遠端 readback 確認仍有必要 blob 不存在，尚不能建立完整提交。
提交前須以本機 blob hash 與遠端 tree 逐檔核對；推送前重新讀取 refs，使用
`expected_sha`／`force:false`，不改用 Git／REST 寫入、不 force push、不重寫原九筆歷史。
本輪尚無新 commit／分支 ref 更新／最終 SHA CI，不把已上傳物件當成已推送的程式碼。
安全功能阻礙未解除之前，PR1 必須保持 Draft／open，不合併 main。
