using Microsoft.AspNetCore.Mvc;

namespace LearnMore.Controllers;

// Standalone public pages: no login, analytics scripts or unrelated website account flows.
public sealed class MobileController(IConfiguration configuration) : Controller
{
    public IActionResult Privacy() => Page("Privacy");
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
