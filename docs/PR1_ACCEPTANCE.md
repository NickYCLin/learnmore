# PR1 待驗收與本機診斷

## 已證實與尚未證實

- 程式提交 ed8809a：後端 CI 38690294740、Capacitor 38690294754 成功。
  SwiftUI 38690294738 的 attempt 1、2 都在 Boot UI test simulator 等待
  BackBoard 五分鐘後逾時；PlaybackCore 五項、Release、build-for-testing 成功，
  UI tests 未執行。Keychain fixture 的 UI 修正仍未驗證，不刪測試放行。
- attempt 2 的選定裝置是 iPhone 17 Pro、iOS 26.0.1，Xcode 26.3；
  06:17:53 UTC 開始開機，06:17:57 等待 BackBoard，06:23:04 步驟逾時。
  build-for-testing 曾有工具「exit code 0 but produced no further output」訊息，
  該步驟仍成功；架構 destination warning 也不是已證實的開機失敗原因。
- 現存日誌缺少當時磁碟、記憶體與詳細 CoreSimulator／BackBoard 資料，
  不能判定資源耗盡或指定根因。兩次相同方式重跑無法補足此證據。
- main 61743b7、PR1 91a3b25 已改為按需 Actions，這些設定提交沒有重新跑 CI。
  本輪不 dispatch、rerun 或取消任何 run，不改 timeout、jobs 或費用設定。
- 目前 executor 是 Linux，沒有 xcrun／xcodebuild。既有兩個 Mac 連線先前失敗，
  本輪可用性清單仍顯示未連線；沒有已確認可用的免費 Mac，不重試未知結果操作。

## 本輪本機檢查（2026-10-15）

- `python3 -m unittest discover -s scripts -p 'test_diagnose_learnmore_ios_simulator.py' -v`：
  五項通過，驗 Linux 不啟動 probe、缺工具、逾時保留輸出、輸出截斷／失敗與 process 過濾。
  Linux 直接執行診斷也回傳 unsupported，沒有 Mac probe 或模擬器結果。
- `dotnet test LearnMore.Tests/LearnMore.Tests.csproj -c Release --no-restore`
  以 filter 選 MobileOAuthIsolationTests、MobileAccountSecurityTests、
  SessionRevocationTests、SqlAccountIntegrationTests：41 pass／0 skip／0 fail，
  有走建置流程，未用 `--no-build` 或沿用舊測試報表。
  其中 12 項在既有 cached SQL Server 2022 image 的新隔離容器上實際執行；
  僅 loopback 動態 port、隨機密碼、合成資料，每測試建立／刪除獨立 DB，結束移除容器。
  另 29 項為合成 OAuth／帳號安全／session 測試，不等於真 OAuth 或真 Apple。
  建置仍有既有 ImageSharp 套件弱點警告，本輪未更動依賴，也不把警告當成不存在。
- 核對雙 proof freshness／owner、禁止 email 接管、一次性 migration approval、
  加密 outbox／短租約與失敗重試後，未找到本輪需另改服務的確切新問題。
  這不代表原生 provider 一定提供 fresh auth_time，亦不代表完成所有發佈驗收。
- 本輪只新增診斷工具／測試與文件，未更動 backend、iOS App 或 workflow jobs；
  本機結果不是本輪提交的遠端 CI，兩次 BackBoard 失敗仍保留。

## 預設只讀診斷

在使用者現有可用 Mac、相同 Xcode 選擇與問題發生後五分鐘內執行：

```sh
python3 scripts/diagnose_learnmore_ios_simulator.py > simulator-before.json
```

腳本只讀 OS／架構、Xcode／SDK、runtime／device inventory、磁碟／記憶體、
相關 process 與最近五分鐘 host CoreSimulator／backboardd unified log。
每個 probe 最多十五秒（log 二十秒），輸出各最多 64 KiB，並保留失敗、逾時、
截斷標記；完整執行的 probe 等待上限合計 185 秒。不收集環境變數或憑證。
macOS 上仍可能因 log 權限或 host log 沒收錄 guest 事件而沒有足夠資料；不自動提權。
Linux 回傳 unsupported 且不執行 probes；此結果與腳本單元測試都不是 Mac 實測。
不 boot、reset、delete、shutdown、kill 模擬器或其他既有工作；逾時只終止本次 probe。
分享前檢查並遮蔽使用者路徑、裝置名稱及日誌內可能的個資。

## 使用者確認後的單次重現

先從 inventory 選定**自己專用、目前無其他工作使用**且匹配原 runtime 的 UDID；
不要直接採用舊 CI 的暫時 UDID，也不要自動選第一台可用裝置。
確認既有 Xcode 26.3／iOS 26 runtime 可用後，才在 Mac 手動執行一次：

```sh
export LEARNMORE_SIMULATOR_ID='填入已確認專用的 UDID'
python3 - <<'PY'
import os
import subprocess
try:
    subprocess.run(
        ["xcrun", "simctl", "bootstatus", os.environ["LEARNMORE_SIMULATOR_ID"], "-b"],
        timeout=300, check=True,
    )
except subprocess.TimeoutExpired:
    raise SystemExit("300 秒逾時；停止重試，先收集診斷。")
PY
python3 scripts/diagnose_learnmore_ios_simulator.py > simulator-after.json
```

這個明確的重現命令會啟動**所選裝置**；等待命令逾時不會撤回 daemon 已開始的開機。
不要自動 reset／shutdown，不因失敗換別台或再重跑。
若開機成功，在相同 Mac、checkout SHA、選定 UDID 上依 `.github/workflows/ios.yml`
先 build-for-testing（十分鐘界線）再 test-without-building（十五分鐘界線），
保留原 UI assertions、xcresult summary 與完整 log；必要時人工在界線停止本次命令。
記錄 SHA、OS、Xcode、runtime、UDID、開始／結束時間與每個命令 exit status。
沒有 Mac 就停在此步，不改用 Linux 結果替代，也不自動啟動付費範圍未知的 Actions。

## OAuth、舊帳號與撤銷的最小驗收

前提是使用者已有隔離後端、測試帳號與可用的既有簽章 App／provider 設定。
沒有既有條件就列為阻礙，不新增 signing key、provider 權限或付費服務。

1. 先記錄 App／後端 SHA 與環境；Google、Apple 各自用測試帳號走原生登入，
   由後端只記驗簽後 auth_time 是否存在、距今秒數與判定，不分享 token／code／nonce。
   既有服務要求五分鐘內 auth_time，未提供或過期須拒絕；新 iat 不能代替。
   原生 SDK 實際是否供應新鮮 auth_time 尚未證實，若持續缺 claim，帳號變更仍被阻擋，
   不能把安全拒絕當功能可用；需先確定 provider 支援的重新驗證方式，再設計修改。
2. 連結依 UI 先取原帳號 proof，再取目標 provider proof；成功後核對 provider 列表。
   以其他帳號原 proof、過期 proof、已屬他人 subject 驗證拒絕且資料不變。
   同 email 不得接管；舊帳號須人工核對既有 provider／subject／UserId 所有權，
   建立現有一次性短效 migration approval，再以 fresh proof 消耗；不可用 email 自動補。
3. 帳號刪除前保留兩個隔離 App sessions、未兌換 code 與 web persistent cookie；
   刪除後逐一驗證失效、重啟服務後仍不能恢復，重用 email 不可繼承舊資料。
4. Apple 撤銷以非敏感 outbox metadata 核對 pending／attempt／lease／完成狀態，
   同時涵蓋先前與本次 refresh token 的工作；HTTP 202 只代表本地刪除接受，
   不等於 Apple 撤銷完成。失敗須保留加密工作並退避重試；pending subject 不得重登入。
   本機 fake HTTP／合成簽章與隔離 SQL 可驗交易、重試與交錯，不能證實真 Apple 撤銷。

只留時間、SHA、狀態碼、布林判定及遮蔽後的結果，不索取 raw token、private key、
cookie、連線字串或完整帳號識別資訊。任一步失敗保留具體失敗，不以隱藏 UI 結案。

## Apple 原始 export 與真機

- 既有 check 115679734630（head 8ee61d45）為 action_required，摘要只有零 errors／
  兩 warnings，無法說明原始 export 為何失敗。具既有 read 權限的使用者從該 check
  的 App Store Connect details 取得原 build／export 階段完整日誌與錯誤；
  回傳遮蔽後的錯誤、build ID、時間與階段即可，不重新 export、不上傳 TestFlight。
- 用既有可安裝 build，在真實 iPhone 分別驗兩客戶端：鎖定 30／60 秒、返回前景、
  耳機拔插與音訊中斷／恢復。記錄裝置、OS、build／SHA、音訊狀態、進度／歌詞、
  實際恢復方式；Capacitor 現有背景暫停規格不等於 SwiftUI 原生背景播放保證。
  未有對應 build／裝置則待使用者提供現有環境，不新增簽章權限或部署。

以上真 OAuth、原始 export、模擬器 UI 與真機證據未齊前，PR1 維持 Draft/Open，
不把未驗收功能合併 main，也不以 CI 綠宣稱發佈或上架成功。
