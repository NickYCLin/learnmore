using LearnMore.Services;
using Microsoft.AspNetCore.Mvc;

namespace LearnMore.Controllers.API;

[ApiController]
[Route("api/music-automation/review-queue")]
[ServiceFilter(typeof(MusicAutomationAuthorizeFilter))]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class TimingReviewQueueAutomationController(ITimingReviewQueueReader reader) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(int page = 1, int pageSize = 25, CancellationToken cancellationToken = default)
    {
        if (!TimingReviewQueueReader.IsValidPage(page, pageSize))
            return BadRequest(new { error = "invalid_pagination" });
        try
        {
            return new JsonResult(await reader.ReadAsync(page, pageSize, cancellationToken), MusicAlignmentValidation.Json);
        }
        catch (TimingReviewQueueSchemaException)
        {
            return StatusCode(503, new { error = "review_queue_schema_unverified" });
        }
    }
}
