# 專案協作約定

## Commit message

依照 [Git Commit Message 這樣寫會更好，替專案引入規範與範例](https://ithelp.ithome.com.tw/articles/10228738) 撰寫。

- 說明文字使用繁體中文，語氣自然、簡短，直接寫清楚改了什麼；避免 AI 式套話、空泛形容詞和不必要的長篇條列。
- 標題格式為 `<type>(<scope>): <subject>`，`scope` 可省略。
- `type` 使用 `feat`、`fix`、`docs`、`style`、`refactor`、`perf`、`test`、`chore` 或 `revert`，依實際改動選擇。
- 標題不超過 50 個字元，結尾不加句號。
- 需要補充時，在標題後空一行寫內文，交代修改原因、調整內容及修改前後的差異；每行不超過 72 個字元。
- 有對應任務或 issue 時，在 footer 註明編號；有不相容變動時，以 `BREAKING CHANGE:` 說明影響、原因及遷移方式。
- 依改動目的拆分 commit，避免把不相關的修改包在一起。

範例：`fix(ios): 修正切換閱讀設定後歌詞未更新`

## Push 前的同步

- 每次 push 前都要先執行 `git pull`，確認遠端是否有其他人的改動。
- Pull 前先確認目前分支、追蹤的遠端分支及工作目錄狀態，妥善保留尚未提交的修改。
- 有遠端更新時，檢查並整合他人的改動，完成必要的驗證後再 push。
- Pull 失敗或仍有衝突時，先處理完成，不直接 push。
