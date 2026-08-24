using LearnMore.Controllers;
using Microsoft.AspNetCore.Mvc;
using System.Data.SqlClient;
using System.Text.RegularExpressions;

namespace LearnMore.Controllers.API
{
    [ApiController]
    [Route("api/[controller]")]
    public class ToolsController : ControllerBase
    {
        #region 基本參數
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;

        public ToolsController(IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
        }
        #endregion

        #region 移除 <span> 標籤
        [HttpPost("RemoveSpan")]
        public IActionResult RemoveSpan([FromBody] string inputHtml)
        {
            if (!ControllerAccessGuard.IsSignedIn(this))
            {
                return ControllerAccessGuard.LoginRequired(this);
            }

            if (string.IsNullOrEmpty(inputHtml))
                return BadRequest("Input is empty.");

            // 移除所有 <span> 外層，保留內部內容
            string result = Regex.Replace(inputHtml, @"<span[^>]*>(.*?)</span>", "$1", RegexOptions.Singleline);

            return Ok(result);
        }
        #endregion

        [HttpPost("UpdateSongRemoveSpan/{songUid}")]
        public async Task<IActionResult> UpdateSongRemoveSpan(string songUid)
        {
            var denied = await ControllerAccessGuard.RequireManagerAsync(this, _configuration, HttpContext.RequestAborted);
            if (denied != null)
            {
                return denied;
            }

            if (string.IsNullOrWhiteSpace(songUid) || !Regex.IsMatch(songUid, "^[A-Za-z0-9_-]+$"))
            {
                return BadRequest("Invalid songUid.");
            }

            return Ok(await UpdateSongRemoveSpanInternal(songUid));
        }

        private async Task<bool> UpdateSongRemoveSpanInternal(string songUid)
        {
            var tableName = $"Songs_{songUid}";
            using (var connection = new SqlConnection(_configuration.GetConnectionString("DefaultConnection")))
            {
                await connection.OpenAsync();

                var selectCommand = new SqlCommand($"SELECT LyricID, JapaneseRuby FROM [{tableName}]", connection);
                var reader = await selectCommand.ExecuteReaderAsync();

                var updates = new List<(int Id, string CleanedHtml)>();

                while (await reader.ReadAsync())
                {
                    var id = Convert.ToInt32(reader["LyricID"]);
                    var originalHtml = reader["JapaneseRuby"]?.ToString() ?? "";
                    var cleanedHtml = Regex.Replace(originalHtml, @"<span[^>]*>(.*?)</span>", "$1", RegexOptions.Singleline);
                    updates.Add((id, cleanedHtml));
                }
                reader.Close();

                foreach (var (id, cleanedHtml) in updates)
                {
                    var updateCommand = new SqlCommand($"UPDATE [{tableName}] SET JapaneseRuby = @cleanedHtml WHERE LyricID = @id", connection);
                    updateCommand.Parameters.AddWithValue("@cleanedHtml", cleanedHtml);
                    updateCommand.Parameters.AddWithValue("@id", id);
                    await updateCommand.ExecuteNonQueryAsync();
                }
            }

            return true;
        }

        [HttpPost("UpdateAllSongsRemoveSpan")]
        public async Task<IActionResult> UpdateAllSongsRemoveSpan()
        {
            var denied = await ControllerAccessGuard.RequireManagerAsync(this, _configuration, HttpContext.RequestAborted);
            if (denied != null)
            {
                return denied;
            }

            var updatedSongList = new List<string>();
            var failedSongList = new List<string>();

            try
            {
                using (var connection = new SqlConnection(_configuration.GetConnectionString("DefaultConnection")))
                {
                    await connection.OpenAsync();

                    var getUidsCommand = new SqlCommand("SELECT [SongUid] FROM [Songs]", connection);
                    var reader = await getUidsCommand.ExecuteReaderAsync();

                    var songUids = new List<string>();
                    while (await reader.ReadAsync())
                    {
                        var songUid = reader["SongUid"]?.ToString();
                        if (!string.IsNullOrEmpty(songUid))
                            songUids.Add(songUid);
                    }
                    reader.Close();

                    // 逐一呼叫 UpdateSongRemoveSpan 方法
                    foreach (var songUid in songUids)
                    {
                        try
                        {
                            var result = await UpdateSongRemoveSpanInternal(songUid);
                            if (result)
                                updatedSongList.Add(songUid);
                            else
                                failedSongList.Add($"{songUid}: update failed");
                        }
                        catch (Exception ex)
                        {
                            failedSongList.Add($"{songUid}: {ex.Message}");
                        }
                    }
                }

                return Ok(new
                {
                    status = "completed",
                    updatedCount = updatedSongList.Count,
                    updatedSongs = updatedSongList,
                    failed = failedSongList
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { status = "error", message = ex.Message });
            }
        }

    }
}
