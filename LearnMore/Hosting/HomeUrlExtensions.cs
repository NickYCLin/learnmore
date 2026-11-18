using Microsoft.AspNetCore.Mvc;

namespace LearnMore.Hosting;

/// <summary>
/// 產生首頁（Home/Index）網址時一律帶結尾斜線：/LearnMore/?type=weekly。
/// 2026-11-18 搬家時 Caddy 曾短暫把 /LearnMore?... 以 308 永久轉到 /LearnMore/ 並丟掉查詢字串，
/// 瀏覽器會快取 308，所以站內連結不能再用沒有斜線的 /LearnMore?...。
/// </summary>
public static class HomeUrlExtensions
{
    public static string HomeIndex(this IUrlHelper url, object? values = null)
    {
        var root = url.Content("~/");
        if (!root.EndsWith('/')) root += "/";
        var generated = url.Action("Index", "Home", values);
        if (string.IsNullOrEmpty(generated)) return root;
        var queryStart = generated.IndexOf('?');
        return queryStart >= 0 ? root + generated[queryStart..] : root;
    }
}
