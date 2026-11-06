using System.Net;
using LearnMore.Controllers;
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

public sealed class ManagementQueuePresentationTests
{
    [Theory]
    [InlineData("high_accuracy_partial", "部分完成")]
    [InlineData("high_accuracy_needs_review", "待人工檢查")]
    [InlineData("high_accuracy_failed", "校正失敗")]
    [InlineData("unknown_status", "unknown_status")]
    public async Task ReviewKeepsRawEvidenceWhileShowingReadableStatus(string status, string label)
    {
        var songUid = Guid.NewGuid().ToString();
        var jobId = Guid.NewGuid();
        var html = await Render("TimingReviewQueue", new TimingReviewQueuePage(2, 1, 3, true,
            [new(songUid, "歌曲標題", "https://youtube.com/watch?v=example", "example", status,
                "第一行\n第二行<script>bad</script>", jobId, "request-hash", "failed", "failure-code",
                new DateTime(2026, 11, 7), null)]));
        Assert.Contains(label, WebUtility.HtmlDecode(html));
        Assert.Contains(status, html);
        Assert.Contains(songUid, html);
        Assert.Contains(jobId.ToString(), html);
        Assert.Contains("request-hash", html);
        Assert.Contains("failure-code", html);
        Assert.Contains("&lt;script&gt;bad&lt;/script&gt;", html);
        Assert.Contains("<details class=\"mq-details\">", html);
        Assert.Contains("<summary>", html);
        Assert.Contains("/LearnMore/Lyrics/" + songUid, html);
        Assert.Contains("page=1", html);
        Assert.Contains("page=3", html);
        Assert.DoesNotContain("<main", html);
        Assert.DoesNotContain("<form", html);
    }

    [Theory]
    [InlineData("0", "新回報")]
    [InlineData("1", "原狀態：1")]
    [InlineData(null, "原狀態：NULL")]
    public async Task ReportsDoNotGuessMeaningOfOtherStatuses(string? status, string label)
    {
        var html = await Render("ErrorReports", new ErrorReportPage(1, 25, 1,
            [new("7", null, "標題", "javascript:bad", "42", "<script>bad</script>", status, null)]));
        Assert.Contains(label, WebUtility.HtmlDecode(html));
        Assert.Contains("問題描述", html);
        Assert.Contains("&lt;script&gt;bad&lt;/script&gt;", html);
        Assert.Contains("javascript:bad", html);
        Assert.DoesNotContain("href=\"javascript:", html);
        Assert.DoesNotContain("<form", html);
        Assert.Contains("aria-current=\"page\">錯誤回報", html);
        Assert.Contains("aria-disabled=\"true\">下一頁", html);
    }

    [Theory]
    [InlineData("TimingReviewQueue")]
    [InlineData("ErrorReports")]
    public async Task EmptyPagesHaveRecoveryLinkAndDisabledPagination(string viewName)
    {
        var html = viewName == "TimingReviewQueue"
            ? await Render(viewName, new TimingReviewQueuePage(1, 25, 0, false, []))
            : await Render(viewName, new ErrorReportPage(1, 25, 0, []));
        Assert.Contains("role=\"status\"", html);
        Assert.Contains("回到第一頁", html);
        Assert.Contains("尚無資料", WebUtility.HtmlDecode(html));
        Assert.Contains("aria-disabled=\"true\">上一頁", html);
        Assert.Contains("aria-disabled=\"true\">下一頁", html);
        Assert.DoesNotContain("class=\"mq-card\"", html);
    }

    private static async Task<string> Render<TModel>(string viewName, TModel model)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        { ApplicationName = typeof(TimingReviewQueueController).Assembly.FullName, EnvironmentName = "Testing" });
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(TimingReviewQueueController).Assembly);
        await using var application = builder.Build();
        using var scope = application.Services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.PathBase = "/LearnMore";
        var action = new ActionContext(context, new RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var view = scope.ServiceProvider.GetRequiredService<IRazorViewEngine>().GetView(null, $"/Views/{viewName}/Index.cshtml", false);
        Assert.True(view.Success);
        var data = new ViewDataDictionary<TModel>(new EmptyModelMetadataProvider(), new ModelStateDictionary()) { Model = model };
        var temp = new TempDataDictionary(context, scope.ServiceProvider.GetRequiredService<ITempDataProvider>());
        using var writer = new StringWriter();
        await view.View.RenderAsync(new ViewContext(action, view.View, data, temp, writer, new HtmlHelperOptions()));
        return writer.ToString();
    }
}
