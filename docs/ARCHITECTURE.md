# LearnMore 程式架構與閱讀入口

這份文件提供工程師、維護者與程式碼分析工具一條可重複的閱讀路徑。先從實際請求入口找到 Controller，再沿著介面、Service、資料存取與測試追蹤，不需要只靠檔名猜測功能。

## 系統輪廓

LearnMore 是 .NET 8 的 ASP.NET Core MVC 應用程式。頁面由 Razor Views 輸出，主要資料透過 ADO.NET 存取 SQL Server；字幕、歌詞、日文讀音、語音辨識與音軌分離則由獨立 Service 與背景工作處理。

```text
Browser
  ├─ MVC Controller ── Razor View
  └─ API Controller
          │
          ▼
      Application Services
          ├─ 歌詞來源與解析
          ├─ Whisper / 高精度對齊
          ├─ 日文 Ruby / Roman
          ├─ 音軌分離背景工作
          └─ SQL Server 資料存取
```

## 建議閱讀順序

1. [`LearnMore/Program.cs`](../LearnMore/Program.cs)：組態來源、DI、Middleware、路由與 Hosted Service。
2. 找到對應頁面或 API 的 Controller。
3. 從 Controller 注入的介面追到 `Services/` 實作。
4. 查看同名或同流程的 `LearnMore.Tests/` 測試，確認既有行為契約。
5. 涉及外部程序時，再檢查 `Scripts/`、`FFmpeg`、`yt-dlp`、Whisper 或遠端 API 的設定邊界。

## 功能與程式碼對照

| 功能 | 請求入口 | 主要實作／模型 | 測試線索 |
| --- | --- | --- | --- |
| 首頁、搜尋、歌手與歌曲清單 | [`HomeController`](../LearnMore/Controllers/HomeController.cs) | `Songs`、`PerformerCollectionViewModel` | `Home*Tests.cs`、`SearchSuggestionsMvpTests.cs` |
| 同步歌詞、卡拉 OK、逐句練習 | [`LyricsController`](../LearnMore/Controllers/LyricsController.cs) | [`Views/Lyrics/Index.cshtml`](../LearnMore/Views/Lyrics/Index.cshtml) | `Lyrics*Tests.cs`、`Karaoke*Tests.cs` |
| 上傳、召喚、編修與審核 | [`MediaController`](../LearnMore/Controllers/MediaController.cs) | `Whisper*QueryService`、`Whisper*MutationService` | `Media*Tests.cs`、`EditPages*Tests.cs` |
| Whisper 轉錄與持久化 | [`WhisperController`](../LearnMore/Controllers/API/WhisperController.cs) | `WhisperTranscribeWorkflowService`、`WhisperTranscriptionPersistenceService` | `WhisperControllerTests.cs`、`WhisperTranscriptionPersistenceServiceTests.cs` |
| 高精度歌詞對齊 | `MediaController` | `WhisperHighAccuracyInitialPassService`、`VocalOnsetDetectionService` | `WhisperHighAccuracy*Tests.cs`、`VocalOnset*Tests.cs` |
| 日文 Ruby 與 Roman | [`KuroshiroController`](../LearnMore/Controllers/API/KuroshiroController.cs)、[`MeCabController`](../LearnMore/Controllers/API/MeCabController.cs) | `JapaneseRubyGeneratorService`、`JapaneseRubySanitizer`、`JapaneseRomanSanitizer` | `JapaneseRomanSanitizerTests.cs` |
| YouTube metadata 與字幕 | [`YoutubeController`](../LearnMore/Controllers/API/YoutubeController.cs) | `YouTubeMetadataResolverService`、`YouTubeSubtitleDownloadService` | `YouTubeMetadataResolverServiceTests.cs`、`YouTubeSubtitleParserServiceTests.cs` |
| 音訊下載與音軌分離 | `MediaController` | `YtDlpAudioDownloaderService`、`DemucsAudioStemProcessor`、`AudioStemProcessingHostedService` | `AudioStemBackgroundQueueTests.cs`、`MediaManageAudioStemStatusSurfaceTests.cs` |
| 群組播放與收藏 | [`GroupPlayerController`](../LearnMore/Controllers/GroupPlayerController.cs)、[`SongGroupController`](../LearnMore/Controllers/API/SongGroupController.cs) | `SongGroupRepository`、`FavoriteSongViewModel` | `GroupPlayerKaraokeAudioSurfaceTests.cs`、`FavoritesFeatureTests.cs` |
| 登入與持續工作階段 | [`LoginController`](../LearnMore/Controllers/LoginController.cs) | `PersistentLoginSessionService`、ASP.NET Core Data Protection | `PersistentSessionMvpTests.cs`、`PrivacyAndCommentSecurityTests.cs` |

## 重要執行流程

### 建立與整理歌曲

1. `MediaController` 接收 YouTube 網址、歌曲資訊或使用者操作。
2. `YouTubeMetadataResolverService`、字幕服務與外部歌詞來源嘗試取得可用資料。
3. 沒有合適時間軸時，Whisper 相關服務負責轉錄與初步對齊。
4. `JapaneseRubyGeneratorService` 與 Kuroshiro／MeCab 產生日文讀音。
5. Query／Mutation／Persistence Service 將歌曲與歌詞寫入 SQL Server。
6. 編修頁與審核佇列保留人工修正入口。

### 播放同步歌詞

1. `LyricsController` 讀取歌曲與歌詞資料。
2. `Views/Lyrics/Index.cshtml` 輸出播放器與歌詞結構。
3. 前端 JavaScript 依播放器時間切換目前句子、Ruby、Roman、中文與卡拉 OK 狀態。

### 背景音軌處理

1. `AudioStemJobService` 建立並租用待處理工作。
2. `AudioStemProcessingHostedService` 輪詢佇列。
3. `DemucsAudioStemProcessor` 執行本機分離；設定遠端模式時改用 `RemoteApiAudioStemProcessor`。
4. 處理狀態回寫 SQL Server，管理頁顯示進度與失敗原因。

## 組態與外部依賴

公開的設定範本位於 [`LearnMore/appsettings.Local.example.json`](../LearnMore/appsettings.Local.example.json)。實際值應放在未納入 Git 的 `appsettings.Local.json` 或部署環境設定。

主要外部依賴包括：

- SQL Server：歌曲、歌詞、使用者、收藏、群組與工作狀態。
- YouTube、LRCLIB、NetEase、TypingTube：metadata、字幕或歌詞來源。
- FFmpeg、yt-dlp：音訊下載、轉檔與前處理。
- Whisper、faster-whisper、WhisperX：語音辨識與時間軸對齊。
- MeCab、Kuroshiro、Kuromoji：日文斷詞、讀音與 Roman。
- Demucs 或遠端音軌 API：人聲／伴奏分離。
- Google Identity：登入驗證。

## 資料層注意事項

- 專案目前以 `System.Data.SqlClient` 與參數化 SQL 為主，不是 Entity Framework 專案。
- 完整 production schema 與正式資料不在公開倉庫中，因此本機完整啟動需要自行準備相容資料庫。
- 修改查詢時，應同時檢查 Controller、Query／Mutation Service，以及對應的 xUnit 測試。

## 驗證方式

```powershell
dotnet restore LearnMore.sln
dotnet test LearnMore.sln
```

測試涵蓋服務邏輯、資料處理、權限與安全邊界，也包含以原始碼或 Razor 結構確認畫面契約的 surface tests。外部 CLI、SQL Server、第三方 API 與正式站行為仍需另外做整合或部署後驗證。

## 公開倉庫邊界

公開版不包含正式資料庫、歌曲與翻譯內容、使用者資料、Cookie、API Key、部署憑證或內部操作腳本。閱讀或修改程式碼時，請以設定 key 與範例檔為準，不要把任何真實秘密寫入測試、文件或 issue。
