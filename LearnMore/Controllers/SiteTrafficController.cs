using LearnMore.Services;
using Microsoft.AspNetCore.Mvc;

namespace LearnMore.Controllers;

public sealed record SiteTrafficBucket(string Label, long Views);
public sealed record SiteTrafficPage(int Days, DateOnly Today, DateTimeOffset? StartedAt, DateTimeOffset? LastViewAt,
    bool SavePending, long TodayViews, IReadOnlyList<SiteTrafficBucket> Daily, IReadOnlyList<SiteTrafficBucket> Pages,
    IReadOnlyList<SiteTrafficBucket> Sources, SearchTrafficReport Search);

[Route("Management/Traffic")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class SiteTrafficController(IErrorReportManagerAccess access, SiteTrafficStore store,
    SearchConsoleTraffic search, TimeProvider clock) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(int days = 7, CancellationToken cancellationToken = default)
    {
        var denied = await access.AuthorizeAsync(this, cancellationToken);
        if (denied != null) return denied;

        days = days == 30 ? 30 : 7;
        var today = SiteTrafficStore.Day(clock.GetUtcNow());
        var start = today.AddDays(1 - days);
        var traffic = store.Read();
        var rows = traffic.Snapshot?.Counts.Where(row => row.Day >= start && row.Day <= today).ToArray() ?? [];
        var byDay = rows.GroupBy(row => row.Day).ToDictionary(group => group.Key, group => group.Sum(row => row.Views));
        var firstRecorded = traffic.Snapshot == null ? today.AddDays(1) : SiteTrafficStore.Day(traffic.Snapshot.StartedAt);
        var daily = Enumerable.Range(0, days).Select(start.AddDays).Where(day => day >= firstRecorded)
            .Select(day => new SiteTrafficBucket(day.ToString("MM/dd"), byDay.GetValueOrDefault(day))).ToArray();
        var report = await search.ReadAsync(days, cancellationToken);

        return View(new SiteTrafficPage(days, today, traffic.Snapshot?.StartedAt, traffic.Snapshot?.LastViewAt,
            traffic.SavePending, byDay.GetValueOrDefault(today), daily, Aggregate(rows, row => row.Page),
            Aggregate(rows, row => row.Source), report));
    }

    private static SiteTrafficBucket[] Aggregate(IEnumerable<SiteTrafficCount> rows, Func<SiteTrafficCount, string> select)
        => rows.GroupBy(select).Select(group => new SiteTrafficBucket(group.Key, group.Sum(row => row.Views)))
            .OrderByDescending(bucket => bucket.Views).ThenBy(bucket => bucket.Label, StringComparer.Ordinal).ToArray();
}
