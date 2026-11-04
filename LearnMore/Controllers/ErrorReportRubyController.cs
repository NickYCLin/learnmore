using LearnMore.Services;
using Microsoft.AspNetCore.Mvc;

namespace LearnMore.Controllers;

[Route("Management/ErrorReports/ruby")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ErrorReportRubyController(IErrorReportManagerAccess access, IErrorReportRubyRepair repair,
    ILogger<ErrorReportRubyController> logger) : Controller
{
    [HttpGet("{reportId:int}")]
    public async Task<IActionResult> Index(int reportId, CancellationToken cancellationToken)
    {
        var denied = await access.AuthorizeAsync(this, cancellationToken);
        if (denied != null) return denied;
        if (RubyRepairTarget.Find(reportId) == null) return NotFound();
        try { return View(await repair.ReadAsync(reportId, cancellationToken)); }
        catch (RubyRepairConflictException) { return Conflict(new { message = "回報、影片或原值不符，或已修正；未提供變更，請重新核對。" }); }
        catch (Exception exception) when (exception is System.Data.SqlClient.SqlException or KeyNotFoundException)
        {
            logger.LogWarning("Ruby repair read failed for report {ReportId}; no changes submitted.", reportId);
            return StatusCode(503, new { message = "無法核實原始結構或讀取資料；未修改任何資料。" });
        }
    }

    [HttpPost("{reportId:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Apply(int reportId, [FromForm] string fingerprint, CancellationToken cancellationToken)
    {
        var denied = await access.AuthorizeAsync(this, cancellationToken);
        if (denied != null) return denied;
        if (RubyRepairTarget.Find(reportId) == null) return NotFound();
        if (!ModelState.IsValid || fingerprint == null || fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit)) return BadRequest();
        try
        {
            var receipt = await repair.ApplyAsync(reportId, fingerprint, HttpContext.Session.GetString("Email")!, cancellationToken);
            return Json(new { receipt.AuditId, receipt.CompletionRecorded, message = receipt.CompletionRecorded
                ? "只修正此句 JapaneseRuby；回報狀態未變更。請重新讀回核對。"
                : "資料已提交，但稽核完成標記未寫入。請以稽核 ID 核對，不要重送。" });
        }
        catch (RubyRepairConflictException) { return Conflict(new { message = "原值已變更或範圍不符；本次未提交，請重新讀取確認頁。" }); }
        catch (Exception exception) when (exception is System.Data.SqlClient.SqlException or IOException or UnauthorizedAccessException or KeyNotFoundException)
        {
            logger.LogWarning("Ruby repair failed for report {ReportId}; reconcile prepared audit and current row before any retry.", reportId);
            return StatusCode(503, new { message = "無法完成安全修正。請管理員核對 RubyRepairAudit 與目前資料，確認交易結果後再操作；不要直接重送。" });
        }
    }
}
