using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Caching.Memory;

namespace LearnMore.Services;

public sealed record SearchTrafficKeyword(string Query, double Clicks, double Impressions);
public sealed record SearchTrafficReport(string State, DateOnly Start, DateOnly End,
    IReadOnlyList<SearchTrafficKeyword> Keywords);

public sealed class SearchConsoleTraffic(HttpClient client, IConfiguration configuration, IMemoryCache cache,
    TimeProvider clock, ILogger<SearchConsoleTraffic> logger)
{
    private static readonly TimeZoneInfo Pacific = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");

    public async Task<SearchTrafficReport> ReadAsync(int days, CancellationToken cancellationToken)
    {
        var end = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), Pacific).DateTime).AddDays(-3);
        var start = end.AddDays(1 - days);
        var property = configuration["SiteTraffic:SearchConsole:Property"]?.Trim();
        var credentialPath = configuration["SiteTraffic:SearchConsole:CredentialPath"]?.Trim();
        var pagePrefix = configuration["SiteTraffic:SearchConsole:PagePrefix"]?.Trim();
        if (string.IsNullOrEmpty(property) || string.IsNullOrEmpty(credentialPath))
            return new("not-configured", start, end, []);

        var cacheKey = (nameof(SearchConsoleTraffic), property, pagePrefix, credentialPath, start, end);
        if (cache.TryGetValue<SearchTrafficReport>(cacheKey, out var cached) && cached != null) return cached;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var credential = GoogleCredential.FromFile(credentialPath);
            if (credential.UnderlyingCredential is not ServiceAccountCredential serviceAccount)
                return new("unavailable", start, end, []);
            var scoped = GoogleCredential.FromServiceAccountCredential(serviceAccount.WithUseJwtAccessWithScopes(false))
                .CreateScoped("https://www.googleapis.com/auth/webmasters.readonly").UnderlyingCredential;
            var token = await scoped.GetAccessTokenForRequestAsync(cancellationToken: timeout.Token);
            var report = await QueryAsync(property, pagePrefix, start, end, token, timeout.Token);
            cache.Set(cacheKey, report, TimeSpan.FromMinutes(15));
            return report;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is
            (HttpRequestException or IOException or UnauthorizedAccessException or JsonException or
            Newtonsoft.Json.JsonException or Google.Apis.Auth.OAuth2.Responses.TokenResponseException or
            OperationCanceledException or ArgumentException or FormatException or InvalidOperationException or System.Security.Cryptography.CryptographicException))
        {
            logger.LogWarning("Search Console report could not be read ({ExceptionType}).", exception.GetType().Name);
            return new("unavailable", start, end, []);
        }
    }

    public async Task<SearchTrafficReport> QueryAsync(string property, string? pagePrefix, DateOnly start, DateOnly end,
        string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://www.googleapis.com/webmasters/v3/sites/{Uri.EscapeDataString(property)}/searchAnalytics/query");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new
        {
            startDate = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            endDate = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            dimensions = new[] { "query" },
            type = "web",
            dataState = "final",
            rowLimit = 20,
            dimensionFilterGroups = string.IsNullOrWhiteSpace(pagePrefix) ? Array.Empty<object>() : new object[]
            {
                new { filters = new[] { new { dimension = "page", @operator = "includingRegex", expression = "^" +
                    EscapePrefix(pagePrefix.TrimEnd('/')) + "(/|[?]|$)" } } }
            }
        });
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var data = await response.Content.ReadFromJsonAsync<SearchAnalyticsResponse>(cancellationToken)
            ?? throw new JsonException("Search Console response was empty.");
        var keywords = (data.Rows ?? []).Where(row => row.Keys?.Length == 1 && !string.IsNullOrWhiteSpace(row.Keys[0]))
            .Select(row => new SearchTrafficKeyword(row.Keys![0], row.Clicks, row.Impressions)).ToArray();
        return new("ready", start, end, keywords);
    }

    private static string EscapePrefix(string prefix) => string.Concat(prefix.Select(character =>
        @"\.^$|?*+()[]{}".Contains(character) ? "\\" + character : character.ToString()));

    private sealed record SearchAnalyticsResponse(SearchAnalyticsRow[]? Rows);
    private sealed record SearchAnalyticsRow(string[]? Keys, double Clicks, double Impressions);
}
