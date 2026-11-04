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

public class LyricsProcessingNoticeRazorTests
{
    [Theory]
    [InlineData("high_accuracy_needs_review", "完整翻譯", false, false)]
    [InlineData("high_accuracy_completed", "完整翻譯", false, false)]
    [InlineData("high_accuracy_needs_review", "翻譯中...", false, true)]
    [InlineData("high_accuracy_completed", "", false, true)]
    [InlineData("high_accuracy_completed", "完整翻譯", true, true)]
    [InlineData("high_accuracy_pending", "完整翻譯", false, true)]
    [InlineData("high_accuracy_processing", "完整翻譯", false, true)]
    [InlineData("high_accuracy_failed", "完整翻譯", false, true)]
    public async Task ActualRazorPreservesNecessaryWarningsWithoutPublicManualReviewNoise(
        string status, string translation, bool empty, bool expected)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(LyricsController).Assembly.FullName, EnvironmentName = "Testing"
        });
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(LyricsController).Assembly);
        await using var application = builder.Build();
        using var scope = application.Services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        var action = new ActionContext(context, new RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var model = new SongViewModel { HighAccuracyStatus = status, HighAccuracyStatusReason = "internal-detail-not-public" };
        if (!empty) model.Lyrics.Add(new Lyrics { Chinese = translation });
        var view = scope.ServiceProvider.GetRequiredService<IRazorViewEngine>()
            .GetView(null, "/Views/Lyrics/_ProcessingNotice.cshtml", false);
        Assert.True(view.Success);
        using var writer = new StringWriter();
        var data = new ViewDataDictionary<SongViewModel>(new EmptyModelMetadataProvider(), new ModelStateDictionary()) { Model = model };
        var temp = new TempDataDictionary(context, scope.ServiceProvider.GetRequiredService<ITempDataProvider>());
        await view.View.RenderAsync(new ViewContext(action, view.View, data, temp, writer, new HtmlHelperOptions()));
        string html = writer.ToString();
        Assert.Equal(expected, html.Contains("lyrics-processing-alert", StringComparison.Ordinal));
        Assert.DoesNotContain("internal-detail-not-public", html);
        Assert.DoesNotContain("需要人工校正", html);
        Assert.Contains("_ProcessingNotice", File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "LearnMore", "Views", "Lyrics", "Index.cshtml")));
    }
}
