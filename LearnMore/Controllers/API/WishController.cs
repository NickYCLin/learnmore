using LearnMore.Models;
using Microsoft.AspNetCore.Mvc;
using System.Data.SqlClient;

namespace LearnMore.Controllers.API
{
    [Route("api/wish")]
    [ApiController]
    public class WishController : ControllerBase
    {
        #region 基本參數
        private readonly string _connectionString;

        public WishController(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection") ?? string.Empty;
        }
        #endregion

        #region 取得所有許願留言
        [HttpGet]
        public IActionResult GetWishes()
        {
            string appBasePath = HttpContext.Request.PathBase;
            List<object> wishes = new List<object>();

            using (SqlConnection conn = new SqlConnection(_connectionString))
            {
                conn.Open();
                int? currentUserId = GetCurrentUserId(conn);
                string query = "SELECT [Id], [Message], [UserId], [NickName], [Avatar], [CreatedAt], [IsOk] FROM [V_Wish] ORDER BY [CreatedAt] DESC";
                using (SqlCommand cmd = new SqlCommand(query, conn))
                using (SqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        int wishUserId = Convert.ToInt32(reader["UserId"]);
                        wishes.Add(new
                        {
                            Id = (int)reader["Id"],
                            Message = reader["Message"].ToString() ?? string.Empty,
                            NickName = reader["NickName"].ToString() ?? string.Empty,
                            Avatar = !string.IsNullOrEmpty(reader["Avatar"].ToString()) ? appBasePath + reader["Avatar"].ToString() : $"{appBasePath}/images/default-avatar.png",
                            CreatedAt = reader["CreatedAt"].ToString() ?? string.Empty,
                            IsOk = (int)reader["IsOk"],
                            CanEdit = currentUserId.HasValue && currentUserId.Value == wishUserId
                        });
                    }
                }
            }

            return Ok(wishes);
        }
        #endregion

        #region 新增許願留言（僅限登入者）
        [HttpPost]
        public IActionResult AddWish([FromBody] WishViewModel newWish)
        {
            if (newWish == null || string.IsNullOrWhiteSpace(newWish.Message))
            {
                return BadRequest(new { message = "許願內容不能為空！" });
            }

            string? userEmail = HttpContext.Session.GetString("Email");
            if (string.IsNullOrEmpty(userEmail))
            {
                return Unauthorized(new { message = "請先登入後再許願！" });
            }

            string? userIp = HttpContext.Connection.RemoteIpAddress?.ToString();

            using (SqlConnection conn = new SqlConnection(_connectionString))
            {
                conn.Open();

                // 取得使用者 ID
                string getUserIdQuery = "SELECT Id FROM [Users] WHERE Email = @Email";
                int? userId = null;

                using (SqlCommand getUserIdCmd = new SqlCommand(getUserIdQuery, conn))
                {
                    getUserIdCmd.Parameters.AddWithValue("@Email", userEmail);
                    object result = getUserIdCmd.ExecuteScalar();
                    if (result != null)
                    {
                        userId = Convert.ToInt32(result);
                    }
                }

                if (userId == null)
                {
                    return Unauthorized(new { message = "使用者不存在，請重新登入！" });
                }

                // 插入許願資料，包括 IP
                string insertWishQuery = "INSERT INTO [Wish] ([Message], [UserId], [CreatedAt], [IsOk], [UserIP]) VALUES (@Message, @UserId, GETDATE(), 0, @UserIP)";
                using (SqlCommand insertWishCmd = new SqlCommand(insertWishQuery, conn))
                {
                    insertWishCmd.Parameters.AddWithValue("@Message", newWish.Message);
                    insertWishCmd.Parameters.AddWithValue("@UserId", userId);
                    insertWishCmd.Parameters.AddWithValue("@UserIP", userIp ?? "Unknown");

                    int insertResult = insertWishCmd.ExecuteNonQuery();
                    if (insertResult > 0)
                    {
                        return Ok(new { message = "許願成功！" });
                    }
                    return BadRequest(new { message = "許願失敗，請稍後再試。" });
                }
            }
        }
        #endregion

        #region 取得特定許願的所有回覆
        [HttpGet("{wishId}/replies")]
        public IActionResult GetReplies(int wishId)
        {
            List<object> replies = new List<object>();
            using (SqlConnection conn = new SqlConnection(_connectionString))
            {
                conn.Open();
                int? currentUserId = GetCurrentUserId(conn);
                string query = @"
            SELECT R.Id, R.Message, R.CreatedAt, R.UserId, U.NickName, U.Avatar
            FROM WishReply R
            JOIN Users U ON R.UserId = U.Id
            WHERE R.WishId = @WishId
            ORDER BY R.CreatedAt ASC";

                using (SqlCommand cmd = new SqlCommand(query, conn))
                {
                    cmd.Parameters.AddWithValue("@WishId", wishId);
                    using (SqlDataReader reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            int replyUserId = Convert.ToInt32(reader["UserId"]);
                            replies.Add(new
                            {
                                Id = (int)reader["Id"],
                                Message = reader["Message"].ToString() ?? string.Empty,
                                NickName = reader["NickName"].ToString() ?? string.Empty,
                                Avatar = string.IsNullOrEmpty(reader["Avatar"].ToString()) ? $"{HttpContext.Request.PathBase}/images/default-avatar.png" : $"{HttpContext.Request.PathBase}{reader["Avatar"]}",
                                CreatedAt = reader["CreatedAt"].ToString() ?? string.Empty,
                                CanEdit = currentUserId.HasValue && currentUserId.Value == replyUserId
                            });
                        }
                    }
                }
            }
            return Ok(replies);
        }

        #endregion

        #region 新增回覆
        [HttpPost("{wishId}/replies")]
        public IActionResult AddReply(int wishId, [FromBody] WishReplyInputModel input)
        {
            if (string.IsNullOrWhiteSpace(input.Message))
                return BadRequest(new { message = "回覆內容不能為空！" });

            string? userEmail = HttpContext.Session.GetString("Email");
            if (string.IsNullOrEmpty(userEmail))
                return Unauthorized(new { message = "請先登入後再留言回覆！" });

            using (SqlConnection conn = new SqlConnection(_connectionString))
            {
                conn.Open();

                string getUserIdQuery = "SELECT Id FROM [Users] WHERE Email = @Email";
                int? userId = null;
                using (SqlCommand cmd = new SqlCommand(getUserIdQuery, conn))
                {
                    cmd.Parameters.AddWithValue("@Email", userEmail);
                    var result = cmd.ExecuteScalar();
                    if (result != null) userId = Convert.ToInt32(result);
                }

                if (userId == null)
                    return Unauthorized(new { message = "使用者不存在" });

                string insertQuery = "INSERT INTO WishReply (WishId, UserId, Message, CreatedAt, UserIP) VALUES (@WishId, @UserId, @Message, GETDATE(), @UserIP)";
                using (SqlCommand cmd = new SqlCommand(insertQuery, conn))
                {
                    cmd.Parameters.AddWithValue("@WishId", wishId);
                    cmd.Parameters.AddWithValue("@UserId", userId);
                    cmd.Parameters.AddWithValue("@Message", input.Message);
                    cmd.Parameters.AddWithValue("@UserIP", HttpContext.Connection.RemoteIpAddress?.ToString() ?? "Unknown");

                    cmd.ExecuteNonQuery();
                }
            }

            return Ok(new { message = "留言回覆成功！" });
        }

        #endregion

        #region 編輯願望留言
        [HttpPut("{wishId}")]
        public IActionResult UpdateWish(int wishId, [FromBody] WishViewModel updatedWish)
        {
            if (string.IsNullOrWhiteSpace(updatedWish.Message))
                return BadRequest(new { message = "內容不能為空！" });

            string? userEmail = HttpContext.Session.GetString("Email");
            if (string.IsNullOrEmpty(userEmail))
                return Unauthorized(new { message = "請先登入！" });

            using (SqlConnection conn = new SqlConnection(_connectionString))
            {
                conn.Open();

                string getUserIdQuery = "SELECT Id FROM Users WHERE Email = @Email";
                int? userId = null;
                using (SqlCommand cmd = new SqlCommand(getUserIdQuery, conn))
                {
                    cmd.Parameters.AddWithValue("@Email", userEmail);
                    var result = cmd.ExecuteScalar();
                    if (result != null) userId = Convert.ToInt32(result);
                }

                if (userId == null)
                    return Unauthorized(new { message = "使用者不存在" });

                // 確保是自己的留言才能修改
                string updateQuery = "UPDATE Wish SET Message = @Message WHERE Id = @WishId AND UserId = @UserId";
                using (SqlCommand cmd = new SqlCommand(updateQuery, conn))
                {
                    cmd.Parameters.AddWithValue("@Message", updatedWish.Message);
                    cmd.Parameters.AddWithValue("@WishId", wishId);
                    cmd.Parameters.AddWithValue("@UserId", userId);
                    int rowsAffected = cmd.ExecuteNonQuery();
                    if (rowsAffected == 0)
                        return Forbid("無權限編輯此留言");
                }
            }

            return Ok(new { message = "修改成功！" });
        }
        #endregion

        #region 刪除願望留言
        [HttpDelete("{wishId}")]
        public IActionResult DeleteWish(int wishId)
        {
            string? userEmail = HttpContext.Session.GetString("Email");
            if (string.IsNullOrEmpty(userEmail))
                return Unauthorized(new { message = "請先登入！" });

            using (SqlConnection conn = new SqlConnection(_connectionString))
            {
                conn.Open();

                string getUserIdQuery = "SELECT Id FROM Users WHERE Email = @Email";
                int? userId = null;
                using (SqlCommand cmd = new SqlCommand(getUserIdQuery, conn))
                {
                    cmd.Parameters.AddWithValue("@Email", userEmail);
                    var result = cmd.ExecuteScalar();
                    if (result != null) userId = Convert.ToInt32(result);
                }

                if (userId == null)
                    return Unauthorized(new { message = "使用者不存在" });

                string deleteQuery = "DELETE FROM Wish WHERE Id = @WishId AND UserId = @UserId";
                using (SqlCommand cmd = new SqlCommand(deleteQuery, conn))
                {
                    cmd.Parameters.AddWithValue("@WishId", wishId);
                    cmd.Parameters.AddWithValue("@UserId", userId);
                    int rowsAffected = cmd.ExecuteNonQuery();
                    if (rowsAffected == 0)
                        return Forbid("無權限刪除此留言");
                }
            }

            return Ok(new { message = "刪除成功！" });
        }
        #endregion

        #region 編輯回覆
        [HttpPut("replies/{replyId}")]
        public IActionResult UpdateReply(int replyId, [FromBody] WishReplyInputModel input)
        {
            if (string.IsNullOrWhiteSpace(input.Message))
                return BadRequest(new { message = "內容不能為空！" });

            string? userEmail = HttpContext.Session.GetString("Email");
            if (string.IsNullOrEmpty(userEmail))
                return Unauthorized(new { message = "請先登入！" });

            using (SqlConnection conn = new SqlConnection(_connectionString))
            {
                conn.Open();

                string getUserIdQuery = "SELECT Id FROM Users WHERE Email = @Email";
                int? userId = null;
                using (SqlCommand cmd = new SqlCommand(getUserIdQuery, conn))
                {
                    cmd.Parameters.AddWithValue("@Email", userEmail);
                    var result = cmd.ExecuteScalar();
                    if (result != null) userId = Convert.ToInt32(result);
                }

                if (userId == null)
                    return Unauthorized(new { message = "使用者不存在" });

                string updateQuery = "UPDATE WishReply SET Message = @Message WHERE Id = @ReplyId AND UserId = @UserId";
                using (SqlCommand cmd = new SqlCommand(updateQuery, conn))
                {
                    cmd.Parameters.AddWithValue("@Message", input.Message);
                    cmd.Parameters.AddWithValue("@ReplyId", replyId);
                    cmd.Parameters.AddWithValue("@UserId", userId);
                    int rowsAffected = cmd.ExecuteNonQuery();
                    if (rowsAffected == 0)
                        return Forbid("無權限編輯此回覆");
                }
            }

            return Ok(new { message = "修改成功！" });
        }
        #endregion

        #region 刪除回覆
        [HttpDelete("replies/{replyId}")]
        public IActionResult DeleteReply(int replyId)
        {
            string? userEmail = HttpContext.Session.GetString("Email");
            if (string.IsNullOrEmpty(userEmail))
                return Unauthorized(new { message = "請先登入！" });

            using (SqlConnection conn = new SqlConnection(_connectionString))
            {
                conn.Open();

                string getUserIdQuery = "SELECT Id FROM Users WHERE Email = @Email";
                int? userId = null;
                using (SqlCommand cmd = new SqlCommand(getUserIdQuery, conn))
                {
                    cmd.Parameters.AddWithValue("@Email", userEmail);
                    var result = cmd.ExecuteScalar();
                    if (result != null) userId = Convert.ToInt32(result);
                }

                if (userId == null)
                    return Unauthorized(new { message = "使用者不存在" });

                string deleteQuery = "DELETE FROM WishReply WHERE Id = @ReplyId AND UserId = @UserId";
                using (SqlCommand cmd = new SqlCommand(deleteQuery, conn))
                {
                    cmd.Parameters.AddWithValue("@ReplyId", replyId);
                    cmd.Parameters.AddWithValue("@UserId", userId);
                    int rowsAffected = cmd.ExecuteNonQuery();
                    if (rowsAffected == 0)
                        return Forbid("無權限刪除此回覆");
                }
            }

            return Ok(new { message = "刪除成功！" });
        }

        #endregion

        private int? GetCurrentUserId(SqlConnection conn)
        {
            string? userEmail = HttpContext.Session.GetString("Email");
            if (string.IsNullOrEmpty(userEmail))
            {
                return null;
            }

            using (SqlCommand cmd = new SqlCommand("SELECT Id FROM Users WHERE Email = @Email", conn))
            {
                cmd.Parameters.AddWithValue("@Email", userEmail);
                object? result = cmd.ExecuteScalar();
                return result == null || result == DBNull.Value ? null : Convert.ToInt32(result);
            }
        }
    }
}
