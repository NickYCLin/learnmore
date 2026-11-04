using LearnMore.Controllers;
using LearnMore.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnMore.Tests;

public class LyricsEditorRazorTests
{
    [Fact]
    public async Task CompiledEditorKeepsAllLineFieldsAndOneToolbarWithoutChangingStoredOrder()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(LyricsController).Assembly.FullName, EnvironmentName = "Testing"
        });
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(LyricsController).Assembly);
        await using var application = builder.Build();
        using var scope = application.Services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        var routeData = new RouteData();
        routeData.Routers.Add(new RouteCollection());
        var actionContext = new ActionContext(context, routeData, new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var model = new EditLyricsViewModel
        {
            SongUid = "fixture-song", Lyrics = new()
            {
                new() { LyricID = 42, TimeStamp = 12.3, Japanese = "first-fixture" },
                new() { LyricID = 7, TimeStamp = 3.1, Japanese = "second-fixture" }
            }
        };
        var engine = scope.ServiceProvider.GetRequiredService<IRazorViewEngine>();
        var view = engine.GetView(null, "/Views/Media/EditLyrics.cshtml", false);
        Assert.True(view.Success);
        using var writer = new StringWriter();
        var data = new ViewDataDictionary<EditLyricsViewModel>(new EmptyModelMetadataProvider(), new ModelStateDictionary()) { Model = model };
        var tempData = new TempDataDictionary(context, scope.ServiceProvider.GetRequiredService<ITempDataProvider>());
        await view.View.RenderAsync(new ViewContext(actionContext, view.View, data, tempData, writer, new HtmlHelperOptions()));
        string html = writer.ToString();
        Assert.Equal(1, html.Split("id=\"saveLyricsBtn\"").Length - 1);
        Assert.Contains("lyrics-editor-toolbar", html);
        Assert.Contains("aria-live=\"polite\"", html);
        foreach (string field in new[] { "TimeStamp", "Japanese", "Chinese", "Roman" })
        {
            Assert.Contains($"name=\"lyrics[0].{field}\"", html);
            Assert.Contains($"name=\"lyrics[1].{field}\"", html);
        }
        Assert.True(html.IndexOf("data-id=\"42\"", StringComparison.Ordinal) < html.IndexOf("data-id=\"7\"", StringComparison.Ordinal));
        Assert.Contains("/Media/EditLyrics", html);
        Assert.Contains("newOrder: newOrder", html);
    }
}
