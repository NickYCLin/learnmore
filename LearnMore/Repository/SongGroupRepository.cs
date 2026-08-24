using LearnMore.Models;
using System.Data;
using System.Data.SqlClient;

namespace LearnMore.Repository
{
    public class SongGroupRepository
    {
        private readonly string _connectionString;

        public SongGroupRepository(string connectionString)
        {
            _connectionString = connectionString;
        }

        public int CreateGroup(string userId, string groupName)
        {
            const string sql = @"
INSERT INTO SongGroup (UserId, GroupName, CreateTime)
OUTPUT INSERTED.GroupId
VALUES (@UserId, @GroupName, GETDATE());";

            using var connection = new SqlConnection(_connectionString);
            using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@UserId", userId);
            command.Parameters.AddWithValue("@GroupName", groupName);
            connection.Open();
            return (int)command.ExecuteScalar();
        }

        public List<SongGroup> GetGroups(string userId)
        {
            const string sql = @"SELECT GroupId, GroupUid, GroupName FROM SongGroup WHERE UserId = @UserId ORDER BY CreateTime DESC";
            var groups = new List<SongGroup>();
            using var connection = new SqlConnection(_connectionString);
            using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@UserId", userId);
            connection.Open();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                groups.Add(new SongGroup
                {
                    GroupId = reader.GetInt32(0),
                    GroupUid = reader.GetString(1),
                    GroupName = reader.GetString(2)
                });
            }
            return groups;
        }

        // ✅ 新增：抓此使用者加入過群組的所有 SongUid（Distinct）
        public List<string> GetJoinedSongUids(string userId)
        {
            const string sql = @"
SELECT DISTINCT m.SongUid
FROM SongGroupMapping m
INNER JOIN SongGroup g ON g.GroupId = m.GroupId
WHERE g.UserId = @UserId;";
            var list = new List<string>();
            using var conn = new SqlConnection(_connectionString);
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@UserId", userId);
            conn.Open();
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) list.Add(rd.GetString(0));
            return list;
        }

        // 🆕 依使用者與歌曲抓出已加入的群組 Id 清單
        public List<int> GetUserGroupIdsForSong(string userId, string songUid)
        {
            const string sql = @"
SELECT m.GroupId
FROM SongGroupMapping m
INNER JOIN SongGroup g ON g.GroupId = m.GroupId
WHERE g.UserId = @UserId AND m.SongUid = @SongUid;";

            var ids = new List<int>();
            using var conn = new SqlConnection(_connectionString);
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@UserId", userId);
            cmd.Parameters.AddWithValue("@SongUid", songUid);
            conn.Open();
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) ids.Add(rd.GetInt32(0));
            return ids;
        }

        // 🆕 根據 GroupUid 確認群組歸屬
        public bool IsGroupOwnedByUserByUid(string groupUid, string userId)
        {
            const string sql = "SELECT COUNT(1) FROM SongGroup WHERE GroupUid=@GroupUid AND UserId=@UserId";
            using var conn = new SqlConnection(_connectionString);
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@GroupUid", groupUid);
            cmd.Parameters.AddWithValue("@UserId", userId);
            conn.Open();
            return (int)cmd.ExecuteScalar() > 0;
        }

        // 🆕 根據 GroupUid 取得 GroupId
        public int? GetGroupIdByUid(string groupUid)
        {
            const string sql = "SELECT GroupId FROM SongGroup WHERE GroupUid=@GroupUid";
            using var conn = new SqlConnection(_connectionString);
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@GroupUid", groupUid);
            conn.Open();
            var result = cmd.ExecuteScalar();
            return result != null ? (int)result : null;
        }

        // 確認群組歸屬
        public bool IsGroupOwnedByUser(int groupId, string userId)
        {
            const string sql = "SELECT COUNT(1) FROM SongGroup WHERE GroupId=@GroupId AND UserId=@UserId";
            using var conn = new SqlConnection(_connectionString);
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@GroupId", groupId);
            cmd.Parameters.AddWithValue("@UserId", userId);
            conn.Open();
            return (int)cmd.ExecuteScalar() > 0;
        }

        public async Task<bool> IsSongInGroupAsync(int groupId, string songUid)
        {
            const string query = "SELECT COUNT(1) FROM SongGroupMapping WHERE GroupId = @GroupId AND SongUid = @SongUid";
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            using var command = new SqlCommand(query, connection);
            command.Parameters.AddWithValue("@GroupId", groupId);
            command.Parameters.AddWithValue("@SongUid", songUid);
            var count = Convert.ToInt32(await command.ExecuteScalarAsync() ?? 0);
            return count > 0;
        }

        public async Task AddSongToGroupAsync(int groupId, string songUid)
        {
            const string query = "INSERT INTO SongGroupMapping (GroupId, SongUid) VALUES (@GroupId, @SongUid)";
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            using var command = new SqlCommand(query, connection);
            command.Parameters.AddWithValue("@GroupId", groupId);
            command.Parameters.AddWithValue("@SongUid", songUid);
            await command.ExecuteNonQueryAsync();
        }

        public List<string> GetSongsInGroup(int groupId)
        {
            const string sql = "SELECT SongUid FROM SongGroupMapping WHERE GroupId = @GroupId";
            var songs = new List<string>();
            using var connection = new SqlConnection(_connectionString);
            using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@GroupId", groupId);
            connection.Open();
            using var reader = command.ExecuteReader();
            while (reader.Read()) songs.Add(reader.GetString(0));
            return songs;
        }

        public void RemoveSongFromGroup(int groupId, string songUid)
        {
            const string sql = "DELETE FROM SongGroupMapping WHERE GroupId = @GroupId AND SongUid = @SongUid";
            using var connection = new SqlConnection(_connectionString);
            using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@GroupId", groupId);
            command.Parameters.AddWithValue("@SongUid", songUid);
            connection.Open();
            command.ExecuteNonQuery();
        }

        // B 方案：刪除群組及其 mapping（保護條件：只能刪自己的群組）
        public bool DeleteGroupWithMappings(int groupId, string userId)
        {
            using var conn = new SqlConnection(_connectionString);
            conn.Open();
            using var tx = conn.BeginTransaction(IsolationLevel.ReadCommitted);

            // 確認存在且屬於此使用者
            var chk = new SqlCommand("SELECT COUNT(1) FROM SongGroup WHERE GroupId=@G AND UserId=@U", conn, tx);
            chk.Parameters.AddWithValue("@G", groupId);
            chk.Parameters.AddWithValue("@U", userId);
            if ((int)chk.ExecuteScalar() == 0) { tx.Rollback(); return false; }

            var delMap = new SqlCommand("DELETE FROM SongGroupMapping WHERE GroupId=@G", conn, tx);
            delMap.Parameters.AddWithValue("@G", groupId);
            delMap.ExecuteNonQuery();

            var delGrp = new SqlCommand("DELETE FROM SongGroup WHERE GroupId=@G", conn, tx);
            delGrp.Parameters.AddWithValue("@G", groupId);
            delGrp.ExecuteNonQuery();

            tx.Commit();
            return true;
        }
    }
}
