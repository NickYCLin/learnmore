using HtmlAgilityPack;
using LearnMore.Controllers;
using LearnMore.Models;
using Microsoft.AspNetCore.Mvc;
using System.Data.SqlClient;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using static LearnMore.Controllers.KuroshiroController;

namespace LearnMore.Controllers.API
{
    [ApiController]
    [Route("api/[controller]")]
    public class CrawlerController : ControllerBase
    {
        #region 基本參數
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;

        public CrawlerController(IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
        }
        #endregion

        #region 羅馬爬蟲
        [HttpPost("convertToRomaji")]
        public async Task<IActionResult> ConvertToRomaji([FromBody] ConversionRequest request)
        {
            if (!ControllerAccessGuard.IsSignedIn(this))
            {
                return ControllerAccessGuard.LoginRequired(this);
            }

            if (request == null || string.IsNullOrWhiteSpace(request.Text))
            {
                return BadRequest("請提供要轉換的日文文本");
            }

            // 使用預先配置的 HttpClient（已配置 CookieContainer）
            var client = _httpClientFactory.CreateClient("jcinfo");

            // 1. GET 頁面以取得 CSRF token
            var getResponse = await client.GetAsync("https://www.jcinfo.net/zh-hans/tools/ja-roman");
            if (!getResponse.IsSuccessStatusCode)
            {
                return StatusCode((int)getResponse.StatusCode, "GET 頁面失敗");
            }
            var getHtml = await getResponse.Content.ReadAsStringAsync();

            var doc = new HtmlDocument();
            doc.LoadHtml(getHtml);
            var tokenNode = doc.DocumentNode.SelectSingleNode("//meta[@name='csrf-token']");
            if (tokenNode == null)
            {
                return StatusCode(500, "無法取得 CSRF token");
            }
            var csrfToken = tokenNode.GetAttributeValue("content", "");

            // 2. 設定 POST 請求 header
            client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrfToken);

            // 3. 模擬表單提交
            var formData = new Dictionary<string, string>
    {
        { "_token", csrfToken },
        { "text", request.Text }
    };
            var content = new FormUrlEncodedContent(formData);
            var postResponse = await client.PostAsync("https://www.jcinfo.net/zh-hans/tools/ja-roman", content);
            if (!postResponse.IsSuccessStatusCode)
            {
                var errorContent = await postResponse.Content.ReadAsStringAsync();
                return StatusCode((int)postResponse.StatusCode, $"呼叫外部服務失敗，狀態碼：{postResponse.StatusCode}，訊息：{errorContent}");
            }

            var postHtml = await postResponse.Content.ReadAsStringAsync();
            var postDoc = new HtmlDocument();
            postDoc.LoadHtml(postHtml);

            // 4. jcinfo 新版：取第二個 div.line（純 romaji 格式）
            // 新版結構：第一個 div.line = 帶括號格式，第二個 = 純 romaji
            string romanizedText;
            var lineNodes = postDoc.DocumentNode.SelectNodes("//div[@class='line']");
            if (lineNodes != null && lineNodes.Count >= 2)
            {
                // 取第二個 div.line（純 romaji）
                romanizedText = lineNodes[1].InnerText;
            }
            else if (lineNodes != null && lineNodes.Count == 1)
            {
                // 只有一個，直接用
                romanizedText = lineNodes[0].InnerText;
            }
            else
            {
                // Fallback：舊版 morpheme span 格式
                var resultNode = postDoc.DocumentNode.SelectSingleNode("//div[contains(@class, '_result') and not(contains(@class, '_result-ruby'))]");
                if (resultNode == null) return NotFound("未能解析出轉換結果");
                var spanNodes = resultNode.SelectNodes(".//span[@class='morpheme']");
                romanizedText = spanNodes != null
                    ? string.Join("", spanNodes.Select(s => s.InnerText))
                    : resultNode.InnerText;
            }

            // 使用 HtmlDecode 處理特殊 HTML 編碼（例如 &#039;）
            romanizedText = WebUtility.HtmlDecode(romanizedText);

            return Ok(new { result = romanizedText });
        }
        #endregion

        #region 日文注音爬蟲
        [HttpPost("convertToKana")]
        public async Task<IActionResult> ConvertToKana([FromBody] ConversionRequest request)
        {
            if (!ControllerAccessGuard.IsSignedIn(this))
            {
                return ControllerAccessGuard.LoginRequired(this);
            }

            if (request == null || string.IsNullOrWhiteSpace(request.Text))
            {
                return BadRequest("請提供要標注的日文文本");
            }

            // 使用預先配置的 HttpClient（已配置 CookieContainer）
            var client = _httpClientFactory.CreateClient("jcinfo");

            // 1. 先 GET 頁面，取得 CSRF token 與隱藏欄位 _token 的值
            var getResponse = await client.GetAsync("https://www.jcinfo.net/zh-hans/tools/kana");
            if (!getResponse.IsSuccessStatusCode)
            {
                return StatusCode((int)getResponse.StatusCode, "GET 頁面失敗");
            }
            var getHtml = await getResponse.Content.ReadAsStringAsync();

            // 使用 HtmlAgilityPack 解析 CSRF token
            var doc = new HtmlDocument();
            doc.LoadHtml(getHtml);
            var tokenNode = doc.DocumentNode.SelectSingleNode("//meta[@name='csrf-token']");
            if (tokenNode == null)
            {
                return StatusCode(500, "無法取得 CSRF token");
            }
            var csrfToken = tokenNode.GetAttributeValue("content", "");

            // 2. 設定 POST 請求 header（通常使用 X-CSRF-TOKEN）
            client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrfToken);

            // 3. 模擬表單提交，傳送隱藏欄位 _token 與 textarea 的內容
            var formData = new Dictionary<string, string>
    {
        { "_token", csrfToken },
        { "text", request.Text }
    };
            var content = new FormUrlEncodedContent(formData);

            // 4. 模擬點擊「转换平假名」按鈕提交表單
            var postResponse = await client.PostAsync("https://www.jcinfo.net/zh-hans/tools/kana", content);
            if (!postResponse.IsSuccessStatusCode)
            {
                var errorContent = await postResponse.Content.ReadAsStringAsync();
                return StatusCode((int)postResponse.StatusCode, $"呼叫外部服務失敗，狀態碼：{postResponse.StatusCode}，訊息：{errorContent}");
            }

            var postHtml = await postResponse.Content.ReadAsStringAsync();
            var postDoc = new HtmlDocument();
            postDoc.LoadHtml(postHtml);

            // 5. 取第一個 div.line（ruby 格式，jcinfo 新版結構）
            var lineNode = postDoc.DocumentNode.SelectSingleNode("//div[@class='line']");
            if (lineNode == null)
            {
                // fallback：舊版 morpheme span 格式
                var resultNode = postDoc.DocumentNode.SelectSingleNode("//div[contains(@class, '_result') and contains(@class, '_result-ruby')]");
                if (resultNode == null) return NotFound("未能解析出標注結果");
                var spanNodes = resultNode.SelectNodes(".//span[@class='morpheme']");
                if (spanNodes == null || spanNodes.Count == 0) return NotFound("未能解析出標注結果的 span");
                var sbOld = new StringBuilder();
                foreach (var span in spanNodes)
                {
                    var html2 = span.InnerHtml;
                    var cleaned2 = Regex.Replace(html2, @"<span[^>]*>(.*?)</span>", "$1", RegexOptions.Singleline);
                    sbOld.Append(cleaned2);
                }
                var cleanHtml2 = sbOld.ToString().Trim();
                return Ok(new { result = cleanHtml2 });
            }

            // 新版：直接取 div.line 的 InnerHtml（保留 ruby 標籤，移除 rp 標籤）
            var rawHtml = lineNode.InnerHtml;
            // 移除 <rp>[</rp> 和 <rp>]</rp>
            var cleanHtml = Regex.Replace(rawHtml, @"<rp>[^<]*</rp>", "", RegexOptions.Singleline);

            return Ok(new { result = cleanHtml });
        }
        #endregion

        #region 批次翻譯
        [HttpPost("convertAndUpdateOptimized")]
        public async Task<IActionResult> ConvertAndUpdateOptimized([FromBody] ConvertRequest request)
        {
            var denied = await ControllerAccessGuard.RequireManagerAsync(this, _configuration, HttpContext.RequestAborted);
            if (denied != null)
            {
                return denied;
            }

            // 檢查必須的參數是否提供
            if (request == null || string.IsNullOrEmpty(request.Column) || string.IsNullOrEmpty(request.SongUid))
            {
                return BadRequest("請提供有效的參數");
            }

            if (!IsValidSongUid(request.SongUid) || !IsWritableLyricsColumn(request.Column))
            {
                return BadRequest("請提供有效的參數");
            }

            // 組成資料表名稱
            string tableName = $"Songs_{request.SongUid}";

            using (SqlConnection connection = new SqlConnection(_configuration.GetConnectionString("DefaultConnection")))
            {
                await connection.OpenAsync();
                await EnsureColumnExists(connection, tableName, request.Column);

                // 1. 讀取所有需要轉換的日文歌詞
                string selectQuery = $"SELECT LyricID, Japanese FROM [{tableName}] WHERE Japanese IS NOT NULL ORDER BY LyricID";
                var lyricsList = new List<(int LyricID, string Japanese)>();

                using (SqlCommand selectCommand = new SqlCommand(selectQuery, connection))
                using (SqlDataReader reader = await selectCommand.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        lyricsList.Add((reader.GetInt32(0), reader.GetString(1)));
                    }
                }

                if (!lyricsList.Any()) return Ok("無需更新");

                // 將所有日文歌詞用 @ 間隔串成一個大字串
                string combinedText = string.Join("@", lyricsList.Select(x => x.Japanese));
                var input = new ConversionRequest { Text = combinedText };

                // 2. 根據 Column 呼叫對應的 API
                OkObjectResult? convertResult = null;
                if (request.Column == "Roman")
                {
                    convertResult = await ConvertToRomaji(input) as OkObjectResult;
                }
                else if (request.Column == "JapaneseRuby")
                {
                    convertResult = await ConvertToKana(input) as OkObjectResult;
                }
                else
                {
                    return BadRequest("未知的轉換類型");
                }

                if (convertResult?.Value == null)
                {
                    return BadRequest("轉換失敗");
                }

                // 取得轉換後的字串
                string convertedString = ((dynamic)convertResult.Value).result;
                string[] convertedArray;

                // 3. 根據 Column 分隔轉換結果並移除前後空白
                if (request.Column == "Roman")
                {
                    convertedArray = convertedString
                        .Split('@')
                        .Select(s => s.Trim())
                        .ToArray();
                }
                else // JapaneseRuby
                {
                    convertedArray = convertedString
                        .Split('@')
                        .Select(s => s.Trim())
                        .ToArray();
                }

                if (convertedArray.Length != lyricsList.Count)
                {
                    return BadRequest(
                        $"轉換結果筆數不一致，預期 {lyricsList.Count} 筆，實際 {convertedArray.Length} 筆，未更新任何資料");
                }

                // 4. 對應每一句轉換結果準備更新資料庫
                var updateList = lyricsList
                    .Select((lyric, index) => new
                    {
                        lyric.LyricID,
                        ConvertedText = convertedArray[index]
                    })
                    .Where(x => !string.IsNullOrEmpty(x.ConvertedText))
                    .ToList();

                if (!updateList.Any()) return Ok("無需更新");

                // 5. 批次更新資料庫
                using (SqlTransaction transaction = connection.BeginTransaction())
                {
                    using (SqlCommand updateCommand = new SqlCommand("", connection, transaction))
                    {
                        var updateQuery = new StringBuilder($"UPDATE [{tableName}] SET [{request.Column}] = CASE LyricID ");

                        foreach (var update in updateList)
                        {
                            updateQuery.Append($"WHEN {update.LyricID} THEN @Text_{update.LyricID} ");
                            updateCommand.Parameters.AddWithValue($"@Text_{update.LyricID}", update.ConvertedText);
                        }

                        updateQuery.Append("END WHERE LyricID IN (");
                        updateQuery.Append(string.Join(",", updateList.Select(x => x.LyricID)));
                        updateQuery.Append(");");

                        updateCommand.CommandText = updateQuery.ToString();
                        await updateCommand.ExecuteNonQueryAsync();
                    }

                    await transaction.CommitAsync();
                }
            }

            return Ok(new { success = true, message = "轉換與更新完成" });
        }

        /// <summary>
        /// 確保資料表中存在指定的欄位，若不存在則新增
        /// </summary>
        private async Task EnsureColumnExists(SqlConnection connection, string tableName, string columnName)
        {
            string checkColumnQuery = $@"
        SELECT COUNT(*)
        FROM INFORMATION_SCHEMA.COLUMNS
        WHERE TABLE_NAME = @TableName AND COLUMN_NAME = @ColumnName";

            using (SqlCommand checkCommand = new SqlCommand(checkColumnQuery, connection))
            {
                checkCommand.Parameters.AddWithValue("@TableName", tableName);
                checkCommand.Parameters.AddWithValue("@ColumnName", columnName);

                int columnCount = Convert.ToInt32(await checkCommand.ExecuteScalarAsync() ?? 0);

                if (columnCount == 0)
                {
                    // 若欄位不存在，則新增
                    string alterTableQuery = $"ALTER TABLE [{tableName}] ADD [{columnName}] NVARCHAR(MAX)";

                    using (SqlCommand alterCommand = new SqlCommand(alterTableQuery, connection))
                    {
                        await alterCommand.ExecuteNonQueryAsync();
                    }
                }
            }
        }
        #endregion

        #region 全部翻譯全資料表
        [HttpPost("convertAllAndUpdateOptimized")]
        public async Task<IActionResult> ConvertAllAndUpdateOptimized([FromBody] ConvertRequest request)
        {
            var denied = await ControllerAccessGuard.RequireManagerAsync(this, _configuration, HttpContext.RequestAborted);
            if (denied != null)
            {
                return denied;
            }

            if (request == null || string.IsNullOrEmpty(request.Column))
            {
                return BadRequest("請提供有效的參數");
            }

            if (!IsWritableLyricsColumn(request.Column))
            {
                return BadRequest("請提供有效的參數");
            }

            var connectionString = _configuration.GetConnectionString("DefaultConnection");
            var songUidList = new List<string>();

            // 1. 撈取全部的 SongUid (此處 SongUid 為 string)
            using (SqlConnection connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();
                string query = "SELECT [SongUid] FROM [Songs]";
                using (SqlCommand cmd = new SqlCommand(query, connection))
                using (SqlDataReader reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        songUidList.Add(reader.GetString(0));
                    }
                }
            }

            if (!songUidList.Any())
            {
                return Ok("無任何 SongUid 需處理");
            }

            // 2. 依序處理每一個 SongUid 的資料表
            foreach (var songUid in songUidList)
            {
                string tableName = $"Songs_{songUid}";

                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    await connection.OpenAsync();

                    // 確保指定的欄位存在
                    await EnsureColumnExists(connection, tableName, request.Column);

                    // 讀取所有需要轉換的日文歌詞
                    string selectQuery = $"SELECT LyricID, Japanese FROM [{tableName}] WHERE Japanese IS NOT NULL ORDER BY LyricID";
                    var lyricsList = new List<(int LyricID, string Japanese)>();

                    using (SqlCommand selectCommand = new SqlCommand(selectQuery, connection))
                    using (SqlDataReader reader = await selectCommand.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            lyricsList.Add((reader.GetInt32(0), reader.GetString(1)));
                        }
                    }

                    if (!lyricsList.Any())
                    {
                        // 無資料可更新，處理下一個 SongUid
                        continue;
                    }

                    // 將所有日文歌詞以 "@" 作為分隔符串成一個大字串
                    string combinedText = string.Join("@", lyricsList.Select(x => x.Japanese));
                    var input = new ConversionRequest { Text = combinedText };

                    // 根據 request.Column 呼叫不同的轉換 API
                    OkObjectResult? convertResult = null;
                    if (request.Column == "Roman")
                    {
                        convertResult = await ConvertToRomaji(input) as OkObjectResult;
                    }
                    else if (request.Column == "JapaneseRuby")
                    {
                        convertResult = await ConvertToKana(input) as OkObjectResult;
                    }
                    if (convertResult?.Value == null)
                    {
                        // 轉換失敗，跳過此筆資料
                        continue;
                    }

                    string convertedString = ((dynamic)convertResult.Value).result;
                    string[] convertedArray;

                    // 根據 Column 來拆分轉換後的字串並移除前後空白
                    if (request.Column == "Roman")
                    {
                        convertedArray = convertedString
                            .Split('@')
                            .Select(s => s.Trim())
                            .ToArray();
                    }
                    else if (request.Column == "JapaneseRuby")
                    {
                        convertedArray = convertedString
                            .Split('@')
                            .Select(s => s.Trim())
                            .ToArray();
                    }
                    else
                    {
                        // 其他情況不支援，跳過此筆
                        continue;
                    }

                    // 確保轉換後的句數與原本句數一致
                    if (convertedArray.Length != lyricsList.Count)
                    {
                        return BadRequest("轉換結果與原本句數不一致");
                    }

                    var updateList = lyricsList
                        .Select((lyric, index) => new
                        {
                            lyric.LyricID,
                            ConvertedText = convertedArray[index]
                        })
                        .Where(x => !string.IsNullOrEmpty(x.ConvertedText))
                        .ToList();

                    if (!updateList.Any())
                    {
                        continue;
                    }

                    // 批次更新資料庫
                    using (SqlTransaction transaction = connection.BeginTransaction())
                    {
                        using (SqlCommand updateCommand = new SqlCommand("", connection, transaction))
                        {
                            var updateQuery = new StringBuilder($"UPDATE [{tableName}] SET [{request.Column}] = CASE LyricID ");
                            foreach (var update in updateList)
                            {
                                updateQuery.Append($"WHEN {update.LyricID} THEN @Text_{update.LyricID} ");
                                updateCommand.Parameters.AddWithValue($"@Text_{update.LyricID}", update.ConvertedText);
                            }
                            updateQuery.Append("END WHERE LyricID IN (");
                            updateQuery.Append(string.Join(",", updateList.Select(x => x.LyricID)));
                            updateQuery.Append(");");

                            updateCommand.CommandText = updateQuery.ToString();
                            await updateCommand.ExecuteNonQueryAsync();
                        }
                        await transaction.CommitAsync();
                    }
                }
            }

            return Ok(new { success = true, message = "所有歌詞轉換與更新完成" });
        }

        private static bool IsValidSongUid(string? songUid)
            => !string.IsNullOrWhiteSpace(songUid) && Regex.IsMatch(songUid, "^[A-Za-z0-9_-]+$");

        private static bool IsWritableLyricsColumn(string? columnName)
            => string.Equals(columnName, "Roman", StringComparison.Ordinal)
               || string.Equals(columnName, "JapaneseRuby", StringComparison.Ordinal);

        #endregion
    }
}
