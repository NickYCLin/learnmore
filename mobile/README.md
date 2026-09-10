# LearnMore iOS 第一版

目前目標是先用 TestFlight 邀請安裝到自己的 iPhone 試用，再逐步增加功能。操作步驟與帳號條件見 [iPhone 個人試用](DEVICE_TESTING.md)，素材查核見 [內容來源與授權](../docs/CONTENT_RIGHTS.md)。App Store 上架暫緩。

使用 Capacitor 包裝本機前端資源，透過 HTTPS 呼叫既有 ASP.NET Core 後端；SQL Server 連線資訊只存在伺服器。

目前包含歌曲搜尋、分頁、YouTube 播放、同步歌詞／注音／羅馬拼音、單句重複，以及登入後的歌曲群組收藏。網站與 App 共用 `Songs`、各歌曲的歌詞表、`SongGroup`、`SongGroupMapping` 和 `Users`。

## 開發與建置

需要 Node.js 22 以上。iOS 編譯需要 Mac 與 Xcode 26 以上；Windows 可以檢查 TypeScript、建置前端與同步 Xcode 專案，無法產生已驗證的 iOS 安裝包。

```sh
cd mobile
npm ci
npm test
npm run build
npm run ios:sync
# 以下在 Mac 執行
npm run ios:open
```

Xcode 選擇開發團隊、確認 Bundle ID `tw.learnmore.app` 的可用性，再選擇 iPhone 執行。正式簽章、Apple 憑證與 provisioning profile 不放進 Git。若變更 Bundle ID，也要同步後端 `mobile-player.js` 的 `widget_referrer`。

前端預覽：`npm run dev`。Vite 將 `/backend` 代理到既有站台的 `/LearnMore`；原生 App 使用 Capacitor 原生 HTTP 呼叫正式 API，不需開放任意 CORS。預覽的登入功能僅在 iOS 提供，前端沒有測試帳密或繞過驗證的入口。

後端地址目前集中於 `src/main.ts`、`vite.config.js` 與 `index.html` 的 CSP。切換環境需一起修改並重新打包。播放器本身是後端的 `/Mobile/Player` 頁面，以 HTTPS 來源嵌入 YouTube，避免本機 `capacitor://` 頁面缺少 HTTP Referer。此頁只接收指定來源與父視窗的播放指令，沒有帳號資料或原生 bridge。

## 後端部署

先依 [部署文件](../docs/DEPLOYMENT.md) 更新網站，再確認：

- `GET /LearnMore/api/mobile/v1/status` 回傳版本 1。
- `GET /LearnMore/api/mobile/v1/songs` 回傳歌曲清單。
- `GET /LearnMore/api/mobile/v1/songs/{songUid}` 回傳歌曲與歌詞。
- 未登入呼叫 `GET /LearnMore/api/mobile/v1/groups` 回傳 401。

不新增資料表，不將資料庫埠開放給 App。私有資料 API 只接受後端簽發的 Bearer 工作階段，不接受 App 指定使用者 ID。

## 登入與收藏

1. App 產生隨機 state 與 PKCE S256 verifier，開啟系統瀏覽器的 `/Mobile/Connect`。
2. 使用者沿用網站 Google 登入，明確允許 App 連接帳號。
3. 後端回傳有效兩分鐘、只能兌換一次的 code，App 驗證 state 後以 verifier 兌換工作階段。
4. 工作階段有效八小時，登出時立即撤銷。第一版的憑證只存 App 記憶體；關閉 App、後端重啟或到期時需重新登入。

目前工作階段及 code 存在單一後端程序的記憶體快取。多台後端或 IIS web garden 必須先換成共用儲存及原子兌換，不能直接增加 worker process。

收藏沿用網站「加入歌曲群組」的定義。App 只新增／移除使用者選擇的群組關聯，不會一次清掉其他群組中的同一首歌。

## 實機與上架前驗收

- 使用真實 iPhone 測試 Google 登入返回、取消／過期登入、登出、鎖定畫面與切換 App。
- 確認正式網域、YouTube iframe 識別資訊、播放限制、歌詞時序與末句重複；手機退到背景會暫停播放。
- 確認 App 收藏在網站可見，其他帳號無法讀寫該群組。
- 完成 Apple 要求的登入選項、App 內帳號刪除、隱私權揭露、正式圖示及素材權利確認，再準備 App Store 送審。第一版沒有接入網站金流、音訊下載或伴奏分離。

`npm run build` 成功只代表前端可建置；Xcode 編譯、iPhone 實測、TestFlight 與 App Store 審核是不同驗收階段。

官方參考：[Capacitor iOS](https://capacitorjs.com/docs/ios)、[YouTube 嵌入播放器識別](https://developers.google.com/youtube/terms/required-minimum-functionality#api-client-identity-and-credentials)、[Apple 審核規範](https://developer.apple.com/app-store/review/guidelines/)。
