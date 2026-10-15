using LearnMore.Repository;
using LearnMore.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace LearnMore.Controllers.API;

[ApiController]
[Route("api/mobile/v1")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[EnableRateLimiting("mobile")]
public sealed class MobileApiController(MobileLibraryService library, MobileSessionService sessions, IConfiguration configuration) : ControllerBase
{
    private MobileSessionService.User? CurrentUser => sessions.Read(Request.Headers.Authorization.ToString());
    private SongGroupRepository Groups => new(configuration.GetConnectionString("DefaultConnection") ?? "");
    public record TokenRequest(string Code, string Verifier);
    public record GroupRequest(string Name);
    public record MembershipRequest(bool Included);

    [HttpGet("status")]
    public IActionResult Status() => Ok(new { version = 1 });

    [HttpPost("session")]
    public IActionResult Exchange(TokenRequest request)
    {
        var result = sessions.Exchange(request.Code, request.Verifier);
        return result is null ? Unauthorized() : Ok(new { token = result.Value.Token, user = result.Value.User, expiresIn = 28800 });
    }

    [HttpDelete("session")]
    public IActionResult Logout() { sessions.Revoke(Request.Headers.Authorization.ToString()); return NoContent(); }

    [HttpGet("songs")]
    public async Task<IActionResult> Songs(string q = "", int page = 1, bool favorites = false, CancellationToken cancellationToken = default)
    {
        if (q.Length > 100 || page is < 1 or > 1000) return BadRequest();
        var user = CurrentUser;
        if (favorites && user is null) return Unauthorized();
        return Ok(await library.Search(q.Trim(), page, favorites ? user!.Id : null, cancellationToken));
    }

    [HttpGet("songs/{uid}")]
    public async Task<IActionResult> Song(string uid, CancellationToken cancellationToken)
    {
        if (!MobileLibraryService.ValidSongUid(uid)) return BadRequest();
        var result = await library.Get(uid, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpGet("groups")]
    public IActionResult GetGroups(string? songUid = null)
    {
        var user = CurrentUser;
        if (user is null) return Unauthorized();
        if (songUid is not null && !MobileLibraryService.ValidSongUid(songUid)) return BadRequest();
        var selected = songUid is null ? [] : Groups.GetUserGroupIdsForSong(user.Id.ToString(), songUid);
        return Ok(Groups.GetGroups(user.Id.ToString()).Select(g => new { id = g.GroupId, name = g.GroupName, included = selected.Contains(g.GroupId) }));
    }

    [HttpPost("groups")]
    public IActionResult CreateGroup(GroupRequest request)
    {
        var user = CurrentUser;
        if (user is null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 50) return BadRequest();
        return Ok(new { id = Groups.CreateGroup(user.Id.ToString(), request.Name.Trim()) });
    }

    [HttpPut("groups/{groupId:int}/songs/{uid}")]
    public async Task<IActionResult> SetMembership(int groupId, string uid, MembershipRequest request, CancellationToken cancellationToken)
    {
        var user = CurrentUser;
        if (user is null) return Unauthorized();
        if (groupId <= 0 || !MobileLibraryService.ValidSongUid(uid)) return BadRequest();
        return await library.SetGroupSong(user.Id, groupId, uid, request.Included, cancellationToken) ? NoContent() : NotFound();
    }
}
