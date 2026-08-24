using Microsoft.AspNetCore.Mvc;
using System.Data.SqlClient;
using System.Text;
using System.Xml.Linq;

namespace LearnMore.Controllers;

public class SeoController : Controller
{
    private const int SitemapSongLimit = 10000;
    private readonly string _connectionString;
    private readonly ILogger<SeoController> _logger;

    public SeoController(IConfiguration configuration, ILogger<SeoController> logger)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection") ?? string.Empty;
        _logger = logger;
    }

    [HttpGet("/sitemap.xml")]
    public async Task<IActionResult> Sitemap()
    {
        XNamespace sitemapNamespace = "http://www.sitemaps.org/schemas/sitemap/0.9";
        var siteRoot = BuildSiteRoot();
        var urls = new List<XElement>
        {
            BuildUrl(sitemapNamespace, siteRoot + "/", "daily", "1.0"),
            BuildUrl(sitemapNamespace, siteRoot + "/?type=weekly", "daily", "0.8"),
            BuildUrl(sitemapNamespace, siteRoot + "/?type=monthly", "daily", "0.8"),
            BuildUrl(sitemapNamespace, siteRoot + "/?type=new", "daily", "0.8")
        };

        foreach (var songUid in await LoadSongUidsAsync())
        {
            urls.Add(BuildUrl(sitemapNamespace, siteRoot + "/Lyrics/" + Uri.EscapeDataString(songUid), "weekly", "0.7"));
        }

        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(sitemapNamespace + "urlset", urls));

        return Content(document.ToString(SaveOptions.DisableFormatting), "application/xml; charset=utf-8", Encoding.UTF8);
    }

    private string BuildSiteRoot()
    {
        return $"{Request.Scheme}://{Request.Host}{Request.PathBase}".TrimEnd('/');
    }

    private async Task<IReadOnlyList<string>> LoadSongUidsAsync()
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            return Array.Empty<string>();
        }

        var songUids = new List<string>();

        const string sql = @"
SELECT TOP (@Limit) [SongUid]
FROM [Songs]
WHERE [SongUid] IS NOT NULL AND LTRIM(RTRIM([SongUid])) <> ''
ORDER BY [SongID] DESC";

        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(HttpContext.RequestAborted);

            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@Limit", SitemapSongLimit);

            await using var reader = await command.ExecuteReaderAsync(HttpContext.RequestAborted);
            while (await reader.ReadAsync(HttpContext.RequestAborted))
            {
                var songUid = reader.GetString(0);
                if (!string.IsNullOrWhiteSpace(songUid))
                {
                    songUids.Add(songUid.Trim());
                }
            }
        }
        catch (SqlException ex)
        {
            _logger.LogWarning(ex, "Failed to load song URLs for sitemap.");
        }

        return songUids;
    }

    private static XElement BuildUrl(XNamespace sitemapNamespace, string loc, string changeFrequency, string priority)
    {
        return new XElement(sitemapNamespace + "url",
            new XElement(sitemapNamespace + "loc", loc),
            new XElement(sitemapNamespace + "changefreq", changeFrequency),
            new XElement(sitemapNamespace + "priority", priority));
    }
}
