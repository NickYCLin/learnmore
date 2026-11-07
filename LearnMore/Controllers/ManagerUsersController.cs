using LearnMore.Services;
using Microsoft.AspNetCore.Mvc;

namespace LearnMore.Controllers;

[Route("Management/Users")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ManagerUsersController(IErrorReportManagerAccess access, ManagerUserStore store) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(string? search = null, string status = "active", int page = 1, CancellationToken ct = default)
    {
        var denied = await access.AuthorizeAsync(this, ct);
        if (denied != null) return denied;
        if (!ModelState.IsValid || page < 1 || search?.Length > 200 || status is not ("all" or "active" or "disabled" or "deleted" or "manager")) return BadRequest();
        return View(await store.ReadAsync(search?.Trim() ?? "", status, page, ct));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Detail(int id, CancellationToken ct)
    {
        var denied = await access.AuthorizeAsync(this, ct);
        if (denied != null) return denied;
        var detail = await store.DetailAsync(id, ct);
        return detail == null ? NotFound() : View(detail);
    }

    [HttpPost("{id:int}/change")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(16384)]
    public async Task<IActionResult> Change(int id, long revision, string action, string confirmation, string reason, CancellationToken ct)
    {
        var denied = await access.AuthorizeAsync(this, ct);
        if (denied != null) return denied;
        if (!ModelState.IsValid) return BadRequest("請完整填寫操作、確認 Email 與原因。");
        try { await store.ChangeAsync(HttpContext.Session.GetString("Email")!, id, revision, action, confirmation, reason, ct); }
        catch (UserManagementConflictException exception) { return StatusCode(409, exception.Message); }
        return RedirectToAction(nameof(Detail), new { id });
    }
}
