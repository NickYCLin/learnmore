# 從 GroupId 改為 GroupUid 的遷移指南

## ?? 目標
將群組播放器的 URL 從使用 `GroupId` (int) 改為使用 `GroupUid` (string, GUID)

## ? 已完成的修改

### 1. 模型修改
- ? `SongGroup.cs` - 添加 `GroupUid` 屬性
- ? `GroupPlayerViewModel.cs` - 將 `GroupId` 改為 `GroupUid`

### 2. Repository 修改
- ? `SongGroupRepository.cs`:
  - `GetGroups()` - 查詢包含 `GroupUid`
  - 新增 `IsGroupOwnedByUserByUid()`
  - 新增 `GetGroupIdByUid()`

### 3. Controller 修改
- ? `GroupPlayerController.cs`:
  - `Play()` action - 參數改為 `string groupUid`
  - SQL 查詢改用 `WHERE g.GroupUid = @GroupUid`

### 4. API 修改
- ? `SongGroupController.cs`:
  - `GetGroups()` - 回傳包含 `groupUid`

## ?? 需要手動修改的前端代碼

### Index.cshtml (首頁)

需要修改的地方：

#### 1. 群組按鈕 (約第 23 行)
```csharp
// 修改前:
<a href="@Url.Action("Index", "Home", new { groupId = group.GroupId })"
   class="btn btn-purple px-4 py-2 rounded-pill group-btn"
   data-group-id="@group.GroupId"
   data-group-name="@group.GroupName">

// 修改後:
<a href="@Url.Action("Index", "Home", new { groupId = group.GroupId })"
   class="btn btn-purple px-4 py-2 rounded-pill group-btn"
   data-group-id="@group.GroupId"
   data-group-uid="@group.GroupUid"  <!-- ?? 新增 -->
   data-group-name="@group.GroupName">
```

#### 2. JavaScript 部分 - refreshGroupButtons 函數 (約第 1400 行)
```javascript
// 修改前:
btn.href = `@Url.Action("Index", "Home")?groupId=${encodeURIComponent(group.groupId)}`;
btn.dataset.groupId = group.groupId;

// 修改後:
btn.href = `@Url.Action("Index", "Home")?groupId=${encodeURIComponent(group.groupId)}`;
btn.dataset.groupId = group.groupId;
btn.dataset.groupUid = group.groupUid;  // ?? 新增
```

#### 3. 將所有 GroupPlayer/Play 連結改用 GroupUid
找到所有跳轉到播放器的代碼，將：
```javascript
// 修改前:
window.location.href = `/GroupPlayer/Play/${groupId}`;

// 修改後:
window.location.href = `/GroupPlayer/Play/${groupUid}`;
```

或使用 Razor 語法：
```csharp
// 修改前:
@Url.Action("Play", "GroupPlayer", new { groupId = group.GroupId })

// 修改後:
@Url.Action("Play", "GroupPlayer", new { groupUid = group.GroupUid })
```

## ?? 資料庫遷移

### 步驟 1: 添加 GroupUid 欄位
```sql
ALTER TABLE SongGroup
ADD GroupUid NVARCHAR(50) NULL;
```

### 步驟 2: 為現有資料生成 GUID
```sql
UPDATE SongGroup
SET GroupUid = NEWID()
WHERE GroupUid IS NULL;
```

### 步驟 3: 設置為必填並添加預設值
```sql
ALTER TABLE SongGroup
ALTER COLUMN GroupUid NVARCHAR(50) NOT NULL;

ALTER TABLE SongGroup
ADD CONSTRAINT DF_SongGroup_GroupUid DEFAULT NEWID() FOR GroupUid;
```

### 步驟 4: 添加唯一索引（可選）
```sql
CREATE UNIQUE INDEX IX_SongGroup_GroupUid
ON SongGroup(GroupUid);
```

## ?? 測試檢查清單

- [ ] 建立新群組 → GroupUid 自動生成
- [ ] 查看群組列表 → 顯示所有群組
- [ ] 點擊群組 → 跳轉到 `/GroupPlayer/Play/{groupUid}`
- [ ] 播放器頁面 → 正確顯示群組名稱和歌曲
- [ ] 自動播放 → 歌曲結束後自動播放下一首
- [ ] 加入/移除歌曲 → 功能正常
- [ ] 刪除群組 → 功能正常

## ?? 優點

使用 `GroupUid` (GUID) 而不是 `GroupId` (int) 的好處：

1. **安全性** - 無法猜測其他群組的 UID
2. **分散式** - 可以在不同伺服器生成唯一 ID
3. **隱私** - 不洩露群組數量資訊
4. **URL 美化** - 可讀性更好 (雖然GUID較長)

## ?? 關於自動播放問題

自動播放需要**用戶互動**後才能啟用（瀏覽器政策）：

1. **第一次播放** - 必須手動點擊播放按鈕
2. **之後** - 歌曲結束會自動切換並播放

這是**正常且預期的行為**，符合現代瀏覽器的自動播放政策。

已修改的代碼會追蹤 `userHasInteracted` 狀態，確保在用戶互動後啟用自動播放功能。
