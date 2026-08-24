# LearnMore

LearnMore 是一個以日文歌曲為核心的學習網站。它把歌詞時間軸、日文注音、羅馬拼音、繁體中文翻譯與跟唱模式放在同一個頁面，也整合了字幕、LRC 與語音辨識來源，方便整理不同品質的歌曲資料。

線上版本：[https://magicplus-design.serveirc.com/LearnMore](https://magicplus-design.serveirc.com/LearnMore)

## 目前功能

- 依播放時間同步顯示日文、中文、Ruby 注音與 Roman。
- 從 YouTube 字幕、LRCLIB、NetEase、TypingTube 與 Whisper 整理歌詞。
- 使用 MeCab、Kuroshiro 與專案內的修正規則產生日文讀音。
- 提供歌曲召喚、人工編修、審核佇列與高精度對齊流程。
- 支援卡拉 OK 模式、音軌分離、歌曲群組、個人收藏與 Mika 角色互動。
- 管理歌曲、演出者、留言、收藏與使用者設定。

## 技術組成

- ASP.NET Core 8 MVC、Razor Views
- SQL Server
- xUnit
- OpenAI Whisper、faster-whisper、WhisperX
- MeCab、Kuroshiro、Kuromoji
- FFmpeg、yt-dlp、Demucs
- Playwright

## 專案結構

```text
LearnMore/          Web 專案、Controller、Service、View 與靜態資源
LearnMore.Tests/    核心流程與畫面行為測試
LearnMore/Scripts/  語音辨識與高精度對齊輔助程式
```

## 開發環境

基本需求：

- .NET 8 SDK
- SQL Server
- Node.js 20 以上
- FFmpeg 與 yt-dlp

若要使用本機高精度辨識或音軌分離，還需要 Python、faster-whisper、WhisperX 或 Demucs。這些功能可以先保持關閉，不影響閱讀程式碼與執行一般測試。

### 1. 還原前端套件

```powershell
npm ci --prefix LearnMore/wwwroot/js
```

### 2. 建立本機設定

```powershell
Copy-Item LearnMore/appsettings.Local.example.json LearnMore/appsettings.Local.json
```

修改 `appsettings.Local.json` 的 SQL Server 連線資訊，需要使用的外部服務再填入對應金鑰。這個檔案已加入 `.gitignore`，請勿提交正式憑證。

### 3. 還原與測試

```powershell
dotnet restore LearnMore.sln
dotnet test LearnMore.sln
```

### 4. 啟動網站

```powershell
dotnet run --project LearnMore/LearnMore.csproj
```

完整啟動需要相容的 SQL Server schema。正式站的歌曲、歌詞、會員與留言資料不包含在這個公開倉庫中。

## 公開範圍

這裡分享的是應用程式原始碼與測試，不包含正式環境設定、Cookie、API Key、資料庫備份、完整歌曲歌詞、翻譯資料及使用者內容。第三方套件與日文字典依各自附帶的授權條款使用。

目前專案仍持續維護。若只是想看看實際操作，可以直接使用上方的線上版本。
