using LearnMore.Services.Mobile;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.RateLimiting;

namespace LearnMore.Controllers.API;

public sealed class MobileAuthorizeFilter(IMobileAccountStore accounts, IConfiguration config) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!config.GetValue<bool>("MobileAuth:Enabled"))
        { context.Result = new ObjectResult(new { error = "會員服務暫時無法使用。" }) { StatusCode = 503 }; return; }
        var token = BearerToken(context.HttpContext.Request);
        var user = token is null ? null : await accounts.AuthenticateAsync(token, context.HttpContext.RequestAborted);
        if (user is null)
        { context.Result = new UnauthorizedObjectResult(new { error = "登入已過期，請重新登入。" }); return; }
        context.HttpContext.Items[typeof(MobileUser)] = user;
        await next();
    }

    public static string? BearerToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var token = header[7..];
        return token.Length == 64 && token.All(Uri.IsHexDigit) ? token : null;
    }
}

[ApiController]
[Route("api/mobile/v1/auth")]
[EnableRateLimiting("mobile-auth")]
public sealed class MobileAuthController(IMobileAccountStore accounts, IMobileIdentityVerifier verifier) : ControllerBase
{
    [HttpGet("providers")]
    public IActionResult Providers() => Ok(new { google = verifier.GoogleEnabled, apple = verifier.AppleEnabled });

    [HttpPost]
    [RequestSizeLimit(16384)]
    public async Task<IActionResult> Login(MobileLoginRequest request, CancellationToken ct)
        => Ok(await accounts.SignInAsync(await verifier.VerifyAsync(request, ct), ct));
}

[ApiController]
[Route("api/mobile/v1/account")]
[ServiceFilter(typeof(MobileAuthorizeFilter))]
[EnableRateLimiting("mobile-auth")]
public sealed class MobileAccountController(IMobileAccountStore accounts, IMobileIdentityVerifier verifier) : ControllerBase
{
    private MobileUser CurrentUser => (MobileUser)HttpContext.Items[typeof(MobileUser)]!;

    [HttpGet]
    public IActionResult Get() => Ok(CurrentUser);

    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        await accounts.SignOutAsync(MobileAuthorizeFilter.BearerToken(Request)!, ct);
        return NoContent();
    }

    [HttpPost("link")]
    [RequestSizeLimit(16384)]
    public async Task<IActionResult> Link(MobileLoginRequest proof, CancellationToken ct)
    {
        await accounts.LinkAsync(CurrentUser.Id, await verifier.VerifyAsync(proof, ct), ct);
        return NoContent();
    }

    [HttpPost("delete")]
    [RequestSizeLimit(16384)]
    public async Task<IActionResult> Delete(MobileLoginRequest proof, CancellationToken ct)
    {
        await accounts.DeleteAsync(CurrentUser.Id, await verifier.VerifyAsync(proof, ct), ct);
        return NoContent();
    }
}
