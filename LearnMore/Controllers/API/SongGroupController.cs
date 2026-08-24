using LearnMore.Repository;
using Microsoft.AspNetCore.Mvc;

namespace LearnMore.Controllers.API
{
    [ApiController]
    [Route("api/[controller]")]
    public class SongGroupController : ControllerBase
    {
        private readonly SongGroupRepository _repository;
        private readonly string _connectionString;

        public SongGroupController(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection") ?? string.Empty;
            _repository = new SongGroupRepository(_connectionString);
        }

        // 建立群組
        [HttpPost("create")]
        public IActionResult CreateGroup([FromForm] string groupName)
        {
            var userId = HttpContext.Session.GetString("UserId");
            if (string.IsNullOrEmpty(userId)) return Unauthorized();

            var groupId = _repository.CreateGroup(userId, groupName);

            // 🆕 取得剛建立的群組資訊（含 GroupUid）
            var groups = _repository.GetGroups(userId);
            var newGroup = groups.FirstOrDefault(g => g.GroupId == groupId);

            return Ok(new
            {
                groupId = groupId,
                groupUid = newGroup?.GroupUid ?? "",
                groupName = newGroup?.GroupName ?? groupName
            });
        }

        // 撈取使用者的群組
        [HttpGet("list")]
        public IActionResult GetGroups()
        {
            var userId = HttpContext.Session.GetString("UserId");
            if (string.IsNullOrEmpty(userId)) return Unauthorized();

            var groups = _repository.GetGroups(userId)
                        .Select(g => new
                        {
                            groupId = g.GroupId,
                            groupUid = g.GroupUid,
                            groupName = g.GroupName
                        })
                        .ToList();

            return Ok(groups);
        }

        // 🆕 取得所有使用者的群組（包含 GroupUid 和歌曲數量）- 供導覽列群組播放選單使用
        [HttpGet("listall")]
        public IActionResult GetAllGroupsWithUid()
        {
            var userId = HttpContext.Session.GetString("UserId");
            if (string.IsNullOrEmpty(userId))
            {
                // 未登入返回空陣列
                return Ok(new List<object>());
            }

            var groups = _repository.GetGroups(userId);
            var result = groups.Select(g =>
            {
                var songCount = _repository.GetSongsInGroup(g.GroupId).Count;
                return new
                {
                    GroupId = g.GroupId,
                    GroupUid = g.GroupUid,
                    GroupName = g.GroupName,
                    SongCount = songCount
                };
            }).ToList();

            return Ok(result);
        }

        // ✅ 新增：回傳「此使用者加入過群組的所有 SongUid」
        [HttpGet("GetJoinedUids")]
        public IActionResult GetJoinedUids()
        {
            var userId = HttpContext.Session.GetString("UserId");
            if (string.IsNullOrEmpty(userId)) return Unauthorized();

            var uids = _repository.GetJoinedSongUids(userId);
            return Ok(uids); // List<string>
        }

        // ✅ 新增：查詢某首歌已加入的群組（回傳群組 Id 陣列）
        [HttpGet("groupsContainingSong")]
        public IActionResult GetGroupsContainingSong([FromQuery] string songUid)
        {
            var userId = HttpContext.Session.GetString("UserId");
            if (string.IsNullOrEmpty(userId)) return Unauthorized();
            if (string.IsNullOrWhiteSpace(songUid)) return BadRequest();

            var groupIds = _repository.GetUserGroupIdsForSong(userId, songUid);
            return Ok(groupIds);
        }

        // 加歌曲進群組
        [HttpPost("addsong")]
        [Produces("application/json")]
        public async Task<IActionResult> AddSongToGroup([FromForm] int groupId, [FromForm] string songUid)
        {
            var userId = HttpContext.Session.GetString("UserId");
            if (string.IsNullOrEmpty(userId))
                return Unauthorized(new { message = "未登入" });

            // 確認群組是此使用者的
            if (!_repository.IsGroupOwnedByUser(groupId, userId))
                return Forbid();

            if (await _repository.IsSongInGroupAsync(groupId, songUid))
                return StatusCode(409, new { message = "這首歌已在群組中" });

            try
            {
                await _repository.AddSongToGroupAsync(groupId, songUid);
                return Ok(new { success = true, message = "已加入群組" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, message = "加入失敗", error = ex.Message });
            }
        }

        // 抓群組內的歌曲
        [HttpGet("songs")]
        public IActionResult GetSongsInGroup([FromQuery] int groupId)
        {
            var songs = _repository.GetSongsInGroup(groupId);
            return Ok(songs);
        }

        // 刪除群組中的歌曲
        [HttpPost("removesong")]
        public IActionResult RemoveSongFromGroup([FromForm] int groupId, [FromForm] string songUid)
        {
            var userId = HttpContext.Session.GetString("UserId");
            if (string.IsNullOrEmpty(userId)) return Unauthorized();
            if (!_repository.IsGroupOwnedByUser(groupId, userId)) return Forbid();

            _repository.RemoveSongFromGroup(groupId, songUid);
            return Ok();
        }

        // ✅ 先前 B 方案：刪除整個群組（連 mapping）
        [HttpPost("delete")]
        public IActionResult DeleteGroup([FromForm] int groupId)
        {
            var userId = HttpContext.Session.GetString("UserId");
            if (string.IsNullOrEmpty(userId)) return Unauthorized();

            if (!_repository.IsGroupOwnedByUser(groupId, userId))
                return Forbid(); // 不是你的群組

            var ok = _repository.DeleteGroupWithMappings(groupId, userId);
            if (!ok) return NotFound();

            return Ok(new { deleted = true });
        }
    }
}
