using LearnMore.Services;
using Microsoft.AspNetCore.Mvc;

namespace LearnMore.Controllers;

public interface IErrorReportManagerAccess
{
    Task<IActionResult?> AuthorizeAsync(ControllerBase controller, CancellationToken cancellationToken);
}

public sealed class ErrorReportManagerAccess(IConfiguration configuration) : IErrorReportManagerAccess
{
    public async Task<IActionResult?> AuthorizeAsync(ControllerBase controller, CancellationToken cancellationToken)
    {
        var result = await ControllerAccessGuard.RequireManagerAsync(controller, configuration, cancellationToken);
        return result is ForbidResult ? controller.StatusCode(StatusCodes.Status403Forbidden) : result;
    }
}

[Route("Management/ErrorReports")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ErrorReportsController(IErrorReportManagerAccess access, IErrorReportReader reader,
    ILogger<ErrorReportsController> logger, IManagementWorkStore? workStore = null) : Controller
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
        if (!ModelState.IsValid || !ErrorReportReader.IsValidPage(page, pageSize))
            return BadRequest(new { message = "page 必須至少為 1，pageSize 必須介於 1 到 100。" });
        try
        {
            var result = await reader.ReadAsync(page, pageSize, cancellationToken);
            if (!json && workStore != null)
                ViewData["WorkStates"] = await workStore.ReadLatestAsync("report", result.Items.Select(item => item.ReportId).ToArray(), cancellationToken);
            return json ? Json(result) : View("Index", result);
        }
        catch (ErrorReportSchemaException)
        {
            logger.LogWarning("ErrorReports schema lacks required columns or a non-null single-column unique index.");
            return StatusCode(503, new { message = "回報結構尚未核實：缺少已知欄位或單欄唯一鍵。請管理者提供 ErrorReports 結構證據；不是零筆回報。" });
        }
        catch (ManagementWorkUnavailableException)
        {
            return StatusCode(503, "處理紀錄尚未就緒，請先完成管理處理紀錄資料表部署；不是尚未處理。");
        }
        catch (System.Data.SqlClient.SqlException exception)
        {
            logger.LogError(exception, "Error reports or management work states could not be read.");
            return StatusCode(503, "回報或處理紀錄暫時無法讀取，請稍後重試；不是零筆或尚未處理。");
        }
    }
}
