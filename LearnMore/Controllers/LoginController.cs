using Microsoft.AspNetCore.Mvc;
using System.Data.SqlClient;
using Google.Apis.Auth;
using LearnMore.Services;

namespace LearnMore.Controllers
{
    public class LoginController : Controller
    {
        #region 基本參數
        private readonly string _connectionString;
        private readonly IConfiguration _configuration;
        private readonly PersistentLoginSessionService _persistentLoginSessionService;

        public LoginController(IConfiguration configuration, PersistentLoginSessionService persistentLoginSessionService)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection") ?? string.Empty;
            _configuration = configuration;
            _persistentLoginSessionService = persistentLoginSessionService;
        }
        #endregion

        #region 測試帳號登入（僅供開發測試用）
        [HttpPost("Login/TestLogin")]
        public async Task<IActionResult> TestLogin([FromBody] TestLoginRequest request)
        {
            var testEmail = _configuration["TestAccount:Email"];
            var testPassword = _configuration["TestAccount:Password"];

            if (string.IsNullOrEmpty(testEmail) || string.IsNullOrEmpty(testPassword))
                return NotFound("測試帳號未設定");

            if (request.Email != testEmail || request.Password != testPassword)
                return Unauthorized("帳號或密碼錯誤");

            var userProfile = await LoadPreferredUserProfileAsync(testEmail, null);
            await _persistentLoginSessionService.PersistAsync(
                HttpContext,
                testEmail,
                userProfile.UserId,
                "Test User",
                userProfile.DisplayPicture);

            return Ok(new { success = true, email = testEmail });
        }

        public class TestLoginRequest
        {
            public string Email { get; set; } = string.Empty;
            public string Password { get; set; } = string.Empty;
        }
        #endregion

        #region 登入畫面
        public IActionResult Index()
        {
            return View();
        }
        #endregion

        #region 驗證 Google 登入授權
        /// <summary>
        /// 驗證 Google 登入授權
        /// </summary>
        /// <returns></returns>
        public async Task<IActionResult> ValidGoogleLogin()
        {
            string? formCredential = Request.Form["credential"]; //回傳憑證
            string? formToken = Request.Form["g_csrf_token"]; //回傳令牌
            string? cookiesToken = Request.Cookies["g_csrf_token"]; //Cookie 令牌

            // 驗證 Google Token
            GoogleJsonWebSignature.Payload? payload = VerifyGoogleToken(formCredential, formToken, cookiesToken).Result;
            if (payload == null)
            {
                // 驗證失敗
                return RedirectToAction("Index", "Login");
            }

            string? userId = null;
            string? uploadedAvatar = null;
            string? googlePicture = payload.Picture;
            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();

                var query = @"
IF EXISTS (SELECT 1 FROM Users WHERE Email = @Email)
BEGIN
    UPDATE [dbo].[Users]
    SET [Name] = @Name,
        [Picture] = @Picture
    WHERE [Email] = @Email
END
ELSE
BEGIN
    INSERT INTO [dbo].[Users]
           ([Name]
           ,[Email]
           ,[Picture]
           ,[EnableRoman])
     VALUES
           (@Name
           ,@Email
           ,@Picture
           ,1)
END";

                using (var command = new SqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@Name", payload.Name ?? (object)DBNull.Value);
                    command.Parameters.AddWithValue("@Email", payload.Email);
                    command.Parameters.AddWithValue("@Picture", payload.Picture ?? (object)DBNull.Value);

                    await command.ExecuteNonQueryAsync();
                }

                var getIdQuery = @"SELECT Id, Avatar, Picture FROM Users WHERE Email = @Email";
                using (var getIdCommand = new SqlCommand(getIdQuery, connection))
                {
                    getIdCommand.Parameters.AddWithValue("@Email", payload.Email);
                    using var reader = await getIdCommand.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        userId = reader.IsDBNull(0) ? string.Empty : reader.GetInt32(0).ToString();
                        uploadedAvatar = reader.IsDBNull(1) ? null : reader.GetString(1);
                        googlePicture = reader.IsDBNull(2) ? payload.Picture : reader.GetString(2);
                    }
                }
            }

            string displayPicture = UserAvatarUrlResolver.Resolve(uploadedAvatar, googlePicture, Request.PathBase);
            await _persistentLoginSessionService.PersistAsync(
                HttpContext,
                payload.Email ?? "None",
                userId ?? string.Empty,
                payload.Name ?? "None",
                displayPicture);

            return RedirectToAction("Index", "Home");
        }

        private async Task<(string UserId, string DisplayPicture)> LoadPreferredUserProfileAsync(string email, string? googlePicture)
        {
            string userId = string.Empty;
            string? uploadedAvatar = null;
            string? storedGooglePicture = googlePicture;

            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            using var command = new SqlCommand("SELECT Id, Avatar, Picture FROM Users WHERE Email = @Email", connection);
            command.Parameters.AddWithValue("@Email", email);
            using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                userId = reader.IsDBNull(0) ? string.Empty : reader.GetInt32(0).ToString();
                uploadedAvatar = reader.IsDBNull(1) ? null : reader.GetString(1);
                storedGooglePicture = reader.IsDBNull(2) ? googlePicture : reader.GetString(2);
            }

            string displayPicture = UserAvatarUrlResolver.Resolve(uploadedAvatar, storedGooglePicture, Request.PathBase);
            return (string.IsNullOrWhiteSpace(userId) ? "test-user" : userId, displayPicture);
        }

        /// <summary>
        /// 驗證 Google Token
        /// </summary>
        /// <param name="formCredential"></param>
        /// <param name="formToken"></param>
        /// <param name="cookiesToken"></param>
        /// <returns></returns>
        public async Task<GoogleJsonWebSignature.Payload?> VerifyGoogleToken(string? formCredential, string? formToken, string? cookiesToken)
        {
            // 檢查空值
            if (formCredential == null || formToken == null && cookiesToken == null)
            {
                return null;
            }

            GoogleJsonWebSignature.Payload? payload;
            try
            {
                // 驗證 token
                if (formToken != cookiesToken)
                {
                    return null;
                }

                // 驗證憑證
                IConfiguration Config = new ConfigurationBuilder().AddJsonFile("appSettings.json").Build();
                string GoogleApiClientId = Config.GetSection("GoogleApiClientId").Value ?? string.Empty;
                var settings = new GoogleJsonWebSignature.ValidationSettings()
                {
                    Audience = new List<string>() { GoogleApiClientId }
                };
                payload = await GoogleJsonWebSignature.ValidateAsync(formCredential, settings);
                if (!payload.Issuer.Equals("accounts.google.com") && !payload.Issuer.Equals("https://accounts.google.com"))
                {
                    return null;
                }
                if (payload.ExpirationTimeSeconds == null)
                {
                    return null;
                }
                else
                {
                    DateTime now = DateTime.Now.ToUniversalTime();
                    DateTime expiration = DateTimeOffset.FromUnixTimeSeconds((long)payload.ExpirationTimeSeconds).DateTime;
                    if (now > expiration)
                    {
                        return null;
                    }
                }
            }
            catch
            {
                return null;
            }
            return payload;
        }
        #endregion

        #region 使用者登出
        public IActionResult Logout()
        {
            HttpContext.Session.Clear();
            _persistentLoginSessionService.ClearPersistentCookie(HttpContext);
            return RedirectToAction("Index", "Login");
        }
        #endregion

    }
}
