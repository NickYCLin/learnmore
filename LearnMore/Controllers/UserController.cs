using LearnMore.Models;
using LearnMore.Services;
using Microsoft.AspNetCore.Mvc;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;
using System.Data.SqlClient;

namespace LearnMore.Controllers
{
    public class UserController : Controller
    {
        #region 基本參數
        private readonly string _connectionString;
        private readonly IWebHostEnvironment _environment;
        private readonly PersistentLoginSessionService _persistentLoginSessionService;

        public UserController(IConfiguration configuration, IWebHostEnvironment environment, PersistentLoginSessionService persistentLoginSessionService)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection") ?? string.Empty;
            _environment = environment;
            _persistentLoginSessionService = persistentLoginSessionService;
        }
        #endregion

        #region 個人資料
        /// <summary>
        /// 個人資料
        /// </summary>
        /// <returns></returns>
        public IActionResult Profile()
        {
            string? email = HttpContext.Session.GetString("Email");
            if (string.IsNullOrEmpty(email))
            {
                return RedirectToAction("Index", "Home");
            }

            UserViewModel user = new UserViewModel();

            using (SqlConnection conn = new SqlConnection(_connectionString))
            {
                conn.Open();
                string query = "SELECT Id, NickName, Email, Avatar, Picture FROM Users WHERE Email = @Email";
                using (SqlCommand cmd = new SqlCommand(query, conn))
                {
                    cmd.Parameters.AddWithValue("@Email", email);
                    using (SqlDataReader reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            user.Id = reader.GetInt32(0);
                            user.NickName = reader.IsDBNull(1) ? "" : reader.GetString(1);
                            user.Email = reader.GetString(2);

                            string? uploadedAvatar = reader.IsDBNull(3) ? null : reader.GetString(3);
                            string? googlePicture = reader.IsDBNull(4) ? null : reader.GetString(4);
                            user.Avatar = UserAvatarUrlResolver.Resolve(uploadedAvatar, googlePicture, HttpContext.Request.PathBase);

                        }
                    }
                }
            }

            return View(user);
        }

        [HttpPost]
        public async Task<IActionResult> UpdateProfile(UserViewModel model, IFormFile? avatarFile)
        {
            if (!ModelState.IsValid)
            {
                return Json(new { success = false, message = "輸入資料不正確" });
            }

            string? currentEmail = HttpContext.Session.GetString("Email");
            if (string.IsNullOrWhiteSpace(currentEmail))
            {
                return Json(new { success = false, message = "請先登入" });
            }

            string? avatarPath = model.Avatar;
            string? newAvatarUrl = null;
            string sqlQuery = string.Empty;

            if (avatarFile != null && avatarFile.Length > 0)
            {
                sqlQuery = ", Avatar = @Avatar";
                string uploadsFolder = Path.Combine(_environment.WebRootPath, "uploads");
                if (!Directory.Exists(uploadsFolder))
                {
                    Directory.CreateDirectory(uploadsFolder);
                }

                string uniqueFileName = $"{Guid.NewGuid()}.png";
                string filePath = Path.Combine(uploadsFolder, uniqueFileName);

                try
                {
                    using (var image = await SixLabors.ImageSharp.Image.LoadAsync(avatarFile.OpenReadStream()))
                    {
                        image.Mutate(x => x.Resize(96, 96));
                        await image.SaveAsync(filePath, new PngEncoder());
                    }
                    avatarPath = "/uploads/" + uniqueFileName;
                    newAvatarUrl = UserAvatarUrlResolver.Resolve(avatarPath, null, HttpContext.Request.PathBase); // 回傳給前端與右上角頭像
                }
                catch (Exception)
                {
                    return Json(new { success = false, message = "上傳的圖片無效或已損壞！" });
                }
            }
            else
            {
                using (SqlConnection conn = new SqlConnection(_connectionString))
                {
                    conn.Open();
                    string query = "SELECT Avatar, Picture FROM Users WHERE Email = @Email";
                    using (SqlCommand cmd = new SqlCommand(query, conn))
                    {
                        cmd.Parameters.AddWithValue("@Email", currentEmail);
                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                string? uploadedAvatar = reader.IsDBNull(0) ? null : reader.GetString(0);
                                string? googlePicture = reader.IsDBNull(1) ? null : reader.GetString(1);
                                newAvatarUrl = UserAvatarUrlResolver.Resolve(uploadedAvatar, googlePicture, HttpContext.Request.PathBase);
                            }
                        }
                    }
                }
            }

            using (SqlConnection conn = new SqlConnection(_connectionString))
            {
                conn.Open();
                string query = string.Format("UPDATE Users SET NickName = @NickName {0}, IsFirstLogin = 0 WHERE Email = @Email", sqlQuery);

                using (SqlCommand cmd = new SqlCommand(query, conn))
                {
                    cmd.Parameters.AddWithValue("@NickName", model.NickName ?? (object)DBNull.Value);
                    if (avatarFile != null && avatarFile.Length > 0)
                    {
                        cmd.Parameters.AddWithValue("@Avatar", avatarPath ?? (object)DBNull.Value);
                    }
                    cmd.Parameters.AddWithValue("@Email", currentEmail);
                    cmd.ExecuteNonQuery();
                }
            }

            string displayPicture = newAvatarUrl ?? UserAvatarUrlResolver.Resolve(null, HttpContext.Session.GetString("Picture"), HttpContext.Request.PathBase);
            HttpContext.Session.SetString("Picture", displayPicture);
            await _persistentLoginSessionService.PersistAsync(
                HttpContext,
                currentEmail,
                HttpContext.Session.GetString("UserId") ?? string.Empty,
                model.NickName ?? HttpContext.Session.GetString("UserName") ?? model.Email,
                displayPicture);

            return Json(new { success = true, message = "更新成功！", newAvatar = displayPicture });
        }
        #endregion

        #region 設定
        /// <summary>
        /// 設定
        /// </summary>
        /// <returns></returns>
        public IActionResult Settings()
        {
            string? email = HttpContext.Session.GetString("Email");
            if (string.IsNullOrEmpty(email))
            {
                return RedirectToAction("Index", "Home");
            }

            int enableRoman = 1; // 預設啟用羅馬拼音

            using (SqlConnection conn = new SqlConnection(_connectionString))
            {
                conn.Open();
                string query = "SELECT EnableRoman FROM Users WHERE Email = @Email";
                using (SqlCommand cmd = new SqlCommand(query, conn))
                {
                    cmd.Parameters.AddWithValue("@Email", email);

                    object result = cmd.ExecuteScalar();
                    if (result != null)
                    {
                        enableRoman = Convert.ToInt32(result);
                    }
                }
            }

            ViewBag.EnableRoman = enableRoman;
            return View();
        }

        /// <summary>
        /// 更新 EnableRoman 狀態
        /// </summary>
        /// <param name="enableRoman">新的 EnableRoman 值</param>
        /// <returns></returns>
        [HttpPost]
        public IActionResult UpdateEnableRoman(int enableRoman)
        {
            string? email = HttpContext.Session.GetString("Email");
            if (string.IsNullOrEmpty(email))
            {
                return Unauthorized(new { success = false, message = "請先登入" });
            }

            using (SqlConnection conn = new SqlConnection(_connectionString))
            {
                conn.Open();
                string query = "UPDATE Users SET EnableRoman = @EnableRoman WHERE Email = @Email";
                using (SqlCommand cmd = new SqlCommand(query, conn))
                {
                    cmd.Parameters.AddWithValue("@EnableRoman", enableRoman);
                    cmd.Parameters.AddWithValue("@Email", email);
                    cmd.ExecuteNonQuery();
                }
            }

            return Json(new { success = true });
        }
        #endregion

        #region 我的收藏
        /// <summary>
        /// 我的收藏
        /// </summary>
        /// <returns></returns>
        public IActionResult Favorites()
        {
            return View();
        }
        #endregion

        #region 說明
        /// <summary>
        /// 說明
        /// </summary>
        /// <returns></returns>
        public IActionResult About()
        {
            return View();
        }
        #endregion

        #region 提供意見
        /// <summary>
        /// 提供意見
        /// </summary>
        /// <returns></returns>
        public IActionResult Feedback()
        {
            string? email = HttpContext.Session.GetString("Email");
            return View();
        }

        [HttpPost]
        public IActionResult Feedback(string Title, string Content)
        {
            string? email = HttpContext.Session.GetString("Email");
            if (string.IsNullOrEmpty(email))
            {
                return RedirectToAction("Login", "Account");
            }

            if (string.IsNullOrWhiteSpace(Title) || string.IsNullOrWhiteSpace(Content))
            {
                ViewData["Message"] = "標題和內容不得為空！";
                return View();
            }

            try
            {
                using (SqlConnection conn = new SqlConnection(_connectionString))
                {
                    conn.Open();
                    string query = "INSERT INTO Feedbacks (Title, FeedbackText, Email) VALUES (@Title, @Content, @Email)";

                    using (SqlCommand cmd = new SqlCommand(query, conn))
                    {
                        cmd.Parameters.AddWithValue("@Title", Title);
                        cmd.Parameters.AddWithValue("@Content", Content);
                        cmd.Parameters.AddWithValue("@Email", email);

                        cmd.ExecuteNonQuery();
                    }
                }

                ViewData["Message"] = "意見已提交，感謝您的回饋！";
            }
            catch (Exception ex)
            {
                ViewData["Message"] = "發生錯誤：" + ex.Message;
            }

            return View();
        }
        #endregion

        #region 歌詞錯誤或是Bug回報
        [HttpPost("ReportBug")]
        public IActionResult ReportBug([FromBody] BugReportModel bugReport)
        {
            string? userEmail = HttpContext.Session.GetString("Email");

            using (SqlConnection connection = new SqlConnection(_connectionString))
            {
                connection.Open();
                string query = @"
            INSERT INTO ErrorReports 
            (UserEmail, ErrorDescription, ErrorSongUid, ErrorLyricID, ReportDate, Status) 
            VALUES (@UserEmail, @ErrorDescription, @ErrorSongUid, @ErrorLyricID, @ReportDate, @Status)";

                using (SqlCommand command = new SqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@UserEmail", userEmail ?? (object)DBNull.Value);
                    command.Parameters.AddWithValue("@ErrorDescription", bugReport.errorDescription);
                    command.Parameters.AddWithValue("@ErrorSongUid", bugReport.errorSongUid);
                    command.Parameters.AddWithValue("@ErrorLyricID", bugReport.errorLyricID);
                    command.Parameters.AddWithValue("@ReportDate", DateTime.UtcNow);
                    command.Parameters.AddWithValue("@Status", 0); // 未處理

                    command.ExecuteNonQuery();
                }
            }

            return Ok();
        }

        // 建立接收錯誤回報的Model
        public class BugReportModel
        {
            public string errorDescription { get; set; } = string.Empty;
            public string errorSongUid { get; set; } = string.Empty;
            public int errorLyricID { get; set; }
        }
        #endregion

        #region 贊助頁面
        public IActionResult CreateOrder()
        {
            IConfiguration Config = new ConfigurationBuilder().AddJsonFile("appSettings.json").Build();
            // 產生測試資訊
            ViewData["CustomerURL"] = $"{Request.Scheme}://{Request.Host}{Request.Path}/LearnMore/Payment/CallbackCustomer"; //商店取號網址
            ViewData["ClientBackURL"] = $"{Request.Scheme}://{Request.Host}{Request.Path}/LearnMore"; //返回商店網址 
            return View();
        }
        #endregion
    }
}
