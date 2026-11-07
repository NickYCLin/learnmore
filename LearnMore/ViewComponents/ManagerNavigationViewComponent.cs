using LearnMore.Services;
using Microsoft.AspNetCore.Mvc;

namespace LearnMore.ViewComponents;

public sealed class ManagerNavigationViewComponent(ManagerUserStore store) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(bool mobile = false)
    {
        if (!await store.IsManagerAsync(HttpContext.Session.GetString("Email"), HttpContext.RequestAborted))
            return Content(string.Empty);
        return View(mobile);
    }
}
