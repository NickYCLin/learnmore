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
    [Fact]
    public async Task UserManagementEncodesIdentityAndPreservesSearchPagination()
    {
        var user = new ManagedUser(17,"<script>bad</script>","","member@example.invalid",false,false,null,null,0,null);
        var html = await Render("ManagerUsers/Index",new ManagedUsersPage("member","all",2,100,[user]),true);
        Assert.Contains("&lt;script&gt;bad&lt;/script&gt;",html);
        Assert.Contains("status=all",html);
        Assert.Contains("search=member",html);
        Assert.Contains("page=4",html);
        Assert.Contains("aria-current=\"page\"",html);
        Assert.Contains("member@example.invalid",html);
    }

    [Fact]
    public async Task DeletedUserViewRetainsAuditAndHidesMutationForm()
    {
        var user = new ManagedUser(17,"Test","","member@example.invalid",false,true,DateTime.UtcNow,null,1,null);
        var html = WebUtility.HtmlDecode(await Render("ManagerUsers/Detail",new ManagedUserDetail(user,
            new Dictionary<string,long?> { ["上傳歌曲"]=1 },[],[new("delete","<script>reason</script>",2,DateTime.UtcNow)]),true));
        Assert.Contains("此帳號已刪除",html);
        Assert.DoesNotContain("name=\"reason\"",html);
        Assert.Contains("操作者 ID 2",html);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManageOnlyShowsGlobalManagementLinksToManagers(bool isManager)
    {
        var html = WebUtility.HtmlDecode(await Render("Media/Manage",
            new LearnMore.Models.ManageViewModel { IsManager = isManager }, true));
        Assert.Contains("待校正清單", html);
        Assert.Equal(isManager, html.Contains("全站待校正（限管理員）"));
        Assert.Equal(isManager, html.Contains("錯誤回報（限管理員）"));
        Assert.Equal(isManager, html.Contains("/Management/ReviewQueue"));
        Assert.Equal(isManager, html.Contains("/Management/ErrorReports"));
    }

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

    [Fact]
    public async Task WorkFormPreservesAuditAndEncodesNotes()
    {
        var evidence = new string('A', 64);
        var detail = new ManagementWorkDetail(new("report", "7", "標題", "問題", null, evidence),
            [new(42, evidence, "resolved", "<script>bad</script>", "manager@example.invalid", DateTime.UtcNow)]);
        var html = await Render("ManagementWork", detail);
        Assert.Contains("__RequestVerificationToken", html);
        Assert.Contains("name=\"ExpectedRevision\" value=\"42\"", html);
        Assert.Contains("name=\"EvidenceHash\" value=\"" + evidence, html);
        Assert.Contains("&lt;script&gt;bad&lt;/script&gt;", html);
        Assert.Contains("manager@example.invalid", html);
        Assert.Contains("value=\"resolved\" selected=\"selected\"", html);
        Assert.Contains("/LearnMore/Management/Work", html);
    }

    [Theory]
    [InlineData("TimingReviewQueue", "Management/ReviewQueue")]
    [InlineData("ErrorReports", "Management/ErrorReports")]
    public async Task NumberedPaginationIsBoundedAndPreservesPageSize(string viewName, string path)
    {
        object model = viewName == "TimingReviewQueue"
            ? new TimingReviewQueuePage(50, 1, 100, true, [])
            : new ErrorReportPage(50, 1, 100, []);
        var html = WebUtility.HtmlDecode(await Render(viewName, model));
        Assert.Contains($"/LearnMore/{path}?page=1&pageSize=1", html);
        Assert.Contains($"/LearnMore/{path}?page=100&pageSize=1", html);
        Assert.Contains($"/LearnMore/{path}?page=48&pageSize=1", html);
        Assert.Contains($"/LearnMore/{path}?page=52&pageSize=1", html);
        Assert.DoesNotContain($"/LearnMore/{path}?page=47&pageSize=1", html);
        Assert.Contains("aria-current=\"page\"", html);
    }

    [Fact]
    public async Task SongPaginationPreservesOtherSectionAndPathBase()
    {
        var html = WebUtility.HtmlDecode(await Render("Shared/_ManageSongPagination", new LearnMore.Models.ManageSongPagination("producer", 2, 85, 3), true));
        Assert.Contains("producerPage=1&collaborationPage=3#producer-songs", html);
        Assert.Contains("producerPage=5&collaborationPage=3#producer-songs", html);
        Assert.Contains("/LearnMore/Media/Manage?", html);
        Assert.Contains("21–40", html);
        Assert.Contains("aria-current=\"page\"", html);
        var empty = WebUtility.HtmlDecode(await Render("Shared/_ManageSongPagination", new LearnMore.Models.ManageSongPagination("collaboration", 1, 0, 2), true));
        Assert.Contains("0–0", empty);
        Assert.Contains("aria-disabled=\"true\"", empty);
    }

    private static async Task<string> Render<TModel>(string viewName, TModel model, bool partial = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        { ApplicationName = typeof(TimingReviewQueueController).Assembly.FullName, EnvironmentName = "Testing" });
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(TimingReviewQueueController).Assembly);
        await using var application = builder.Build();
        application.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
        application.Urls.Add("http://127.0.0.1:0");
        await application.StartAsync();
        using var scope = application.Services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider, Session = new PreviewSession() };
        context.Request.PathBase = "/LearnMore";
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(), "Razor preview"));
        var action = new ActionContext(context, new RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var viewPath = partial ? $"/Views/{viewName}.cshtml" : $"/Views/{viewName}/Index.cshtml";
        var view = scope.ServiceProvider.GetRequiredService<IRazorViewEngine>().GetView(null, viewPath, false);
        Assert.True(view.Success);
        var data = new ViewDataDictionary<TModel>(new EmptyModelMetadataProvider(), new ModelStateDictionary()) { Model = model };
        var temp = new TempDataDictionary(context, scope.ServiceProvider.GetRequiredService<ITempDataProvider>());
        using var writer = new StringWriter();
        await view.View.RenderAsync(new ViewContext(action, view.View, data, temp, writer, new HtmlHelperOptions()));
        return writer.ToString();
    }

    private sealed class PreviewSession : ISession
    {
        public bool IsAvailable => true;
        public string Id => "preview";
        public IEnumerable<string> Keys => [];
        public void Clear() { }
        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Remove(string key) { }
        public void Set(string key, byte[] value) { }
        public bool TryGetValue(string key, out byte[] value) { value = []; return false; }
    }
}
