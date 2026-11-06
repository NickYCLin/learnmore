# 歌曲上傳者寫入

手動上傳、自動建檔、歌曲召喚與舊版轉錄 API 都必須傳入伺服器登入
Session 的 Email，不接受前端指定上傳者。持久層在同一個交易內鎖定並
確認唯一帳號，寫入 Songs.AddedByUserId 與 Users.Producer；任一步驟失敗
都回滾，不再於建檔完成後另外補關聯。Producer 為 NULL 的帳號可正常
建立第一首歌曲，帳號不存在或 Email 重複則拒絕新增。

所有歌曲與歌詞 SQL 使用 DefaultConnection 指向的 dbo，避免主表與
關聯寫入不同資料庫。伴奏背景工作只在交易提交後入隊。

本修正不推定歷史歌曲的上傳者，不批次回填既有 NULL 欄位。
隔離 SQL 測試涵蓋三個持久層入口的上傳者、首次關聯、追加關聯、
帳號不存在與強制 INSERT 失敗回滾；API 測試確認傳遞登入 Session。
