using LearnMore.Services;
using Microsoft.AspNetCore.Mvc;

namespace LearnMore.Controllers;

public interface ITimingReviewQueueManagerAccess
{
    Task<IActionResult?> AuthorizeAsync(ControllerBase controller, CancellationToken cancellationToken);
}

public sealed class TimingReviewQueueManagerAccess(IConfiguration configuration) : ITimingReviewQueueManagerAccess
{
    public async Task<IActionResult?> AuthorizeAsync(ControllerBase controller, CancellationToken cancellationToken)
    {
        var result = await ControllerAccessGuard.RequireManagerAsync(controller, configuration, cancellationToken);
        return result is ForbidResult ? controller.StatusCode(StatusCodes.Status403Forbidden) : result;
    }
}

[Route("Management/ReviewQueue")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class TimingReviewQueueController(ITimingReviewQueueManagerAccess access,
    ITimingReviewQueueReader reader, IManagementWorkStore? workStore = null) : Controller
{
    [HttpGet("")]
    public Task<IActionResult> Index(int page = 1, int pageSize = 25, CancellationToken cancellationToken = default)
        => ReadAsync(false, page, pageSize, cancellationToken);

    [HttpGet("data")]
    public Task<IActionResult> Data(int page = 1, int pageSize = 25, CancellationToken cancellationToken = default)
        => ReadAsync(true, page, pageSize, cancellationToken);

    private async Task<IActionResult> ReadAsync(bool json, int page, int pageSize, CancellationToken cancellationToken)
    {
        var denied = await access.AuthorizeAsync(this, cancellationToken);
        if (denied != null) return denied;
        if (!ModelState.IsValid || !TimingReviewQueueReader.IsValidPage(page, pageSize))
            return BadRequest(new { message = "page 必須至少為 1，pageSize 必須介於 1 到 100。" });
        try
        {
            var result = await reader.ReadAsync(page, pageSize, cancellationToken);
            if (!json && workStore != null)
                ViewData["WorkStates"] = await workStore.ReadLatestAsync("review", result.Items.Select(item => item.SongUid).ToArray(), cancellationToken);
            return json ? Json(result) : View("Index", result);
        }
        catch (TimingReviewQueueSchemaException)
        {
            return StatusCode(503, new { message = "歌曲或校正工作欄位不完整，無法核實全站清單；不是零首。" });
        }
        catch (ManagementWorkUnavailableException)
        {
            return StatusCode(503, "處理紀錄尚未就緒，請先完成管理處理紀錄資料表部署；不是尚未處理。");
        }
        catch (System.Data.SqlClient.SqlException)
        {
            return StatusCode(503, "清單或處理紀錄暫時無法讀取，請稍後重試；不是零首或尚未處理。");
        }
    }
}
