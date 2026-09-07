# LearnMore 產品說明

這裡整理產品用途、現有功能與對應的程式位置。要先看畫面，可以讀 [README](../README.md)。

## 基本資料

| 欄位 | 內容 |
| --- | --- |
| 專案名稱 | LearnMore |
| 網站名稱 | ビビ學日語 |
| 線上網站 | [ビビ學日語](https://magicplus-design.serveirc.com/LearnMore) |
| 產品類型 | 以日文歌曲為素材的網頁學習工具 |
| 主要使用者 | 想透過歌曲練習日文聽力、閱讀與跟唱的人 |
| 介面與翻譯 | 主要使用繁體中文；學習內容以日文歌曲為主 |
| 使用方式 | 透過電腦或手機瀏覽器使用；可自行部署 |
| 原始碼授權 | MIT；第三方資源與歌曲內容另依各自授權 |

**English summary:** LearnMore is a web app for learning Japanese through songs. It combines YouTube playback with synchronized lyrics, furigana, romaji, Traditional Chinese translations, karaoke practice, favorites, and song groups. It also provides song and lyric editing workflows and optional audio-processing services.

## 典型使用流程

搜尋或挑選歌曲 → 播放影片與同步歌詞 → 對照讀音和翻譯 → 逐句或卡拉 OK 練習 → 收藏或整理歌曲群組。

具備管理權限的使用者另可建立歌曲、編修歌詞與時間軸、檢查辨識狀態及處理審核佇列。

## 功能與程式依據

| 功能 | 使用者能做什麼 | 程式入口 | 條件 |
| --- | --- | --- | --- |
| 搜尋與演唱者合輯 | 找歌、瀏覽排行及演唱者歌曲 | [HomeController](../LearnMore/Controllers/HomeController.cs) | 需要歌曲資料 |
| 同步歌詞 | 依播放時間閱讀歌詞 | [LyricsController](../LearnMore/Controllers/LyricsController.cs)、[歌詞頁](../LearnMore/Views/Lyrics/Index.cshtml) | 需要影片與歌詞時間軸 |
| 日文讀音 | 查看漢字注音與羅馬拼音 | [JapaneseRubyGeneratorService](../LearnMore/Services/JapaneseRubyGeneratorService.cs) | 依字典、工具與既有資料產生，仍可人工校正 |
| 卡拉 OK 與逐句練習 | 切換練習模式及可用音源 | [歌詞頁](../LearnMore/Views/Lyrics/Index.cshtml)、[卡拉 OK 測試](../LearnMore.Tests/GroupPlayerKaraokeAudioSurfaceTests.cs) | 人聲／伴奏切換需要對應音軌 |
| 收藏與歌曲群組 | 保存歌曲並連續播放 | [SongGroupController](../LearnMore/Controllers/API/SongGroupController.cs)、[GroupPlayerController](../LearnMore/Controllers/GroupPlayerController.cs) | 個人收藏與群組管理需要登入 |
| 歌曲建立與歌詞校正 | 編修內容、重排歌詞、修改時間軸、處理審核 | [MediaController](../LearnMore/Controllers/MediaController.cs) | 需要相應權限 |
| 高精度辨識與音軌分離 | 由背景或遠端服務處理音訊 | [VocalOnsetDetectionService](../LearnMore/Services/VocalOnsetDetectionService.cs)、[LearnMoreAPI](../LearnMoreAPI/learnmore_api/main.py) | 須安裝工具、準備模型並啟用設定 |
| Mika 角色整合 | 歌詞頁嵌入角色及歌曲動作 | [角色面板](../LearnMore/Views/Shared/_MikaAvatarPanel.cshtml)、[前端控制](../LearnMore/wwwroot/js/mika-avatar.js) | 依賴外部 Mika 服務；可用角色依整合契約決定 |

## 使用條件

- 歌詞、翻譯、時間軸與音軌的完整程度依歌曲資料而異，自動辨識結果可進入人工校正流程。
- 人聲／伴奏分離與遠端高精度 API 在共用範本中預設關閉，需要部署者另行設定。
- Mika 整合已有程式，但未完成 LearnMore 驗收的正式角色不列為已交付功能；目前選項受服務契約與前端過濾控制。
- 倉庫包含程式、測試與去除環境專用資訊的維運工具，不包含完整正式資料庫、會員資料、歌曲媒體、API Key 或部署憑證。
- 實際可用功能以站台設定為準，外部服務需要另外確認。

## 技術與閱讀順序

網站使用 .NET 8、ASP.NET Core MVC、Razor、JavaScript 與 SQL Server／ADO.NET。遠端工作服務使用 Python／FastAPI；語音、音訊及日文處理依賴外部工具、模型與字典。

1. [README](../README.md)：產品用途與使用畫面。
2. [ARCHITECTURE](ARCHITECTURE.md)：模組關係與資料流程。
3. [Program.cs](../LearnMore/Program.cs)：設定載入、DI、路由與背景服務。
4. 上表的 Controller／Service，以及 [LearnMore.Tests](../LearnMore.Tests) 和 [API 測試](../LearnMoreAPI/tests/test_api.py)。
5. [DEPLOYMENT](DEPLOYMENT.md)：建置、正式設定移轉及雙儲存庫同步。
