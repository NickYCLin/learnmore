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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LearnMore.Tests;

public class ErrorReportReadOnlyTests
{
    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task RealHttpRoutesDenyBothRepresentationsAndDisableCaching(int status)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        { ApplicationName = typeof(ErrorReportsController).Assembly.FullName, EnvironmentName = "Testing" });
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(ErrorReportsController).Assembly);
        var reader = new Reader();
        builder.Services.AddSingleton<IErrorReportManagerAccess>(new Access(new StatusCodeResult(status)));
        builder.Services.AddSingleton<IErrorReportReader>(reader);
        await using var application = builder.Build();
        application.MapControllers();
        application.Urls.Add("http://127.0.0.1:0");
        await application.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(application.Urls.Single()) };
        foreach (var path in new[] { "/Management/ErrorReports", "/Management/ErrorReports/data?pageSize=101" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(status, (int)response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
        using var mutation = await client.PostAsync("/Management/ErrorReports/data", new StringContent("{}"));
        Assert.Equal(405, (int)mutation.StatusCode);
        Assert.Equal(0, reader.Calls);
        await application.StopAsync();
    }

    [Fact]
    public async Task RealHttpManagerPaginationBindsAndRejectsInvalidText()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        { ApplicationName = typeof(ErrorReportsController).Assembly.FullName, EnvironmentName = "Testing" });
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(ErrorReportsController).Assembly);
        var reader = new Reader();
        builder.Services.AddSingleton<IErrorReportManagerAccess>(new Access(null));
        builder.Services.AddSingleton<IErrorReportReader>(reader);
        await using var application = builder.Build();
        application.MapControllers();
        application.Urls.Add("http://127.0.0.1:0");
        await application.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(application.Urls.Single()) };
        using var allowed = await client.GetAsync("/Management/ErrorReports/data?page=2&pageSize=1");
        Assert.Equal(200, (int)allowed.StatusCode);
        Assert.Equal((2, 1), reader.Request);
        Assert.Contains("song-version-b", await allowed.Content.ReadAsStringAsync());
        foreach (var query in new[] { "page=wrong", "pageSize=101", "page=0", "pageSize=0" })
        {
            using var invalid = await client.GetAsync("/Management/ErrorReports/data?" + query);
            Assert.Equal(400, (int)invalid.StatusCode);
        }
        Assert.Equal(1, reader.Calls);
        await application.StopAsync();
    }

    [Theory]
    [InlineData(false, 401)]
    [InlineData(true, 401)]
    [InlineData(false, 403)]
    [InlineData(true, 403)]
    public async Task DeniedHtmlAndJsonNeverReadReports(bool json, int status)
    {
        var reader = new Reader();
        var controller = Create(new Access(new StatusCodeResult(status)), reader);
        var result = json ? await controller.Data() : await controller.Index();
        Assert.Equal(status, Assert.IsType<StatusCodeResult>(result).StatusCode);
        Assert.Equal(0, reader.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealPolicyRejectsGuestBeforeOpeningAnyDatabase(bool json)
    {
        var context = new DefaultHttpContext();
        context.Session = new MemorySession();
        var reader = new Reader();
        var controller = Create(new ErrorReportManagerAccess(new ConfigurationBuilder().Build()), reader);
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        Assert.IsType<UnauthorizedObjectResult>(json ? await controller.Data() : await controller.Index());
        Assert.Equal(0, reader.Calls);
    }

    [Theory]
    [InlineData(0, 25)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    [InlineData(-1, 100)]
    public async Task InvalidPaginationRejectedWithoutRead(int page, int pageSize)
    {
        var reader = new Reader();
        var controller = Create(new Access(null), reader);
        Assert.IsType<BadRequestObjectResult>(await controller.Data(page, pageSize));
        Assert.Equal(0, reader.Calls);
    }

    [Fact]
    public async Task ManagerGetsOriginalStatusAndExactSongVersionWithoutFilteringUnresolved()
    {
        var reader = new Reader();
        var controller = Create(new Access(null), reader);
        var result = Assert.IsType<JsonResult>(await controller.Data(2, 1));
        var page = Assert.IsType<ErrorReportPage>(result.Value);
        Assert.Equal((2, 1), reader.Request);
        Assert.Equal("song-version-b", page.Items.Single().SongUid);
        Assert.Equal("0", page.Items.Single().Status);
        Assert.Equal(3, page.TotalPages);
        Assert.Equal(1, reader.Calls);
    }

    [Fact]
    public async Task UnknownSchemaIsUnavailableNotAnEmptyList()
    {
        var result = Assert.IsType<ObjectResult>(await Create(new Access(null), new Reader { SchemaMissing = true }).Data());
        Assert.Equal(503, result.StatusCode);
    }

    [Fact]
    public void IdentifierFromMetadataCannotEscapeAndPageArithmeticDoesNotOverflow()
    {
        Assert.Equal("[ID]]; DROP TABLE Songs;--]", ErrorReportReader.QuoteIdentifier("ID]; DROP TABLE Songs;--"));
        Assert.True(ErrorReportReader.IsValidPage(int.MaxValue, 100));
        Assert.Equal(0, new ErrorReportPage(1, 25, 0, []).TotalPages);
        Assert.Equal(2, new ErrorReportPage(1, 25, 26, []).TotalPages);
    }

    [Fact]
    public async Task ActualRazorEncodesDescriptionAndDoesNotCreateMutationForms()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        { ApplicationName = typeof(ErrorReportsController).Assembly.FullName, EnvironmentName = "Testing" });
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(ErrorReportsController).Assembly);
        await using var application = builder.Build();
        using var scope = application.Services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        var action = new ActionContext(context, new RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var view = scope.ServiceProvider.GetRequiredService<IRazorViewEngine>().GetView(null, "/Views/ErrorReports/Index.cshtml", false);
        Assert.True(view.Success);
        var model = new ErrorReportPage(1, 25, 3, [
            new("7", null, "<script>title</script>", "javascript:bad", "42", "<script>alert(1)</script>", "0", null),
            new("47", RubyRepairTarget.SongUid, "水平線", RubyRepairTarget.VideoSource, "7", "秒數", "0", null),
            new("50", RubyRepairTarget.SongUid, "水平線", RubyRepairTarget.VideoSource, "22", "分句", "0", null)]);
        context.Request.PathBase = "/LearnMore";
        var data = new ViewDataDictionary<ErrorReportPage>(new EmptyModelMetadataProvider(), new ModelStateDictionary()) { Model = model };
        var temp = new TempDataDictionary(context, scope.ServiceProvider.GetRequiredService<ITempDataProvider>());
        using var writer = new StringWriter();
        await view.View.RenderAsync(new ViewContext(action, view.View, data, temp, writer, new HtmlHelperOptions()));
        var html = writer.ToString();
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<form", html);
        Assert.DoesNotContain("href=\"javascript:", html);
        Assert.Contains("42", html);
        Assert.Contains("href=\"/LearnMore/Management/ErrorReports/raw/47\"", html);
        Assert.Contains("href=\"/LearnMore/Management/ErrorReports/raw/50\"", html);
        Assert.Contains("href=\"/LearnMore/Management/ErrorReports/split/50\"", html);
        Assert.DoesNotContain("/split/47", html);
        Assert.DoesNotContain("/raw/7\"", html);
    }

    private static ErrorReportsController Create(IErrorReportManagerAccess access, IErrorReportReader reader)
        => new(access, reader, NullLogger<ErrorReportsController>.Instance);

    private sealed class Access(IActionResult? result) : IErrorReportManagerAccess
    {
        public Task<IActionResult?> AuthorizeAsync(ControllerBase controller, CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class Reader : IErrorReportReader
    {
        public int Calls { get; private set; }
        public (int, int) Request { get; private set; }
        public bool SchemaMissing { get; init; }
        public Task<ErrorReportPage> ReadAsync(int page, int pageSize, CancellationToken cancellationToken)
        {
            Calls++;
            Request = (page, pageSize);
            if (SchemaMissing) throw new ErrorReportSchemaException();
            return Task.FromResult(new ErrorReportPage(page, pageSize, 3, [new("7", "song-version-b", "title", "video-b", "42", "description", "0", null)]));
        }
    }

    private sealed class MemorySession : ISession
    {
        public bool IsAvailable => true;
        public string Id => "test";
        public IEnumerable<string> Keys => [];
        public void Clear() { }
        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Remove(string key) { }
        public void Set(string key, byte[] value) { }
        public bool TryGetValue(string key, out byte[] value) { value = []; return false; }
    }
}
