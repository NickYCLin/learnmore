# 本機暫存產物保留規則

此工具管理執行開發、測試與歌曲分析的 Linux 主機，不是正式網站的 Windows 部署磁碟。

只清理管理者在私有登記檔明確列出的退役發布目錄。目錄必須直接位於 `/tmp`，
名稱符合 `learnmore-…-publish`；不會自動擴大到其他新目錄。
不得將仍需要的回復副本、正式歌曲、上傳目錄或分析工作目錄登記為退役產物。

預設保留至少七天，依最後存取、修改、狀態變更時間中較新的時間計算。
每日臺北時間 04:30 執行，隨機延後至多五分鐘；關機錯過時會補跑。
不清除工作目錄、Git checkout、虛擬環境、辨識模型或未完成歌曲的音訊與驗證證據。
登記目錄中的 JSON、日誌、文件、設定、腳本、模型、符號連結及硬連結也會保留。

清理前會檢查執行中的同帳戶程序、所有 Docker 容器掛載（包含停止的備份容器）、
目錄內的掛載點、Git 標記與 `.keep`。無法列出 Docker 掛載或讀取同帳戶程序的
指令列時保留檔案；可讀取的開啟檔案與記憶體映射會作為額外的使用中檢查。
刪除使用不追蹤符號連結的目錄檔案描述元，並再次比對檔案身分。
工具預設只預覽，須加 `--apply` 才刪除；以檔案鎖防止重複執行。

## 安裝

以下命令應由擁有這些本機暫存目錄的帳戶執行。登記檔必須是該帳戶持有、
不可由其他帳戶修改的普通檔案（建議權限 0600），格式如下：

```json
{"retired_publish_roots": ["/tmp/learnmore-example-publish"]}
```

```sh
install -Dm755 scripts/learnmore_temp_cleanup.py "$HOME/.local/libexec/learnmore-temp-cleanup.py"
install -Dm644 scripts/systemd/learnmore-temp-cleanup.service "$HOME/.config/systemd/user/learnmore-temp-cleanup.service"
install -Dm644 scripts/systemd/learnmore-temp-cleanup.timer "$HOME/.config/systemd/user/learnmore-temp-cleanup.timer"
python3 "$HOME/.local/libexec/learnmore-temp-cleanup.py" --registry "$HOME/.config/learnmore-temp-cleanup/registry.json"
systemctl --user daemon-reload
systemctl --user enable --now learnmore-temp-cleanup.timer
```

確認預覽後，可用 `systemctl --user start learnmore-temp-cleanup.service` 清理。
排程收據只記錄目錄名稱、檔案數與配置空間，不輸出程序指令列或容器設定；
保存在 `~/.local/state/learnmore-temp-cleanup/last-apply.json`。

若已逐項確認所有登記目錄均已退役且可重建，可單次使用 `--min-age-days 0 --apply`
清除這些副本；排程仍使用七天保留期。這個參數不會略過其他保護檢查。

停用排程：`systemctl --user disable --now learnmore-temp-cleanup.timer`。

## 驗證

```sh
python3 -m unittest discover -s scripts -p test_learnmore_temp_cleanup.py -v
systemd-analyze --user verify scripts/systemd/learnmore-temp-cleanup.service scripts/systemd/learnmore-temp-cleanup.timer
```
