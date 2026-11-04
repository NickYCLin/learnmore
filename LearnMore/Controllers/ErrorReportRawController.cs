using LearnMore.Services;
using Microsoft.AspNetCore.Mvc;

namespace LearnMore.Controllers;

[Route("Management/ErrorReports/raw")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ErrorReportRawController(IErrorReportManagerAccess access, IErrorReportRawReader reader,
    ILogger<ErrorReportRawController> logger) : Controller
{
    [HttpGet("{reportId:int}")]
    public async Task<IActionResult> Index(int reportId, CancellationToken cancellationToken)
    {
        var denied = await access.AuthorizeAsync(this, cancellationToken);
        if (denied != null) return denied;
        if (ErrorReportRawTarget.Find(reportId) == null) return NotFound();
        try { return View(await reader.ReadAsync(reportId, cancellationToken)); }
        catch (RubyRepairConflictException) { return Conflict(new { message = "回報、歌曲、影片或歌詞列不符；未提供資料，未修改任何資料。" }); }
        catch (Exception exception) when (exception is System.Data.SqlClient.SqlException or KeyNotFoundException)
        {
            logger.LogWarning("Raw lyric read failed for report {ReportId}; no changes submitted.", reportId);
            return StatusCode(503, new { message = "無法核實原始結構或讀取資料；未修改任何資料。" });
        }
    }
}
