# 原生背景音訊診斷原型

這不是正式歌曲背景播放功能。正式開放授權歌曲 **0 首**、歌詞／翻譯 **0 首**。
既有 YouTube 歌曲仍只在前景播放，App 進入背景會 pause；沒有抽音或繞過背景限制。
本原型不下載外部音樂、不串流、不上傳、不呼叫新音樂 API。

## 預設關閉

只有 `VITE_NATIVE_AUDIO_DIAGNOSTIC=1 npm run ios:sync` 會在網頁顯示診斷 UI，
並在同步資源中加入 `native-audio-diagnostic.json`，原生 bridge 也核對此建置資源。
一般 `npm run ios:sync` 不含啟用資源，原生呼叫拒絕 `DISABLED`。
不要將診斷建置當成正式曲庫發布。`UIBackgroundModes=audio` 是背景音訊執行模式，
不新增麥克風、媒體庫或其他隱私權授權。

## 唯一測試訊號

bridge 只接受 `tone-220-30s`，不接受任何 URL。Swift 在 App 暫存目錄自行產生
30 秒、220 Hz、單聲道 PCM WAV；振幅 0.025、頭尾淡入淡出，AVPlayer 音量 0.3。
仍請先降低裝置音量；不是歌曲，也不附歌詞。停止會釋放 player／observer／remote
command targets、清除 Now Playing 與暫存訊號，沒有 loop、靜音保活或網路入口。

播放由 AVPlayer 與 AVAudioSession playback 管理；控制中心支援 play/pause/seek。
原生時間、rate 和 revision 是 UI 的唯一進度來源，前景恢復會 getState，
不以 JS 計時推估背景進度。進入診斷先移除 YouTube player，開啟歌曲先等待原生 stop。
中斷只在原本確實播放、使用者仍有播放意圖且系統 shouldResume 時恢復；使用者暫停、
耳機拔除或停止會撤銷恢復意圖。暫停／結束停用 audio session，不播放靜音維持背景。

## 驗證邊界

- `npm test`：既有時序測試與診斷控制器 mock 測試；原生 wiring 檢查是靜態檢查，不是 Swift 執行。
- `npm run test:ui`：既有 WebKit；`npx playwright test --config playwright.diagnostic.config.mjs`：旗標 off/on。
- `npm run build` 與旗標 on build，核對啟用資源；`npm run ios:sync` 同步既有 target。
- Linux 沒有 Swift/Xcode，不能證實 native 編譯或真機背景播放。既有 GitHub iOS CI
  可做 simulator compile，但不能代替鎖屏、控制中心、來電中斷、耳機拔除的真機驗收。
- 真機需另驗證：有限訊號結束、鎖屏 seek/rate、pause 後無保活、前景恢復進度、
  中斷允許／不允許恢復、使用者於中斷期間 pause、切歌時序、拔耳機、停止後清理。
- Apple AdHoc/Development export 警告與 ASC 原始日誌存取阻礙未處理；保持 PR draft，
  不手動觸發 TestFlight、簽章變更、送審或部署。

## 後續合法音源

使用者沒有訂閱或自有授權音源，且明確不下載音樂。本輪不接 Jamendo、不建帳號/token。
Jamendo tracks 要求 client_id，audio 與 audiodownload 是不同欄位；免費 API 僅供非商用，
LearnMore 的商用身份尚未確認。未來逐首驗證來源、CC0/CC BY、署名與串流條款；
歌詞與翻譯權利另查，不以 YouTube 歌單替代授權曲庫或使用未核實授權 stems。
