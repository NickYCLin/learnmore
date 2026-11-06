using System.ComponentModel.DataAnnotations;
using System.Data.SqlClient;
using LearnMore.Services;
using Microsoft.AspNetCore.Mvc;

namespace LearnMore.Controllers;

public sealed class ManagementWorkUpdate
{
    [Range(0, long.MaxValue)]
    public long ExpectedRevision { get; set; }
    [Required, RegularExpression("^[A-Fa-f0-9]{64}$")]
    public string EvidenceHash { get; set; } = "";
    [Required, RegularExpression("^(pending|in_progress|resolved)$")]
    public string Status { get; set; } = "";
    [Required, StringLength(2000)]
    public string Note { get; set; } = "";
}

[Route("Management/Work")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ManagementWorkController(IErrorReportManagerAccess access, IManagementWorkStore store,
    ILogger<ManagementWorkController> logger) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(string kind, string itemId, CancellationToken cancellationToken)
    {
        var denied = await access.AuthorizeAsync(this, cancellationToken);
        if (denied != null) return denied;
        if (!ManagementWork.ValidTarget(kind, itemId)) return BadRequest();
        try
        {
            var detail = await store.ReadAsync(kind, itemId, cancellationToken);
            return detail == null ? NotFound() : View("Index", detail);
        }
        catch (Exception exception) when (Unavailable(exception))
        {
            logger.LogError(exception, "Management work history could not be read.");
            return StatusCode(503, "處理紀錄暫時無法讀取，請稍後重試；不是尚未處理。");
        }
    }

    [HttpPost(""), ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(string kind, string itemId, ManagementWorkUpdate update,
        CancellationToken cancellationToken)
    {
        var denied = await access.AuthorizeAsync(this, cancellationToken);
        if (denied != null) return denied;
        var actor = HttpContext.Session.GetString("Email");
        if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();
        if (!ManagementWork.ValidTarget(kind, itemId) || !ModelState.IsValid
            || !ManagementWork.ValidStatus(update.Status) || string.IsNullOrWhiteSpace(update.Note))
            return BadRequest("請選擇有效狀態並填寫 1 到 2000 字的處理備註。");
        try
        {
            await store.SaveAsync(kind, itemId, update.ExpectedRevision, update.EvidenceHash,
                update.Status, update.Note, actor, cancellationToken);
            return RedirectToAction(nameof(Index), new { kind, itemId });
        }
        catch (ManagementWorkConflictException)
        {
            Response.StatusCode = StatusCodes.Status409Conflict;
            ViewData["Conflict"] = "尚未儲存：原始資料或處理紀錄已更新。請核對最新內容，再重新提交；不會覆蓋他人的紀錄。";
            ViewData["AttemptedNote"] = update.Note;
            return await Index(kind, itemId, cancellationToken);
        }
        catch (Exception exception) when (Unavailable(exception))
        {
            logger.LogError(exception, "Management work update could not be confirmed.");
            return StatusCode(503, "無法確認處理紀錄是否已儲存。請先重新讀取紀錄，勿直接重複提交。");
        }
    }

    private static bool Unavailable(Exception exception)
        => exception is ManagementWorkUnavailableException or ErrorReportSchemaException
            or TimingReviewQueueSchemaException or SqlException;
}
