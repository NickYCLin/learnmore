using LearnMore.Services;
using Microsoft.AspNetCore.Mvc;

namespace LearnMore.Controllers;

[Route("Management/ErrorReports/split")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class Report50RepairController(IErrorReportManagerAccess access, IReport50Repair repair,
    ILogger<Report50RepairController> logger) : Controller
{
    [HttpGet("{reportId:int}")]
    public async Task<IActionResult> Index(int reportId, CancellationToken cancellationToken)
    {
        var denied = await access.AuthorizeAsync(this, cancellationToken);
        if (denied != null) return denied;
        if (reportId != 50) return NotFound();
        try { return View(await repair.ReadAsync(reportId, cancellationToken)); }
        catch (RubyRepairConflictException) { return Conflict(new { message = "回報、歌曲、影片或歌詞列不符；未提供候選，未修改任何資料。" }); }
        catch (Exception exception) when (exception is System.Data.SqlClient.SqlException or KeyNotFoundException)
        {
            logger.LogWarning("Report50 preview unavailable; no changes submitted.");
            return StatusCode(503, new { message = "無法核實原始結構或資料，未修改任何資料。" });
        }
    }

    [HttpPost("{reportId:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Apply(int reportId, [FromForm] string fingerprint, CancellationToken cancellationToken)
    {
        var denied = await access.AuthorizeAsync(this, cancellationToken);
        if (denied != null) return denied;
        if (reportId != 50) return NotFound();
        if (!ModelState.IsValid || fingerprint == null || fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit)) return BadRequest();
        try
        {
            var receipt = await repair.ApplyAsync(reportId, fingerprint, HttpContext.Session.GetString("Email")!, cancellationToken);
            return Json(new { receipt.AuditId, receipt.CompletionRecorded, message = receipt.CompletionRecorded
                ? "兩列已一致提交，中文、秒數與回報狀態未變更。請重新讀回核對。"
                : "資料已提交但稽核完成標記不足；請以稽核ID核對，不要重送。" });
        }
        catch (RubyRepairConflictException) { return Conflict(new { message = "兩列原值、指紋或範圍不符，或已套用；本次未提交，請重新讀取。" }); }
        catch (Exception exception) when (exception is System.Data.SqlClient.SqlException or IOException or UnauthorizedAccessException or KeyNotFoundException)
        {
            logger.LogWarning("Report50 repair failed; reconcile audit and current rows before retry.");
            return StatusCode(503, new { message = "無法確認安全提交；請核對Report50RepairAudit與目前兩列，勿直接重送。" });
        }
    }
}
