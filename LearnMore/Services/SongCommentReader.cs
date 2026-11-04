using System.Data;
using System.Data.SqlClient;
using LearnMore.Models;

namespace LearnMore.Services;

public static class SongCommentReadPolicy
{
    public static bool CanRead(bool isPrivate, string requestedSongUid, string commentSongUid,
        int? viewerId, int? authorId, int? uploaderId, bool isManager)
        => string.Equals(requestedSongUid, commentSongUid, StringComparison.Ordinal)
           && (!isPrivate || (viewerId.HasValue &&
               (isManager || viewerId == authorId || viewerId == uploaderId)));
}

public static class SongCommentReader
{
    public static List<CommentModel> Read(string connectionString, string songUid, string? email, string pathBase)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        int? viewerId = null;
        bool isManager = false;
        using (var viewerCommand = new SqlCommand("SELECT Id, Manager FROM Users WHERE Email = @Email", connection))
        {
            viewerCommand.Parameters.Add("@Email", SqlDbType.NVarChar, 500).Value = (object?)email ?? DBNull.Value;
            using var viewer = viewerCommand.ExecuteReader();
            if (viewer.Read())
            {
                viewerId = viewer.GetInt32(0);
                isManager = !viewer.IsDBNull(1) && viewer.GetBoolean(1);
            }
        }

        var comments = new List<CommentModel>();
        using (var command = new SqlCommand(@"
SELECT comments.CommentId, comments.SongUid, comments.UserName, comments.Message,
       comments.TimeStamp, comments.IsPrivate, comments.Avatar,
       author.Id AS AuthorId, song.AddedByUserId
FROM V_Comments comments
INNER JOIN Songs song ON song.SongUid = comments.SongUid
LEFT JOIN Users author ON author.Email = comments.UserEmail
WHERE comments.SongUid = @SongUid
ORDER BY comments.TimeStamp DESC", connection))
        {
            command.Parameters.Add("@SongUid", SqlDbType.NVarChar, 128).Value = songUid;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                string commentSongUid = reader.GetString(1);
                bool isPrivate = !reader.IsDBNull(5) && reader.GetBoolean(5);
                int? authorId = reader.IsDBNull(7) ? null : reader.GetInt32(7);
                int? uploaderId = reader.IsDBNull(8) ? null : reader.GetInt32(8);
                if (!SongCommentReadPolicy.CanRead(isPrivate, songUid, commentSongUid,
                    viewerId, authorId, uploaderId, isManager)) continue;

                string name = reader.IsDBNull(2) ? "訪客" : reader.GetString(2);
                string? avatar = reader.IsDBNull(6) ? null : reader.GetString(6);
                comments.Add(new CommentModel
                {
                    CommentId = reader.GetGuid(0),
                    SongUid = commentSongUid,
                    UserName = name,
                    Message = reader.GetString(3),
                    TimeStamp = reader.GetDateTime(4),
                    IsPrivate = isPrivate,
                    CanViewPrivate = true,
                    Avatar = !string.IsNullOrEmpty(avatar) ? $"{pathBase}{avatar}"
                        : $"{pathBase}/images/{(string.IsNullOrEmpty(name) || name == "訪客" ? "visitor" : "default-avatar")}.png"
                });
            }
        }

        using var replyCommand = new SqlCommand(@"
SELECT replies.ReplyId, replies.CommentId, replies.ReplyMessage, replies.ReplyTime
FROM CommentReplies replies
INNER JOIN Comments parent ON parent.CommentId = replies.CommentId
WHERE parent.SongUid = @SongUid
ORDER BY replies.ReplyTime", connection);
        replyCommand.Parameters.Add("@SongUid", SqlDbType.NVarChar, 128).Value = songUid;
        var readableParents = comments.ToDictionary(comment => comment.CommentId);
        using var replies = replyCommand.ExecuteReader();
        while (replies.Read())
        {
            if (!readableParents.TryGetValue(replies.GetGuid(1), out var parent)) continue;
            parent.Replies.Add(new CommentReplyModel
            {
                ReplyId = replies.GetGuid(0), CommentId = parent.CommentId,
                ReplyMessage = replies.GetString(2), ReplyTime = replies.GetDateTime(3)
            });
        }
        return comments;
    }
}
