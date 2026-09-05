using Microsoft.AspNetCore.Mvc;
using System.Data.SqlClient;
using System.Security.Cryptography;
using System.Text;
using Google.Apis.Auth;
using LearnMore.Services;

namespace LearnMore.Controllers
{
    public class LoginController : Controller
    {
        private const string SmokeTokenHeaderName = "X-LearnMore-Smoke-Token";

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
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public async Task<IActionResult> TestLogin([FromBody] TestLoginRequest? request)
        {
            var configuredSmokeToken = _configuration["TestAccount:SmokeToken"];
            var providedSmokeToken = Request.Headers[SmokeTokenHeaderName].ToString();
            if (!IsValidSmokeToken(configuredSmokeToken, providedSmokeToken))
                return NotFound();

            var testEmail = _configuration["TestAccount:Email"];
            var testPassword = _configuration["TestAccount:Password"];

            if (string.IsNullOrEmpty(testEmail) || string.IsNullOrEmpty(testPassword))
                return NotFound("測試帳號未設定");

            if (request == null || request.Email != testEmail || request.Password != testPassword)
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

        private static bool IsValidSmokeToken(string? configuredToken, string? providedToken)
        {
            if (string.IsNullOrWhiteSpace(configuredToken) || string.IsNullOrWhiteSpace(providedToken))
                return false;

            var configuredHash = SHA256.HashData(Encoding.UTF8.GetBytes(configuredToken));
            var providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(providedToken));
            return CryptographicOperations.FixedTimeEquals(configuredHash, providedHash);
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
            GoogleJsonWebSignature.Payload? payload = await VerifyGoogleToken(formCredential, formToken, cookiesToken);
            if (payload == null)
            {
                // 驗證失敗
                return RedirectToAction("Index", "Login");
            }

            string? userId = null;
            string? uploadedAvatar = null;
            string? googlePicture = payload.Picture;
            var accountEmail = payload.Email;
            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();

                using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable);

                if (_configuration.GetValue<bool>("MobileAuth:Enabled"))
                {
                    using (var linked = new SqlCommand(@"
SELECT U.Email FROM dbo.MobileIdentities I
INNER JOIN dbo.Users U ON U.Id = I.UserId
WHERE I.Provider = 'google' AND I.Subject = @Subject", connection, transaction))
                    {
                        linked.Parameters.AddWithValue("@Subject", payload.Subject);
                        if (await linked.ExecuteScalarAsync() is string linkedEmail) accountEmail = linkedEmail;
                    }
                    // An Apple-created account must be explicitly linked before Google can access it.
                    // Never merge an Apple identity into a website account merely because emails match.
                    using var identityCheck = new SqlCommand(@"
SELECT COUNT(*) FROM dbo.Users U WITH (UPDLOCK, HOLDLOCK)
WHERE U.Email = @Email
AND EXISTS (SELECT 1 FROM dbo.MobileIdentities I WHERE I.UserId = U.Id)
AND NOT EXISTS (SELECT 1 FROM dbo.MobileIdentities I WHERE I.UserId = U.Id AND I.Provider = 'google' AND I.Subject = @Subject)
", connection, transaction);
                    identityCheck.Parameters.AddWithValue("@Email", accountEmail);
                    identityCheck.Parameters.AddWithValue("@Subject", payload.Subject);
                    if (Convert.ToInt32(await identityCheck.ExecuteScalarAsync()) > 0)
                        return StatusCode(409, "請先在 LearnMore App 登入原帳號，再於帳號頁連結此 Google 帳號。");
                }

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

                using (var command = new SqlCommand(query, connection, transaction))
                {
                    command.Parameters.AddWithValue("@Name", payload.Name ?? (object)DBNull.Value);
                    command.Parameters.AddWithValue("@Email", accountEmail);
                    command.Parameters.AddWithValue("@Picture", payload.Picture ?? (object)DBNull.Value);

                    await command.ExecuteNonQueryAsync();
                }

                var getIdQuery = @"SELECT Id, Avatar, Picture FROM Users WHERE Email = @Email";
                using (var getIdCommand = new SqlCommand(getIdQuery, connection, transaction))
                {
                    getIdCommand.Parameters.AddWithValue("@Email", accountEmail);
                    using var reader = await getIdCommand.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        userId = reader.IsDBNull(0) ? string.Empty : reader.GetInt32(0).ToString();
                        uploadedAvatar = reader.IsDBNull(1) ? null : reader.GetString(1);
                        googlePicture = reader.IsDBNull(2) ? payload.Picture : reader.GetString(2);
                    }
                }
                await transaction.CommitAsync();
            }

            string displayPicture = UserAvatarUrlResolver.Resolve(uploadedAvatar, googlePicture, Request.PathBase);
            await _persistentLoginSessionService.PersistAsync(
                HttpContext,
                accountEmail ?? "None",
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
            if (string.IsNullOrWhiteSpace(formCredential) || string.IsNullOrWhiteSpace(formToken) || string.IsNullOrWhiteSpace(cookiesToken))
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
                string GoogleApiClientId = _configuration["GoogleApiClientId"] ?? string.Empty;
                if (string.IsNullOrWhiteSpace(GoogleApiClientId)) return null;
                var settings = new GoogleJsonWebSignature.ValidationSettings()
                {
                    Audience = new List<string>() { GoogleApiClientId }
                };
                payload = await GoogleJsonWebSignature.ValidateAsync(formCredential, settings);
                if (!payload.EmailVerified) return null;
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
