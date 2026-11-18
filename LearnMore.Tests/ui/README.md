# 播放器版面回歸檢查

在儲存庫根目錄執行：

```sh
LEARNMORE_UI_FIXTURES=/tmp/learnmore-ui-fixtures \
  dotnet test LearnMore.Tests/LearnMore.Tests.csproj \
  --filter FullyQualifiedName~PlayerLayoutTests
npm install --prefix /tmp/learnmore-ui-tools --no-save puppeteer-core
NODE_PATH=/tmp/learnmore-ui-tools/node_modules \
LEARNMORE_UI_FIXTURES=/tmp/learnmore-ui-fixtures \
  node LearnMore.Tests/ui/player-layout.cjs
```

預設使用 `/usr/bin/google-chrome`，可透過 `CHROME_PATH` 指定其他 Chrome。
Puppeteer 只安裝在暫存目錄，不是專案依賴。

`PlayerLayoutTests` 以獨立 MVC host 編譯完整 Razor 頁面，驗證
`/LearnMore` 路徑、資源版本與頁面限定控制項，不執行正式 `Program` 或連線資料庫。
瀏覽器測試使用這些合成頁面及專案 CSS/JS，攔截全部外部網路；
YouTube IFrame API、收藏群組、分享與剪貼簿皆為本機替身，沒有真實帳號或歌曲資料。

涵蓋手機／平板／桌面斷點、固定導覽列與播放器尺寸、模式橫向列、練習收合、
逐行觸控、搜尋、Mika、收藏新增／移除／建群組、分享、合輯循環與背景暫停，
以及首頁預覽斷點。截圖輸出至 fixtures 目錄下的 `screenshots/`。

這些測試不等於實際 YouTube 播放、行動裝置 Wake Lock、iOS 安全區域或登入服務驗收；
外部 CDN 字型／圖示也不會載入。正式裝置驗收需另行執行。
