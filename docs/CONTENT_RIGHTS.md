# 內容來源與授權查核

查核日期：2026-09-10。範圍為這份 LearnMore 原始碼、既有聲明及可取得的官方條款；未讀取正式歌曲資料庫、私人授權契約或外部 Mika 主機上的模型檔。以下是來源與證據盤點，不能視為整個服務已取得授權的法律結論。

目前先做個人 iPhone 試用，App Store 上架暫緩。個人擁有網站或檔案，不等於擁有其中所有作品的著作權；是否可用仍取決於各項內容的來源、授權及實際利用方式。

| 項目 | 專案證據 | 本次確認與限制 |
| --- | --- | --- |
| 自行撰寫的程式碼 | 根目錄 LICENSE、THIRD_PARTY_NOTICES.md | 採 MIT；第三方套件各自適用原授權，不會因此授權歌曲或角色。 |
| 新 iOS 圖示 | `mobile/assets/app-icon.svg` | 本次以書本、音符幾何圖形繪製，未使用既有角色、封面或字型；SVG 及產生的 PNG 依專案 MIT。 |
| mao_pro、hiyori_pro | `LearnMore/Views/User/About.cshtml` 註明 Live2D 官方 sample，並附版權標示 | 官方將 Mao Niziiro、Hiyori Momose 列為 Live2D 原創角色。有條件可用，並非使用者自有。外部服務未連通，尚未比對實際模型版本、修改內容及 SDK 授權。 |
| 語音「まお」 | 同一說明頁連至 AivisHub 指定模型 | 官方模型頁列 ACML 1.0、版本 1.2.0、作者 Oz Chat／Trippy／ねゆたろ。這與 Live2D 角色是不同素材，不能混用授權；需依實際部署檔內的授權及附加條件核對。 |
| 歌詞、時間軸 | `TypingTubeLyricsService.cs`、`docs/ARCHITECTURE.md` 記錄 LRCLIB、NetEase、TypingTube、YouTube 等來源 | 程式取得資料的管道已確認，未找到可涵蓋全部歌曲的權利人授權證據；來源可存取不代表可任意重製、散布。 |
| 中文翻譯 | `WhisperTranslationSourceService.cs` 具有 marumaru、巴哈姆特與 GPT 翻譯流程 | 不能一概當作使用者自行創作。需逐曲確認實際使用哪個來源、翻譯者許可及原歌詞權利；AI 翻譯也不會自行取得原歌詞的授權。 |
| YouTube 影片 | `LearnMore/wwwroot/js/mobile-player.js` 使用官方嵌入播放器 | 嵌入服務與影片／歌詞本身的權利是不同問題。iOS 第一版不提供音訊下載；網站其他下載或分離流程不在此結論內。 |
| 既有封面、網站圖示、圖片與媒體 | THIRD_PARTY_NOTICES.md 列出的 images、media、favicon 等範圍 | 尚未取得逐項來源及許可；保留為待確認，沒有因個人試用而改列 MIT。 |

## Live2D 可使用的條件

[Mao 官方頁](https://www.live2d.com/en/learn/sample/niziiro-mao/)與[無償素材契約](https://www.live2d.com/eula/live2d-free-material-license-agreement_en.html)允許符合定義的一般使用者／小規模事業者，在遵守條款下用於商業或非商業創作。一般使用者的定義含最近年度商業活動銷售額低於一千萬日圓；不能只用「個人專案」判定資格。

[模型條款](https://www.live2d.com/eula/live2d-sample-model-terms_en.html)要求保留指定版權標示，Hiyori 另有不得變更角色設計的限制。LearnMore 的 About 頁已有長版標示，但仍須核對實際角色呈現、模型與使用方式。素材條款不會取代 Cubism SDK／Core 或其他執行程式的授權。

## 歌曲與翻譯需要的證據

智慧財產局說明，翻譯外國歌詞涉及改作，除合理使用等例外外，原則上需原權利人同意；非營利本身也不是一律免授權的理由。個人私人練習與在公開網站向使用者提供整份歌詞，不能直接視為同一種利用方式。

- [智慧財產局：翻譯外國歌詞](https://www.tipo.gov.tw/tw/copyright/692-13434.html)
- [智慧財產局：著作權一點通](https://www.tipo.gov.tw/tw/copyright/701-21230.html)
- [YouTube 開發者政策](https://developers.google.com/youtube/terms/developer-policies)：影音下載、快取及離線播放等限制。
- [AivisHub「まお」模型頁](https://hub.aivis-project.com/aivm-models/a59cb814-0083-4369-8542-f51a29e72af7)：模型來源與授權標籤，仍待實際檔案核對。

後續逐曲補上：歌曲識別碼、詞曲權利人、歌詞來源、翻譯作者與來源、授權證據位置、允許的公開傳輸／改作／散布範圍、商用與地區限制、期限、必要署名。私人契約只記錄內部存放位置，不將契約全文或個人聯絡資料提交到公開 GitHub。

本次未聯繫權利人、未申請授權，也未把「未找到證據」當成確定侵權。若素材確實全部由使用者原創，仍需補上可核對的來源紀錄，以區分外部取得的內容。
