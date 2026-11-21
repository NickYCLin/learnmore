using LearnMore.Controllers;
using LearnMore.Models;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnMore.Tests;

public class PlayerLayoutTests
{
    [Fact]
    public async Task CompiledPagesKeepPathBaseAndLimitPlayerChromeToPlayerPages()
    {
        var root = ProjectRoot();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(LyricsController).Assembly.FullName,
            EnvironmentName = "Testing", ContentRootPath = Path.Combine(root, "LearnMore")
        });
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(LyricsController).Assembly);
        builder.Services.AddHttpContextAccessor();
        builder.WebHost.UseSetting("urls", "http://127.0.0.1:0");
        await using var application = builder.Build();
        application.MapControllers();
        application.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
        await application.StartAsync();
        using var scope = application.Services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("fixture.test");
        context.Request.PathBase = "/LearnMore";
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(), "synthetic-ui"));
        context.Features.Set<ISessionFeature>(new FixtureSessionFeature());
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = context;
        var route = new RouteData();
        route.Routers.Add(new RouteCollection());
        var action = new ActionContext(context, route, new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var lyrics = Enumerable.Range(0, 12).Select(index => new Lyrics
        {
            LyricID = index + 1, TimeStamp = index * 5, Japanese = $"合成歌詞 {index + 1}",
            Chinese = "測試翻譯", Roman = "fixture roman"
        }).ToList();
        async Task<string> Render(string viewPath, object? model, string requestPath)
        {
            context.Request.Path = requestPath;
            route.Values["controller"] = viewPath.Split('/')[2];
            route.Values["action"] = Path.GetFileNameWithoutExtension(viewPath);
            var engine = scope.ServiceProvider.GetRequiredService<IRazorViewEngine>();
            var view = engine.GetView(null, viewPath, true);
            Assert.True(view.Success, viewPath);
            using var writer = new StringWriter();
            var data = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary()) { Model = model };
            var temp = new TempDataDictionary(context, scope.ServiceProvider.GetRequiredService<ITempDataProvider>());
            await view.View.RenderAsync(new ViewContext(action, view.View, data, temp, writer, new HtmlHelperOptions()));
            return writer.ToString();
        }
        var song = new SongViewModel
        {
            SongUid = "fixture-song", Title = "合成測試歌曲", Artist = "合成演唱者",
            VideoPath = "https://www.youtube.com/watch?v=abcdefghijk", Lyrics = lyrics
        };
        var guest = await Render("/Views/Lyrics/Index.cshtml", song, "/Lyrics/Index");
        Assert.Contains("data-logged-in=\"false\"", guest);
        Assert.Contains("data-login-url=\"/LearnMore/Login", guest);
        Assert.Contains("data-groups-url=\"/LearnMore/api/SongGroup/list\"", guest);
        Assert.Contains("/LearnMore/css/player-layout.css?v=", guest);
        Assert.DoesNotContain("id=\"navigation-bar\"", guest);
        song.UserEmail = "fixture@example.invalid";
        var member = await Render("/Views/Lyrics/Index.cshtml", song, "/Lyrics/Index");
        Assert.Contains("data-logged-in=\"true\"", member);
        var group = await Render("/Views/GroupPlayer/Play.cshtml", new GroupPlayerViewModel
        {
            GroupUid = "fixture-group", GroupName = "合成群組", Songs = new()
            {
                new() { SongUid = song.SongUid, Title = song.Title, YouTubeVideoUrl = song.VideoPath }
            }
        }, "/GroupPlayer/Play/fixture-group");
        var home = await Render("/Views/Home/Index.cshtml", new[]
        {
            new Songs { SongUid = song.SongUid, Title = song.Title, YouTubeVideoUrl = song.VideoPath }
        }, "/Home/Index");
        Assert.Contains("player-search-toggle", group);
        Assert.DoesNotContain("player-search-toggle", home);
        Assert.DoesNotContain("/js/player-layout.js", home);
        var fixtureDirectory = Environment.GetEnvironmentVariable("LEARNMORE_UI_FIXTURES");
        if (!string.IsNullOrEmpty(fixtureDirectory))
        {
            var login = await Render("/Views/Login/Index.cshtml", null, "/Login");
            var wish = await Render("/Views/Media/Wish.cshtml", null, "/Media/Wish");
            context.Session.SetString("Email", "fixture@example.invalid");
            var wishMember = await Render("/Views/Media/Wish.cshtml", null, "/Media/Wish");
            Directory.CreateDirectory(fixtureDirectory);
            foreach (var (name, html) in new[] { ("lyrics", guest), ("member", member), ("group", group), ("home", home), ("login", login), ("wish", wish), ("wish-member", wishMember) })
                await File.WriteAllTextAsync(Path.Combine(fixtureDirectory, name + ".html"), html);
        }
        await application.StopAsync();
    }

    [Fact]
    public void PlayerAssetsPreserveMobileBoundaryAndOfficialPlayerPolicy()
    {
        string Read(string path) => File.ReadAllText(Path.Combine(ProjectRoot(), "LearnMore", path));
        var layout = Read("wwwroot/js/player-layout.js");
        Assert.Contains("(max-width: 991.98px)", layout);
        Assert.Contains("details.open = !mobile.matches", layout);
        Assert.Contains("stack.after(details)", layout);
        Assert.Contains("navigator.share", layout);
        Assert.Contains("new URL(window.location.href)", layout);
        Assert.DoesNotContain("youtube.com", layout);
        var css = Read("wwwroot/css/player-layout.css");
        Assert.Contains("env(safe-area-inset-bottom", css);
        Assert.Contains("min-width: 44px; min-height: 44px", css);
        foreach (var path in new[] { "Views/Lyrics/Index.cshtml", "Views/GroupPlayer/Play.cshtml", "wwwroot/js/mobile-player.js" })
        {
            Assert.Contains("part !== 'picture-in-picture'", Read(path));
            Assert.Contains("playsinline", Read(path));
        }
        var home = Read("wwwroot/js/home.js");
        Assert.Contains("(min-width: 992px)", home);
        Assert.DoesNotContain("picture-in-picture", home);
        var group = Read("Views/GroupPlayer/Play.cshtml");
        Assert.Contains("!document.hidden && playing", group);
        Assert.Contains("loopStart !== null) seekGroupPractice(loopStart)", group);
    }

    private static string ProjectRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "LearnMore.sln"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("LearnMore.sln");
    }

    private sealed class FixtureSessionFeature : ISessionFeature
    {
        public ISession Session { get; set; } = new FixtureSession();
    }

    private sealed class FixtureSession : ISession
    {
        private readonly Dictionary<string, byte[]> values = new();
        public bool IsAvailable => true;
        public string Id => "synthetic-ui-session";
        public IEnumerable<string> Keys => values.Keys;
        public void Clear() => values.Clear();
        public void Remove(string key) => values.Remove(key);
        public void Set(string key, byte[] value) => values[key] = value;
        public bool TryGetValue(string key, [NotNullWhen(true)] out byte[]? value) => values.TryGetValue(key, out value);
        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
