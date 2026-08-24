using System.Data.SqlClient;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LearnMore.Services;
using Microsoft.AspNetCore.Mvc;
using IConfiguration = Microsoft.Extensions.Configuration.IConfiguration;

namespace LearnMore.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class KuroshiroController : ControllerBase
    {
        private readonly IWebHostEnvironment _env;
        private readonly IConfiguration _configuration;
        private readonly JapaneseRubyGeneratorService _rubyGenerator;

        public KuroshiroController(
            IWebHostEnvironment env,
            IConfiguration configuration,
            IHttpClientFactory httpClientFactory,
            JapaneseRubyGeneratorService rubyGenerator)
        {
            _env = env;
            _configuration = configuration;
            _rubyGenerator = rubyGenerator;
        }

        [HttpPost("convert")]
        public async Task<IActionResult> Convert([FromBody] TextInput input)
        {
            if (!ControllerAccessGuard.IsSignedIn(this))
            {
                return ControllerAccessGuard.LoginRequired(this);
            }

            if (input == null || string.IsNullOrWhiteSpace(input.Text))
            {
                return BadRequest("輸入文字不可為空");
            }

            var result = await ConvertSingleLineAsync(input.Text, input.Mode, input.To);
            return Ok(new { result });
        }

        [HttpPost("convertLines")]
        public async Task<IActionResult> ConvertLines([FromBody] BatchTextInput input)
        {
            if (!ControllerAccessGuard.IsSignedIn(this))
            {
                return ControllerAccessGuard.LoginRequired(this);
            }

            if (input?.Texts == null || input.Texts.Count == 0)
            {
                return BadRequest("請提供要轉換的文字陣列");
            }

            var results = await ConvertLinesAsync(input.Texts, input.Mode, input.To);
            return Ok(new { results });
        }

        [HttpPost("convertAndUpdate")]
        public async Task<IActionResult> ConvertAndUpdate([FromBody] ConvertRequest request)
        {
            var denied = await ControllerAccessGuard.RequireManagerAsync(this, _configuration, HttpContext.RequestAborted);
            if (denied != null)
            {
                return denied;
            }

            return await ConvertAndUpdateInternalAsync(request);
        }

        [NonAction]
        public async Task<IActionResult> ConvertAndUpdateInternalAsync(ConvertRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Column))
            {
                return BadRequest("請提供有效的參數");
            }

            if (!IsValidSongUid(request.SongUid) || !IsWritableLyricsColumn(request.Column))
            {
                return BadRequest("請提供有效的參數");
            }

            var lyricsList = await LoadLyricsAsync(request.SongUid);
            if (!lyricsList.Any())
            {
                return Ok("無需更新");
            }

            var convertedLines = await ConvertLinesAsync(
                lyricsList.Select(x => x.Japanese).ToList(),
                request.Mode,
                request.To);

            if (convertedLines.Count != lyricsList.Count)
            {
                return BadRequest("轉換結果與原本句數不一致");
            }

            var updateList = lyricsList
                .Select((lyric, index) => new LyricUpdate(lyric.LyricID, convertedLines[index]))
                .Where(x => !string.IsNullOrWhiteSpace(x.ConvertedText))
                .ToList();

            if (!updateList.Any())
            {
                return Ok("無需更新");
            }

            await UpdateLyricsColumnAsync(request.SongUid, request.Column, updateList);
            return Ok(new { success = true, message = "轉換與更新完成" });
        }

        [HttpPost("convertAndUpdateOptimized")]
        public async Task<IActionResult> ConvertAndUpdateOptimized([FromBody] ConvertRequest request)
        {
            var denied = await ControllerAccessGuard.RequireManagerAsync(this, _configuration, HttpContext.RequestAborted);
            if (denied != null)
            {
                return denied;
            }

            return await ConvertAndUpdateInternalAsync(request);
        }

        public async Task<string> ConvertSingleLineAsync(string text, string? mode = null, string? to = null)
        {
            var results = await ConvertLinesAsync(new List<string> { text }, mode, to);
            return results.FirstOrDefault() ?? string.Empty;
        }

        public Task<List<string>> ConvertLinesAsync(
            IReadOnlyCollection<string> texts,
            string? mode = null,
            string? to = null)
        {
            if (texts.Count == 0)
            {
                return Task.FromResult(new List<string>());
            }

            string effectiveMode = string.IsNullOrWhiteSpace(mode) ? "furigana" : mode;
            string effectiveTo = string.IsNullOrWhiteSpace(to) ? "hiragana" : to;

            if (effectiveMode.Equals("furigana", StringComparison.OrdinalIgnoreCase) &&
                effectiveTo.Equals("hiragana", StringComparison.OrdinalIgnoreCase))
            {
                var results = texts
                    .Select(_rubyGenerator.ConvertToRubyHtml)
                    .Select(JapaneseRubySanitizer.NormalizeRubyHtml)
                    .ToList();

                return Task.FromResult(results);
            }

            return ConvertWithNodeAsync(texts, effectiveMode, effectiveTo);
        }

        private async Task<List<string>> ConvertWithNodeAsync(
            IReadOnlyCollection<string> texts,
            string effectiveMode,
            string effectiveTo)
        {
            string scriptPath = Path.Combine(_env.WebRootPath, "js", "index.js");
            if (!System.IO.File.Exists(scriptPath))
            {
                throw new FileNotFoundException($"找不到腳本: {scriptPath}");
            }

            string payload = JsonSerializer.Serialize(texts);

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "node",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                }
            };

            process.StartInfo.ArgumentList.Add(scriptPath);
            process.StartInfo.ArgumentList.Add(payload);
            process.StartInfo.ArgumentList.Add("--json-lines");
            process.StartInfo.ArgumentList.Add("--mode");
            process.StartInfo.ArgumentList.Add(effectiveMode);
            process.StartInfo.ArgumentList.Add("--to");
            process.StartInfo.ArgumentList.Add(effectiveTo);

            process.Start();

            string errorOutput = await process.StandardError.ReadToEndAsync();
            string standardOutput = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Kuroshiro 轉換失敗 (exit={process.ExitCode}): {errorOutput}".Trim());
            }

            string[] outputLines = standardOutput
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            string lastLine = outputLines.LastOrDefault() ?? "[]";
            var results = JsonSerializer.Deserialize<List<string>>(lastLine) ?? new List<string>();

            if (effectiveTo.Equals("romaji", StringComparison.OrdinalIgnoreCase))
            {
                var sourceLines = texts.ToList();
                results = results
                    .Select((result, index) => JapaneseRomanSanitizer.NormalizeWithContext(sourceLines[index], result, _rubyGenerator))
                    .ToList();
            }

            return results;
        }

        private async Task<List<(int LyricID, string Japanese)>> LoadLyricsAsync(string songUid)
        {
            string tableName = $"Songs_{songUid}";
            var lyricsList = new List<(int LyricID, string Japanese)>();

            using var connection = new SqlConnection(_configuration.GetConnectionString("DefaultConnection"));
            await connection.OpenAsync();

            string selectQuery = $"SELECT LyricID, Japanese FROM [{tableName}] WHERE Japanese IS NOT NULL ORDER BY LyricID";
            using var selectCommand = new SqlCommand(selectQuery, connection);
            using var reader = await selectCommand.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                lyricsList.Add((reader.GetInt32(0), reader.GetString(1)));
            }

            return lyricsList;
        }

        private async Task UpdateLyricsColumnAsync(
            string songUid,
            string columnName,
            IReadOnlyCollection<LyricUpdate> updateList)
        {
            string tableName = $"Songs_{songUid}";

            using var connection = new SqlConnection(_configuration.GetConnectionString("DefaultConnection"));
            await connection.OpenAsync();
            await EnsureColumnExists(connection, tableName, columnName);

            using var transaction = connection.BeginTransaction();
            using var updateCommand = new SqlCommand("", connection, transaction);

            var updateQuery = new StringBuilder($"UPDATE [{tableName}] SET [{columnName}] = CASE LyricID ");

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
            await transaction.CommitAsync();
        }

        private async Task EnsureColumnExists(SqlConnection connection, string tableName, string columnName)
        {
            const string checkColumnQuery = @"
                SELECT COUNT(*)
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_NAME = @TableName AND COLUMN_NAME = @ColumnName";

            using var checkCommand = new SqlCommand(checkColumnQuery, connection);
            checkCommand.Parameters.AddWithValue("@TableName", tableName);
            checkCommand.Parameters.AddWithValue("@ColumnName", columnName);

            int columnCount = System.Convert.ToInt32(await checkCommand.ExecuteScalarAsync() ?? 0);

            if (columnCount == 0)
            {
                string alterTableQuery = $"ALTER TABLE [{tableName}] ADD [{columnName}] NVARCHAR(MAX)";
                using var alterCommand = new SqlCommand(alterTableQuery, connection);
                await alterCommand.ExecuteNonQueryAsync();
            }
        }

        private static bool IsValidSongUid(string? songUid)
            => !string.IsNullOrWhiteSpace(songUid) && Regex.IsMatch(songUid, "^[A-Za-z0-9_-]+$");

        private static bool IsWritableLyricsColumn(string? columnName)
            => string.Equals(columnName, "Roman", StringComparison.Ordinal)
               || string.Equals(columnName, "JapaneseRuby", StringComparison.Ordinal);

        public class TextInput
        {
            public string Text { get; set; } = string.Empty;
            public string Mode { get; set; } = "furigana";
            public string To { get; set; } = "hiragana";
        }

        public class BatchTextInput
        {
            public List<string> Texts { get; set; } = new();
            public string Mode { get; set; } = "furigana";
            public string To { get; set; } = "hiragana";
        }

        public class ConvertRequest
        {
            public string SongUid { get; set; } = string.Empty;
            public string Mode { get; set; } = "furigana";
            public string To { get; set; } = "hiragana";
            public string Column { get; set; } = string.Empty;
        }

        public class ConvertResponse
        {
            public string Result { get; set; } = string.Empty;
        }

        private sealed record LyricUpdate(int LyricID, string ConvertedText);
    }
}
