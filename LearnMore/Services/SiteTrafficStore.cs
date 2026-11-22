using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace LearnMore.Services;

public sealed record SiteTrafficCount(DateOnly Day, string Page, string Source, long Views);
public sealed record SiteTrafficSnapshot(DateTimeOffset StartedAt, DateTimeOffset? LastViewAt,
    IReadOnlyList<SiteTrafficCount> Counts);
public sealed record SiteTrafficRead(SiteTrafficSnapshot? Snapshot, bool SavePending);

public sealed class SiteTrafficStore(IConfiguration configuration, IWebHostEnvironment environment, TimeProvider clock)
{
    private readonly object gate = new();
    private readonly Dictionary<(DateOnly Day, string Page, string Source), long> counts = new();
    private readonly string path = configuration["SiteTraffic:SnapshotPath"] is { Length: > 0 } configured
        ? Path.GetFullPath(configured, environment.ContentRootPath)
        : Path.Combine(environment.ContentRootPath, "App_Data", "site-traffic.json");
    private DateTimeOffset? startedAt;
    private DateTimeOffset? lastViewAt;
    private long revision;
    private long savedRevision;
    private bool savePending;

    public static DateOnly Day(DateTimeOffset instant) => DateOnly.FromDateTime(instant.ToOffset(TimeSpan.FromHours(8)).DateTime);

    public void Load()
    {
        SiteTrafficSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<SiteTrafficSnapshot>(File.ReadAllText(path))
                ?? throw new JsonException("Invalid site traffic snapshot.");
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            snapshot = null;
        }
        if (snapshot != null && (snapshot.StartedAt == default || snapshot.Counts == null))
            throw new JsonException("Invalid site traffic snapshot.");

        lock (gate)
        {
            if (snapshot != null)
            {
                foreach (var row in snapshot.Counts)
                    counts[(row.Day, row.Page, row.Source)] = row.Views;
                lastViewAt = snapshot.LastViewAt;
            }

            startedAt = snapshot?.StartedAt ?? clock.GetUtcNow();
            revision++;
            Prune(Day(clock.GetUtcNow()));
        }
    }

    public void Record(string page, string source)
    {
        lock (gate)
        {
            if (startedAt == null) return;
            var now = clock.GetUtcNow();
            var day = Day(now);
            var key = (day, page, source);
            counts[key] = counts.GetValueOrDefault(key) + 1;
            lastViewAt = now;
            revision++;
        }
    }

    public SiteTrafficRead Read()
    {
        lock (gate)
            return new SiteTrafficRead(startedAt == null ? null : Snapshot(), savePending);
    }

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        SiteTrafficSnapshot snapshot;
        long snapshotRevision;
        lock (gate)
        {
            if (startedAt == null) return;
            Prune(Day(clock.GetUtcNow()));
            if (savedRevision == revision) return;
            snapshot = Snapshot();
            snapshotRevision = revision;
        }

        var temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, snapshot, cancellationToken: cancellationToken);
            File.Move(temporary, path, overwrite: true);
            lock (gate)
            {
                savedRevision = snapshotRevision;
                savePending = false;
            }
        }
        catch
        {
            lock (gate) savePending = true;
            throw;
        }
    }

    private SiteTrafficSnapshot Snapshot() => new(startedAt!.Value, lastViewAt,
        counts.Select(entry => new SiteTrafficCount(entry.Key.Day, entry.Key.Page, entry.Key.Source, entry.Value)).ToArray());

    private void Prune(DateOnly today)
    {
        foreach (var key in counts.Keys.Where(key => key.Day < today.AddDays(-29)).ToArray())
        {
            counts.Remove(key);
            revision++;
        }
    }
}

public sealed class SiteTrafficWriter(SiteTrafficStore store, ILogger<SiteTrafficWriter> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            store.Load();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogError(exception, "Site traffic snapshot could not be loaded; existing data will not be overwritten.");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            await PersistAsync(stoppingToken);
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await PersistAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await PersistAsync(timeout.Token); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                logger.LogWarning("Site traffic final snapshot timed out.");
            }
        }
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        try { await store.SaveAsync(cancellationToken); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Site traffic snapshot could not be saved.");
        }
    }
}

public sealed class SiteTrafficMiddleware(RequestDelegate next)
{
    private static readonly string[] BotMarkers = ["bot", "crawler", "spider", "preview", "curl/", "wget/", "facebookexternalhit"];
    private static readonly string[] SocialDomains = ["facebook.com", "instagram.com", "threads.net", "threads.com", "line.me", "youtube.com", "youtu.be", "x.com", "t.co"];

    public async Task InvokeAsync(HttpContext context, SiteTrafficStore store)
    {
        var page = Page(context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>());
        var request = context.Request;
        if (page != null && HttpMethods.IsGet(request.Method)
            && !KnownBot(request.Headers.UserAgent.ToString())
            && !request.Headers.ContainsKey("Purpose") && !request.Headers.ContainsKey("Sec-Purpose"))
        {
            var source = Source(request);
            context.Response.OnCompleted(() =>
            {
                if (context.Response.StatusCode == StatusCodes.Status200OK && !context.RequestAborted.IsCancellationRequested
                    && context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true)
                    store.Record(page, source);
                return Task.CompletedTask;
            });
        }
        await next(context);
    }

    private static string? Page(ControllerActionDescriptor? endpoint) => (endpoint?.ControllerName, endpoint?.ActionName) switch
    {
        ("Home", "Index" or "GroupByUid") => "首頁與歌曲分類",
        ("Home", "Search") => "站內歌曲搜尋",
        ("Lyrics", "Index") => "歌曲與歌詞",
        ("GroupPlayer", "Play" or "PlayByQuery" or "PlayPerformer") => "群組播放",
        ("GroupPlayer", "SongDetail" or "SongDetailByQuery") => "群組歌曲資訊",
        ("Media", "Wish") => "許願留言板",
        _ => null
    };

    private static bool KnownBot(string userAgent) => string.IsNullOrWhiteSpace(userAgent) ||
        BotMarkers.Any(marker => userAgent.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static string Source(HttpRequest request)
    {
        if (!Uri.TryCreate(request.Headers.Referer.ToString(), UriKind.Absolute, out var referrer)
            || referrer.Scheme is not ("http" or "https")) return "無來源資訊";
        var host = referrer.IdnHost;
        if (host.Equals(request.Host.Host, StringComparison.OrdinalIgnoreCase)) return "站內導覽";
        if (InDomain(host, "google.com") || InDomain(host, "google.com.tw")) return "Google";
        if (InDomain(host, "bing.com") || InDomain(host, "search.yahoo.com") || InDomain(host, "duckduckgo.com")) return "其他搜尋網站";
        if (SocialDomains.Any(domain => InDomain(host, domain))) return "社群與影音網站";
        return "其他外部網站";
    }

    private static bool InDomain(string host, string domain) => host.Equals(domain, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
}
