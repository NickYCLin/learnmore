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

## 帳號安全與驗收界線（2026-10-15 續作）

本次已移除固定 503 gate；link / delete 會執行實際的 proof、ownership 與 SQL 流程。
連結要求原帳號與目標 provider 各一份已驗證的 proof，auth_time 限五分鐘內、未來容許
三十秒；沒有 auth_time 就拒絕，不用 iat、電子郵件或 UI 取得授權的時間代替。
SwiftUI 先取得原帳號授權，再取得目標授權，一起交給伺服器驗證；關閉或失敗即清除。
原生 SDK 是否實際提供新鮮 auth_time 尚無真 OAuth 證據，不能聲稱實際帳號流程已驗收。

刪除交易只寫入本地 SQL 與 outbox，不做外部 revoke。Apple 舊、新 refresh token 均由
Data Protection 加密後入列，與資料刪除同時提交；SQL rollback 不會造成外部授權先失效。
worker 以 READ COMMITTED 交易領取五分鐘租約，再在交易外呼叫 Apple；成功後依租約
刪除工作，失敗保留、最長每六十分鐘重試，不記錄 token 或外部錯誤內容。
中斷或成功後 acknowledgement 遺失會在租約到期後重做，屬 at-least-once 而非 exactly-once。
尚有撤銷工作時禁止相同 Apple subject 重新建立帳號或連結，避免舊工作影響新授權。
HTTP 202 表示 LearnMore 資料已提交刪除、背景清理已排入，不代表 Apple 已完成撤銷。
佇列不依賴被刪除的 Users FK；正式環境必須保留既有 Data Protection key ring 才能重試。
沒有設定 Apple 撤銷憑證、key 遺失或外部長期失敗時工作會保留，需營運處理，不能假報完成。

Capacitor code exchange / Read 與網站 restore / renew 每次檢查 user Id 存活；網站另比對
email。刪除後不能恢復舊 persistent cookie 或快取 token，DB 失敗也不退回 cache。
SwiftUI SQL session 透過 Users join 失效；合成 SQL 測試涵蓋多裝置與同 email 新帳號。

## 舊帳號遷移

沒有 provider subject 的舊網站帳號不能以相同 email 自動接管。MobileAuth 開啟時網站
也拒絕既有帳號的未連結 Google subject；新建立的網站帳號在同一交易寫入 verified subject。
支援人員必須先以既有可信紀錄獨立確認原帳號所有權，並確認目標 provider 的穩定 subject。
只有 email 相同、收到 email 截圖或使用者自行聲稱擁有帳號都不足以核准。
確認後才可由有權限的管理者在資料庫使用 docs/sql/002-reviewed-legacy-identity.sql，
填入確切 UserId、provider、subject 及可追溯的證據編號；預設空值會拒絕執行。
核准只存活十分鐘，原生重新驗證後才能在交易中消耗一次，保留原 Id / 收藏 / 歌曲。
過期、缺少 auth_time、已連結或 subject 不相符都拒絕，不覆寫任何既有 provider identity。
本次未操作正式資料庫或核准任何真實帳號；無獨立所有權證據的舊帳號仍需人工處理。

## 隔離驗證

使用既有 SQL Server 2022 Developer image，新容器只綁 127.0.0.1:15439，
每項測試建立與刪除專屬 LearnMoreAudit_ 合成 DB，不讀寫正式資料。
SQL 測試直接執行公開 store 方法，另有 controller + fake verifier + 真 SQL 測試，
不再以 reflection 繞過 gate。涵蓋 schema / FK / migration 重入、跨帳號與過期 proof、
多 session 撤銷、outbox rollback / retry / restart、租約交錯與 late-worker / delete 阻塞。
OAuth 隔離測試用臨時合成簽章與攔截 HTTP，測試真 verifier 的 auth_time、code replay
及 revoke failure；不是真實 Google / Apple OAuth，也沒有建立 Apple 開發者簽署憑證。
重複交換測試發現短生命週期 ECDsa 被 JWT signature provider cache 重用後已釋放；
Apple client secret 現在關閉該 key 的簽章 provider 快取，實際重複簽章與撤銷測試通過。

本輪重新編譯 .NET Release：624 pass / 0 fail / 0 skip，包含 12 項真 SQL 測試與
3 項簽章 OAuth 隔離案例；不是前輪 617 項 binary 的重跑。
Capacitor Node：49 pass / 1 skip（本機無 Swift）；WebKit：9 pass / 1 conditional skip；
TypeScript / Vite build 通過。Python API：55 pass。SQL 另重跑五輪與 publish 結果保存在
本機 audit 證據中；這些測試不冒充真實 OAuth 或真機結果。

SQL DMV 先前發現發布與刪除跨索引鎖定的 deadlock 1205。兩路現在先取得交易專屬
LearnMore.AudioStemPublication app lock；十五秒逾時拒絕，以全曲庫序列化換取鎖順序一致。
這不保證直接 SQL writer 或檔案與 SQL 的原子回滾。
本輪 SQL 實測另發現 pooled connection 保留 SERIALIZABLE 造成 READPAST 拒絕；
撤銷 worker 現在明確用 READ COMMITTED 短交易領取租約，提交後才進行外部 HTTP。

## 同步與交付

已在既有 /tmp/learnmore-audit/pr1 快照內初始化 Git metadata 並 fetch GitHub 歷史，
不是 clone，也未寫入原 GitLab checkout。這是使用者續作時新增的正常 Git 授權，
取代前輪 MCP-only 限制。部分歷史 MCP blob / tree 未曾更新任何 branch，不當成已提交。
整合提交保留 PR1 與 main 兩個 parent，原九筆歷史不 squash、不重寫，推送前會實際 pull。

CI 保留 Windows / API、Capacitor、SwiftUI，另有獨立合成 SQL job；兩路 iOS 也在 PR 分支
push 執行，避免僅因 path filter 而缺少最終 head 的驗證。只做無簽章 simulator build，
沒有 TestFlight、正式部署、Apple 帳號操作、新權限或付費 runner。
最終測試數、提交 SHA、CI run 與 readback 以本輪實測紀錄與完成回報為準，不沿用舊 binary。
尚缺真 OAuth / 原生重新驗證與舊帳號人工所有權證據、原始 Apple export 日誌、
真機 30 / 60 秒鎖定、耳機與中斷驗收。這些限制存在時 PR 保持 open，不因 CI 綠就宣稱交付完成。
