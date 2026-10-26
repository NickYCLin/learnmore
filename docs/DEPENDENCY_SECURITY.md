# 相依套件安全修補紀錄

查核日期：2026-10-26。基準為 GitHub main `17d1f5c`，僅修改 mobile 相依版本與此紀錄，不調整業務程式、框架目標或 CI 觸發方式。

## 結果摘要

- mobile：npm audit 原為 3 高、1 嚴重，共 4 個套件、14 筆 advisory；修補後全部等級為 0。
- .NET：ImageSharp 的 3 高、2 中共 5 筆 advisory 仍未解決。修正版 4.1.2 要求有效的 Six Labors 授權，本機升級試驗在建置時被授權檢查擋下，已還原至 3.1.12。
- 測試通過僅證明本次可執行的相容性檢查通過，不代表 ImageSharp 弱點已修復、正式環境安全驗收或可送審。

## ImageSharp：保留版本與未解原因

相依路徑：

```text
LearnMore -> SixLabors.ImageSharp 3.1.12（直接）
LearnMore.Tests -> LearnMore 專案參考 -> SixLabors.ImageSharp 3.1.12（間接）
```

| Advisory | 風險與實際問題 | 受影響版本 | 首個修正版 |
|---|---|---|---|
| [GHSA-gwg2-r3hj-4w44](https://github.com/advisories/GHSA-gwg2-r3hj-4w44) | 中：ICC CLUT 通道／網格尺寸未驗證，可能造成過量配置 | `>=1.0.0-beta0001 <=4.1.1` | `4.1.2` |
| [GHSA-j3p4-wp97-rph4](https://github.com/advisories/GHSA-j3p4-wp97-rph4) | 高：HistogramEqualization 未驗證亮度值即作為直方圖索引 | `>=2.0.0 <=4.1.1` | `4.1.2` |
| [GHSA-j9gm-c75j-xc9q](https://github.com/advisories/GHSA-j9gm-c75j-xc9q) | 高：TIFF CCITT T4 編碼器可能寫出壓縮緩衝區邊界 | `>=2.0.0 <=4.1.1` | `4.1.2` |
| [GHSA-jjfr-hcj7-qf5w](https://github.com/advisories/GHSA-jjfr-hcj7-qf5w) | 高：TIFF CCITT T6 編碼器輸出緩衝區不足，可能越界寫入 | `>=2.1.0 <=4.1.1` | `4.1.2` |
| [GHSA-wmxv-xphr-5c9g](https://github.com/advisories/GHSA-wmxv-xphr-5c9g) | 中：BigTIFF IFD 計數可能使解碼執行緒陷入無進展迴圈 | `>=2.0.0 <=4.1.1` | `4.1.2` |

4.1.2 的 NuGet 套件提供 `net8.0` 組件，不需要改 TargetFramework；但其 MSBuild targets 在本機直接回報 `No Six Labors license found`。目前沒有可供這次建置驗證的授權，因此不能宣稱此升級已可交付；較舊的 3.2.0 仍位於受影響範圍，也不是修法。

本次沒有繞過授權檢查、停用 NuGet audit、加入弱點抑制或寫入金鑰。頭像上傳現有程式會解碼圖片、縮放為 96×96 並儲存 PNG；部分編碼／處理功能雖未直接呼叫，仍不能把套件警告視為已消除。後續須先由既有安全設定管道提供合法授權，再升級並重新驗證；改用其他圖片函式庫會擴大程式與行為變更，不屬於這次最小修補範圍。

## mobile：最小修補與相依路徑

| 套件 | 原版本 → 本次版本 | 相依路徑 | 處理方式 |
|---|---|---|---|
| DOMPurify | `3.4.15 → 3.4.17` | `mobile -> dompurify`，正式相依 | 提高 manifest 最低版本為 `^3.4.17`，更新 lockfile |
| Vite | `7.3.6 → 7.3.7` | `mobile -> vite`，開發／建置相依 | 留在 Vite 7，提高最低版本為 `^7.3.7`，更新 lockfile |
| brace-expansion | `5.0.9 → 5.0.12` | `@capacitor/cli@8.5.1 -> rimraf@6.1.3 -> glob@13.0.6 -> minimatch@10.2.6 -> brace-expansion` | 在既有 `^5.0.8` 範圍內更新 lockfile，不新增 override |
| source-map-js | `1.2.1 → 1.2.2` | `vite -> postcss@8.5.28 -> source-map-js` | 在既有 `^1.2.1` 範圍內更新 lockfile，不新增直接相依 |

lockfile 只更新上述四個套件的版本、下載位置與完整性雜湊；其他套件版本不變，沒有使用 `npm audit fix --force` 或跨主要版本更新。

### 各筆 advisory 的受影響範圍

下表列出本次安裝版本所在的版本線；修補後再次執行 registry audit，而非僅依版本推定已修復。

| 套件／Advisory | 風險與實際問題 | 該版本線受影響範圍 | 首個修正版 |
|---|---|---|---|
| DOMPurify [GHSA-p98j-92pf-mc4p](https://github.com/advisories/GHSA-p98j-92pf-mc4p) | 低：IN_PLACE hook 移除節點後，脫離子樹仍保有事件處理器 | `>=3.4.13 <=3.4.15` | `3.4.16` |
| DOMPurify [GHSA-6688-9rhm-gjv2](https://github.com/advisories/GHSA-6688-9rhm-gjv2) | 低：移除的 rawtext 根節點重新解析後可能執行 HTML | `<=3.4.15` | `3.4.16` |
| DOMPurify [GHSA-cg2g-xh64-v4q7](https://github.com/advisories/GHSA-cg2g-xh64-v4q7) | 嚴重：DOM 輸出模式中的 shadow root 可繞過 SAFE_FOR_TEMPLATES | `>=3.4.4 <=3.4.16` | `3.4.17` |
| DOMPurify [GHSA-mrv2-3pqr-j5jr](https://github.com/advisories/GHSA-mrv2-3pqr-j5jr) | 高：IN_PLACE hook 移除父／相鄰節點後仍保留事件處理器，屬先前修補不完整 | `<=3.4.16` | `3.4.17` |
| DOMPurify [GHSA-c6gr-qvv4-5v3f](https://github.com/advisories/GHSA-c6gr-qvv4-5v3f) | 低：XHTML 解析模式未套用 SMIL attributeName=href 保護 | `<=3.4.16` | `3.4.17` |
| brace-expansion [GHSA-6j4f-fj2g-mc7p](https://github.com/advisories/GHSA-6j4f-fj2g-mc7p) | 高：parseCommaParts 遞迴造成堆疊耗盡 DoS | `>=4.0.0 <5.0.10` | `5.0.10` |
| brace-expansion [GHSA-qhr7-859c-m2p7](https://github.com/advisories/GHSA-qhr7-859c-m2p7) | 高：巢狀括號群組遞迴造成堆疊耗盡 DoS | `>=4.0.0 <5.0.11` | `5.0.11` |
| brace-expansion [GHSA-q2hr-2g5m-vwhr](https://github.com/advisories/GHSA-q2hr-2g5m-vwhr) | 中：特定括號展開重寫造成平方時間 CPU DoS | `>=4.0.0 <5.0.12` | `5.0.12` |
| source-map-js [GHSA-68fv-2mgg-jv7q](https://github.com/advisories/GHSA-68fv-2mgg-jv7q) | 高：indexed source-map section offset 可阻塞事件迴圈 | `>=1.0.0 <1.2.2` | `1.2.2` |
| Vite [GHSA-698v-4r3x-3vcv](https://github.com/advisories/GHSA-698v-4r3x-3vcv) | 中：launch-editor 未跳脫 Windows `%` 參數 | `>=7.0.0 <=7.3.6` | `7.3.7` |
| Vite [GHSA-r825-w8mm-5pxw](https://github.com/advisories/GHSA-r825-w8mm-5pxw) | 中：launch-editor 參數被誤認為編輯器旗標，可導致命令執行 | `>=7.0.0 <=7.3.6` | `7.3.7` |
| Vite [GHSA-587h-7wmj-h5pm](https://github.com/advisories/GHSA-587h-7wmj-h5pm) | 高：Windows 可繞過 launch-editor 的 fs.existsSync 檢查 | `>=7.0.0 <=7.3.6` | `7.3.7` |
| Vite [GHSA-9jrq-w75r-8gcw](https://github.com/advisories/GHSA-9jrq-w75r-8gcw) | 低：自訂 middleware 可造成跨來源指令碼執行 | `>=7.0.0 <=7.3.6` | `7.3.7` |
| Vite [GHSA-rq7h-c2jc-7f22](https://github.com/advisories/GHSA-rq7h-c2jc-7f22) | 中：非 Windows 的特定路徑組合可讀取 server.fs.allow 之外檔案 | `>=7.0.0 <=7.3.6` | `7.3.7` |

## 本機驗證

| 項目 | 通過 | 失敗 | 跳過／限制 |
|---|---:|---:|---|
| 完整 .NET Release | 624 | 0 | 0；含 12 項實際隔離 SQL Server 2022 測試，使用合成 schema／資料 |
| Capacitor Node | 50 | 0 | 0；透過本機 Swift 6.0.3 容器補跑 fake 系統依賴 harness，不是真機驗收 |
| Capacitor WebKit 預設組態 | 9 | 0 | 1；diagnostic-on 案例依既有專案條件跳過 |
| Capacitor WebKit diagnostic-on | 1 | 0 | 0；使用暫存組態開旗標補跑該案例，未改正式設定 |
| Python API | 55 | 0 | 0；模型與外部 CLI 為測試替身，另有 1 筆既有棄用警告 |
| Swift PlaybackCore | 5 | 0 | 0；本機 Linux 容器，不是 Xcode App 建置 |
| 模擬器診斷工具單元測試 | 5 | 0 | 0 |
| 部署檢查腳本 | 15 | 0 | 0；PowerShell 7、本機 HTTP fixture，不是 Windows PowerShell 5.1 驗證 |

- `npm ci` 可依新 lockfile 乾淨安裝，`npm audit --json` 為 0 個弱點。
- `dotnet list "$PWD/LearnMore.sln" package --vulnerable --include-transitive --format json` 仍列出上述 ImageSharp 5 筆警告；測試專案的間接項目是同一套件，不是另 5 個獨立漏洞。
- TypeScript 型別檢查及 Vite 正式建置通過；.NET Release publish 到本機暫存目錄通過，Kuromoji 字典存在且不含本機設定。ImageSharp 安全警告仍會出現。
- Python 編譯、Shell／CommonJS 語法及 `git diff --check` 通過。
- ImageSharp 4.1.2 的試驗建置因授權失敗，未納入最終修改；未把這次失敗寫成升級驗證通過。
- 本機無 Xcode／xcrun，未執行 SwiftUI／Capacitor 原生 iOS 建置、原生 UI 或真機測試；也未執行 Windows PowerShell 5.1。

## 操作邊界

只建立獨立分支與 PR，不自行合併。三個 workflow 保持 `workflow_dispatch`，不觸發 Actions、不推送 GitLab、不部署、不變更簽章或 OAuth、不送審或上架；既有送審待辦與 release-status 均保持未完成。
