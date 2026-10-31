using System.Security.Cryptography;
using System.Text;
using LearnMore.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace LearnMore.Controllers.API;

public sealed class MusicAutomationAuthorizeFilter(IConfiguration config) : IAsyncActionFilter
{
    public static bool Matches(string? token, string? expected) => !string.IsNullOrEmpty(token)
        && expected?.Length >= 32 && CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(token)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var read = config["MusicAutomation:ReadToken"];
        var write = config["MusicAutomation:WriteToken"];
        if (!config.GetValue<bool>("MusicAutomation:Enabled") || read?.Length < 32 || string.IsNullOrEmpty(read))
        { context.Result = new NotFoundResult(); return; }
        var header = context.HttpContext.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : null;
        var canWrite = write != read && Matches(token, write);
        var apply = context.ActionDescriptor.EndpointMetadata.OfType<MusicAutomationWriteAttribute>().Any();
        if (apply ? !canWrite : !(canWrite || Matches(token, read)))
        { context.Result = new UnauthorizedResult(); return; }
        await next();
    }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class MusicAutomationWriteAttribute : Attribute;

[ApiController]
[Route("api/music-automation")]
[ServiceFilter(typeof(MusicAutomationAuthorizeFilter))]
public sealed class MusicAutomationController(MusicAlignmentStore store, ILogger<MusicAutomationController> logger) : ControllerBase
{
    public sealed record StartRequest(string RequestId, string? SongUid = null, string? YoutubeUrl = null);
    public sealed record ApplyRequest(string PreviewHash);

    [HttpPost("jobs")]
    public Task<IActionResult> Start(StartRequest request, CancellationToken ct) => Safe(async () =>
    {
        var job = await store.StartAsync(request.RequestId, request.SongUid, request.YoutubeUrl, ct);
        return ResponseJson(Summary(job), 202);
    });

    private static object Summary(MusicAlignmentJob job) => new { job.JobId, job.SongUid, job.State,
        job.CreatedAt, job.UpdatedAt, job.FailureCode, job.AppliedAt,
        matchedCount = job.Report?.MatchedCount, totalCount = job.Report?.TotalCount,
        nextPollAfterSeconds = job.State is "queued" or "processing" ? 10 : 0 };

    [HttpGet("jobs/{id:guid}")]
    public Task<IActionResult> Get(Guid id, CancellationToken ct) => Safe(async () => ResponseJson(Summary(await store.GetAsync(id, ct))));

    [HttpGet("jobs/{id:guid}/report")]
    public Task<IActionResult> Report(Guid id, CancellationToken ct) => Safe(async () =>
    {
        var job = await store.GetAsync(id, ct);
        return ResponseJson(new { job.JobId, job.State, job.FailureCode, job.Report });
    });

    [HttpGet("jobs/{id:guid}/preview")]
    public Task<IActionResult> Preview(Guid id, CancellationToken ct) => Safe(async () =>
    {
        var job = await store.GetAsync(id, ct);
        if (job.Report is not { Accepted: true } report || job.State is not ("verified" or "applied"))
            throw new MusicAutomationException("verification_required");
        return ResponseJson(new { job.JobId, job.SongUid, job.State, report.PreviewHash,
            changes = report.Lines.Select(x => new { x.LyricId, x.Japanese, x.Before, x.After }),
            requiresUnchangedSong = true, preservesTextAndOwnership = true, job.AppliedAt });
    });

    [HttpPost("jobs/{id:guid}/apply")]
    [MusicAutomationWrite]
    public Task<IActionResult> Apply(Guid id, ApplyRequest request, CancellationToken ct) => Safe(async () =>
        ResponseJson(Summary(await store.ApplyAsync(id, request.PreviewHash, ct))));

    private static JsonResult ResponseJson(object value, int status = 200) =>
        new(value, MusicAlignmentValidation.Json) { StatusCode = status };

    private async Task<IActionResult> Safe(Func<Task<IActionResult>> action)
    {
        try { return await action(); }
        catch (MusicAutomationException ex) { return StatusCode(ex.Status, new { error = ex.Code }); }
        catch (OperationCanceledException) { return StatusCode(503, new { error = "request_interrupted_query_same_job" }); }
        catch (Exception ex)
        {
            logger.LogWarning("音樂工具請求失敗 type={Type}", ex.GetType().Name);
            return StatusCode(503, new { error = "service_unavailable_query_same_job" });
        }
    }
}
