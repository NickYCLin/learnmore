using LearnMore.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace LearnMore.Controllers;

[Route("Mobile")]
[EnableRateLimiting("mobile")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class MobileController(MobileSessionService sessions, IConfiguration configuration) : Controller
{
    public record ConnectRequest(string Challenge, string State);

    [HttpGet("Player")]
    public IActionResult Player(string videoId)
    {
        if (string.IsNullOrWhiteSpace(videoId) || !System.Text.RegularExpressions.Regex.IsMatch(videoId, "^[A-Za-z0-9_-]{11}$")) return BadRequest();
        // 只有無帳號資料的播放器頁允許 App 嵌入，其餘頁面保留 SAMEORIGIN。
        Response.Headers.Remove("X-Frame-Options");
        Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self' https://www.youtube.com https://s.ytimg.com; style-src 'self' 'unsafe-inline'; frame-src https://www.youtube.com; img-src 'self' https:; connect-src 'self' https://www.youtube.com; frame-ancestors capacitor://localhost http://localhost:5173 http://127.0.0.1:5173; object-src 'none'; base-uri 'self'";
        return View(model: videoId);
    }

    [HttpGet("Connect")]
    public IActionResult Connect(string challenge, string state)
    {
        if (!MobileSessionService.ValidChallenge(challenge) || !MobileSessionService.ValidState(state)) return BadRequest();
        if (!int.TryParse(HttpContext.Session.GetString("UserId"), out var userId) || userId <= 0)
        {
            HttpContext.Session.SetString("MobileLoginReturnUrl", Url.Action(nameof(Connect), new { challenge, state })!);
            return RedirectToAction("Index", "Login");
        }
        return View(new ConnectRequest(challenge, state));
    }

    [HttpPost("Connect")]
    [ValidateAntiForgeryToken]
    public IActionResult Approve(ConnectRequest request)
    {
        if (!MobileSessionService.ValidChallenge(request.Challenge) || !MobileSessionService.ValidState(request.State)) return BadRequest();
        if (!int.TryParse(HttpContext.Session.GetString("UserId"), out var userId) || userId <= 0) return Unauthorized();
        var user = new MobileSessionService.User(userId, HttpContext.Session.GetString("UserName") ?? "學習者");
        var code = sessions.IssueCode(user, request.Challenge);
        // 固定回呼避免任意 redirect；一次性 code 還需要 App 持有的 PKCE verifier。
        ViewBag.Callback = $"learnmore://auth?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(request.State)}";
        return View("Connected");
    }

    [HttpGet("Privacy")]
    public IActionResult Privacy() => Page("Privacy");
    [HttpGet("Support")]
    public IActionResult Support() => Page("Support");

    private IActionResult Page(string view)
    {
        var email = configuration["MobilePublication:SupportEmail"];
        var name = configuration["MobilePublication:OperatorName"];
        var logs = configuration.GetValue<int?>("MobilePublication:LogRetentionDays");
        var backups = configuration.GetValue<int?>("MobilePublication:BackupRetentionDays");
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(name) || logs is null or < 0 || backups is null or < 0)
            return StatusCode(503, "此頁面暫時無法使用，請稍後再試。");
        ViewBag.SupportEmail = email;
        ViewBag.OperatorName = name;
        ViewBag.LogRetentionDays = logs;
        ViewBag.BackupRetentionDays = backups;
        return View(view);
    }
}
