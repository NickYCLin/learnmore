using System.Text.Json;
using LearnMore.Controllers;
using LearnMore.Models;
using LearnMore.Services;
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

public class SongCommentRazorTests
{
    [Theory]
    [InlineData(11, false, true)]
    [InlineData(22, false, true)]
    [InlineData(33, true, true)]
    [InlineData(44, true, true)]
    [InlineData(55, false, false)]
    [InlineData(null, false, false)]
    public async Task CompiledRazorAndJsonExposeOnlyAuthorizedParentAndReply(int? viewerId, bool manager, bool expected)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(LyricsController).Assembly.FullName,
            EnvironmentName = "Testing"
        });
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(LyricsController).Assembly);
        await using var application = builder.Build();
        using var scope = application.Services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        var actionContext = new ActionContext(context, new RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var comment = new CommentModel
        {
            SongUid = "song-a", Message = "private-body-fixture", IsPrivate = true, CanViewPrivate = true,
            Replies = new() { new() { ReplyMessage = "private-reply-fixture" } }
        };
        var model = new SongViewModel
        {
            Comments = SongCommentReadPolicy.CanRead(true, "song-a", comment.SongUid, viewerId, 11, 22, manager)
                ? new() { comment } : new()
        };
        var engine = scope.ServiceProvider.GetRequiredService<IRazorViewEngine>();
        var view = engine.GetView(null, "/Views/Lyrics/_SongComments.cshtml", false);
        Assert.True(view.Success, string.Join(",", view.SearchedLocations ?? Array.Empty<string>()));
        using var writer = new StringWriter();
        var data = new ViewDataDictionary<SongViewModel>(new EmptyModelMetadataProvider(), new ModelStateDictionary()) { Model = model };
        var tempData = new TempDataDictionary(context, scope.ServiceProvider.GetRequiredService<ITempDataProvider>());
        await view.View.RenderAsync(new ViewContext(actionContext, view.View, data, tempData, writer, new HtmlHelperOptions()));
        string html = writer.ToString();
        string json = JsonSerializer.Serialize(model.Comments);
        Assert.Equal(expected, html.Contains("private-body-fixture", StringComparison.Ordinal));
        Assert.Equal(expected, html.Contains("private-reply-fixture", StringComparison.Ordinal));
        Assert.Equal(expected, json.Contains("private-body-fixture", StringComparison.Ordinal));
        Assert.Equal(expected, json.Contains("private-reply-fixture", StringComparison.Ordinal));
    }
}
