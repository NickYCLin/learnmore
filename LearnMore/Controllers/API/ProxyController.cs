using Microsoft.AspNetCore.Mvc;
using System.Data.SqlClient;

namespace LearnMore.Controllers.API
{
    public class ProxyController : Controller
    {
        #region 基本參數
        private readonly string _connectionString;
        private readonly IWebHostEnvironment _environment;

        public ProxyController(IConfiguration configuration, IWebHostEnvironment environment)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection") ?? string.Empty;
            _environment = environment;
        }
        #endregion

        #region 貓咪動圖proxy
        [HttpGet("proxy/catframe/{id}")]
        public async Task<IActionResult> Catframe(string id)
        {
            string? relativePath = null;

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();

                var query = @"
            SELECT PARAM_VALUE
            FROM [language].[dbo].[PARAM_MAP]
            WHERE [PARENT_SN] = 2 AND [ID] = @ID
            ORDER BY [ID] ASC";

                using (var command = new SqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@ID", id);

                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        if (await reader.ReadAsync())
                        {
                            relativePath = reader["PARAM_VALUE"]?.ToString();
                        }
                    }
                }
            }

            if (string.IsNullOrEmpty(relativePath))
            {
                return NotFound("找不到影像資料");
            }

            // 轉換為實體檔案路徑
            var webRootPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
            var filePath = Path.Combine(webRootPath, relativePath.TrimStart('~', '/').Replace('/', Path.DirectorySeparatorChar));

            if (!System.IO.File.Exists(filePath))
            {
                return NotFound("找不到圖片檔案");
            }

            var contentType = "image/png"; // 或用 MimeMapping 判斷
            var imageBytes = await System.IO.File.ReadAllBytesAsync(filePath);
            return File(imageBytes, contentType);
        }
        #endregion
    }
}
