using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Caching.Memory;

namespace LearnMore.Services;

public sealed record SearchTrafficMetrics(double Clicks, double Impressions, double Ctr, double Position);
public sealed record SearchTrafficRow(string Label, SearchTrafficMetrics Metrics);
public sealed record SearchTrafficReport(string State, DateOnly Start, DateOnly End,
    SearchTrafficMetrics? Totals = null, IReadOnlyList<SearchTrafficRow>? Keywords = null,
    IReadOnlyList<SearchTrafficRow>? Daily = null, IReadOnlyList<SearchTrafficRow>? Pages = null,
    IReadOnlyList<SearchTrafficRow>? Devices = null, IReadOnlyList<SearchTrafficRow>? Countries = null);

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
            return new("not-configured", start, end);

        var cacheKey = (nameof(SearchConsoleTraffic), property, pagePrefix, credentialPath, start, end);
        if (cache.TryGetValue<SearchTrafficReport>(cacheKey, out var cached) && cached != null) return cached;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var credential = GoogleCredential.FromFile(credentialPath);
            if (credential.UnderlyingCredential is not ServiceAccountCredential serviceAccount)
                return new("unavailable", start, end);
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
            return new("unavailable", start, end);
        }
    }

    public async Task<SearchTrafficReport> QueryAsync(string property, string? pagePrefix, DateOnly start, DateOnly end,
        string token, CancellationToken cancellationToken)
    {
        Task<SearchAnalyticsRow[]> Read(string? dimension, int limit) =>
            ReadRowsAsync(property, pagePrefix, start, end, token, dimension, limit, cancellationToken);

        var totals = Read(null, 1);
        var keywords = Read("query", 20);
        var daily = Read("date", end.DayNumber - start.DayNumber + 1);
        var pages = Read("page", 10);
        var devices = Read("device", 10);
        var countries = Read("country", 10);
        await Task.WhenAll(totals, keywords, daily, pages, devices, countries);

        return new("ready", start, end, (await totals).FirstOrDefault()?.Metrics,
            Breakdown(await keywords), Breakdown(await daily).OrderBy(row => row.Label, StringComparer.Ordinal).ToArray(),
            Breakdown(await pages), Breakdown(await devices), Breakdown(await countries, SearchTrafficCountryNames.Format));
    }

    private async Task<SearchAnalyticsRow[]> ReadRowsAsync(string property, string? pagePrefix, DateOnly start, DateOnly end,
        string token, string? dimension, int limit, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://www.googleapis.com/webmasters/v3/sites/{Uri.EscapeDataString(property)}/searchAnalytics/query");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new
        {
            startDate = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            endDate = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            dimensions = dimension == null ? Array.Empty<string>() : new[] { dimension },
            type = "web",
            dataState = "final",
            rowLimit = limit,
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
        return data.Rows ?? [];
    }

    private static SearchTrafficRow[] Breakdown(SearchAnalyticsRow[] rows, Func<string, string>? formatLabel = null) => rows
        .Where(row => row.Keys?.Length == 1 && !string.IsNullOrWhiteSpace(row.Keys[0]))
        .Select(row => new SearchTrafficRow(formatLabel?.Invoke(row.Keys![0]) ?? row.Keys![0], row.Metrics)).ToArray();

    private static string EscapePrefix(string prefix) => string.Concat(prefix.Select(character =>
        @"\.^$|?*+()[]{}".Contains(character) ? "\\" + character : character.ToString()));

    private sealed record SearchAnalyticsResponse(SearchAnalyticsRow[]? Rows);
    private sealed record SearchAnalyticsRow(string[]? Keys, double Clicks, double Impressions, double Ctr, double Position)
    {
        public SearchTrafficMetrics Metrics => new(Clicks, Impressions, Ctr, Position);
    }
}
