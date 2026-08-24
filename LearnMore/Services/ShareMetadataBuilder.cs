namespace LearnMore.Services;

public sealed record ShareMetadata(
    string HtmlLanguage,
    string PageTitle,
    string OpenGraphTitle,
    string Description,
    string CanonicalUrl,
    string OpenGraphImageUrl,
    string TwitterCard,
    string AppleTouchIconUrl,
    string AppleMobileWebAppTitle,
    string AppleMobileWebAppCapable,
    string ThemeColor);

public static class ShareMetadataBuilder
{
    private const string SiteTitle = "ビビ學日語";
    private const string PageTitleSuffix = "｜日文歌詞同步學習平台";
    private const string OpenGraphTitleSuffix = "｜日文歌詞同步學習";
    private const string DefaultDescription = "免費日語歌詞學習平台！提供 YouTube 同步播放、假名標註（振り仮名）、羅馬拼音、繁體中文翻譯。用唱歌學日文，邊聽邊學，輕鬆記住日文歌詞，適合日語初學者到進階學習者。";
    private const string DefaultHtmlLanguage = "zh-Hant";
    private const string DefaultTwitterCard = "summary_large_image";
    private const string DefaultShareImagePath = "/LearnMore/proxy/catframe/1";
    private const string DefaultAppleTouchIconPath = "/apple-touch-icon.png?v=20260421";
    private const string DefaultAppleMobileWebAppCapable = "yes";
    private const string DefaultThemeColor = "#6366f1";

    public static ShareMetadata Build(
        string siteRoot,
        string currentPathAndQuery,
        string? pageTitle,
        string? description,
        string? shareImageUrl,
        string? searchTitle = null,
        string? openGraphTitle = null)
    {
        var siteContext = ParseSiteContext(siteRoot);
        var normalizedPathAndQuery = NormalizePathAndQuery(currentPathAndQuery, siteContext.PathBase);
        var normalizedTitle = string.IsNullOrWhiteSpace(pageTitle) ? "首頁" : pageTitle.Trim();
        var normalizedSearchTitle = string.IsNullOrWhiteSpace(searchTitle) ? null : searchTitle.Trim();
        var normalizedOpenGraphTitle = string.IsNullOrWhiteSpace(openGraphTitle) ? null : openGraphTitle.Trim();
        var normalizedDescription = string.IsNullOrWhiteSpace(description) ? DefaultDescription : description.Trim();
        var canonicalUrl = siteContext.Origin + normalizedPathAndQuery;
        var normalizedShareImageUrl = NormalizeAssetUrl(siteContext, string.IsNullOrWhiteSpace(shareImageUrl) ? DefaultShareImagePath : shareImageUrl.Trim());
        var appleTouchIconUrl = NormalizeAssetUrl(siteContext, DefaultAppleTouchIconPath);

        return new ShareMetadata(
            HtmlLanguage: DefaultHtmlLanguage,
            PageTitle: normalizedSearchTitle ?? $"{normalizedTitle} - {SiteTitle}{PageTitleSuffix}",
            OpenGraphTitle: normalizedOpenGraphTitle ?? $"{normalizedTitle} - {SiteTitle}{OpenGraphTitleSuffix}",
            Description: normalizedDescription,
            CanonicalUrl: canonicalUrl,
            OpenGraphImageUrl: normalizedShareImageUrl,
            TwitterCard: DefaultTwitterCard,
            AppleTouchIconUrl: appleTouchIconUrl,
            AppleMobileWebAppTitle: SiteTitle,
            AppleMobileWebAppCapable: DefaultAppleMobileWebAppCapable,
            ThemeColor: DefaultThemeColor);
    }

    private static SiteContext ParseSiteContext(string siteRoot)
    {
        if (string.IsNullOrWhiteSpace(siteRoot))
        {
            throw new ArgumentException("siteRoot is required.", nameof(siteRoot));
        }

        if (!Uri.TryCreate(siteRoot.Trim(), UriKind.Absolute, out var siteUri)
            || string.IsNullOrWhiteSpace(siteUri.Scheme)
            || string.IsNullOrWhiteSpace(siteUri.Host))
        {
            throw new ArgumentException("siteRoot must be an absolute URL.", nameof(siteRoot));
        }

        var pathBase = siteUri.AbsolutePath == "/"
            ? string.Empty
            : siteUri.AbsolutePath.TrimEnd('/');

        return new SiteContext(
            Origin: $"{siteUri.Scheme}://{siteUri.Authority}",
            PathBase: pathBase);
    }

    private static string NormalizePathAndQuery(string currentPathAndQuery, string pathBase)
    {
        var trimmed = string.IsNullOrWhiteSpace(currentPathAndQuery)
            ? "/"
            : currentPathAndQuery.Trim();

        if (!trimmed.StartsWith('/'))
        {
            trimmed = "/" + trimmed;
        }

        if (string.IsNullOrEmpty(pathBase) || trimmed == pathBase || trimmed.StartsWith(pathBase + "/", StringComparison.Ordinal))
        {
            return trimmed;
        }

        if (trimmed == "/")
        {
            return pathBase + "/";
        }

        return pathBase + trimmed;
    }

    private static string NormalizeAssetUrl(SiteContext siteContext, string assetUrl)
    {
        if (Uri.TryCreate(assetUrl, UriKind.Absolute, out var absoluteUri)
            && !string.IsNullOrWhiteSpace(absoluteUri.Scheme)
            && !string.IsNullOrWhiteSpace(absoluteUri.Host))
        {
            return assetUrl;
        }

        var pathPart = assetUrl;
        var queryPart = string.Empty;
        var queryIndex = assetUrl.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex >= 0)
        {
            pathPart = assetUrl[..queryIndex];
            queryPart = assetUrl[queryIndex..];
        }

        if (!pathPart.StartsWith('/'))
        {
            pathPart = "/" + pathPart;
        }

        if (!string.IsNullOrEmpty(siteContext.PathBase)
            && pathPart != siteContext.PathBase
            && !pathPart.StartsWith(siteContext.PathBase + "/", StringComparison.Ordinal))
        {
            pathPart = siteContext.PathBase + pathPart;
        }

        return siteContext.Origin + pathPart + queryPart;
    }

    private sealed record SiteContext(string Origin, string PathBase);
}
