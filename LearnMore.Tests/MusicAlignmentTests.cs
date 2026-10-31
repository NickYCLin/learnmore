using System.Data.SqlClient;
using System.Net;
using System.Text;
using LearnMore.Models;
using LearnMore.Options;
using LearnMore.Services;
using LearnMore.Controllers.API;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using static LearnMore.Services.MusicAlignmentValidation;

namespace LearnMore.Tests;

public sealed class MusicAlignmentAuthTests
{
    [Theory]
    [InlineData(false, "read", false, false)]
    [InlineData(true, "", false, false)]
    [InlineData(true, "gpu", false, false)]
    [InlineData(true, "read", false, true)]
    [InlineData(true, "read", true, false)]
    [InlineData(true, "write", false, true)]
    [InlineData(true, "write", true, true)]
    public async Task DedicatedCredentialsEnforceToolScope(bool enabled, string token, bool write, bool allowed)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        { ["MusicAutomation:Enabled"]=enabled.ToString(), ["MusicAutomation:ReadToken"]=new('r',40), ["MusicAutomation:WriteToken"]=new('w',40) }).Build();
        var http = new DefaultHttpContext();
        http.Request.Headers.Authorization = "Bearer " + (token switch { "read"=>new string('r',40),"write"=>new string('w',40),_=>token });
        var descriptor = new ActionDescriptor { EndpointMetadata = write ? new List<object>{ new MusicAutomationWriteAttribute() } : new() };
        var action = new ActionContext(http,new RouteData(),descriptor);
        var context = new ActionExecutingContext(action,new List<IFilterMetadata>(),new Dictionary<string,object?>(),new object());
        var ran = false;
        await new MusicAutomationAuthorizeFilter(config).OnActionExecutionAsync(context, () => { ran=true; return Task.FromResult(new ActionExecutedContext(action,new List<IFilterMetadata>(),new object())); });
        Assert.Equal(allowed,ran);
        if (!allowed) Assert.IsType(enabled ? typeof(UnauthorizedResult) : typeof(NotFoundResult),context.Result);
    }

    [Theory]
    [InlineData("http://youtube.com/watch?v=LjuJgrdh5eY")]
    [InlineData("https://youtube.com.evil.invalid/watch?v=LjuJgrdh5eY")]
    [InlineData("https://youtube.com@evil.invalid/watch?v=LjuJgrdh5eY")]
    [InlineData("https://youtube.com/watch?v=../../abc")]
    public void RejectsUnsupportedVideoSources(string value) => Assert.Throws<MusicAutomationException>(()=>MusicAlignmentStore.VideoId(value));
}

public sealed class MusicAlignmentSqlTests : IAsyncLifetime
{
    private string? master;
    private string? connection;
    private readonly string database = "LearnMoreMusicAudit_"+Guid.NewGuid().ToString("N");
    private readonly string uid=Guid.NewGuid().ToString("D");
    private MusicAlignmentStore Store => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["ConnectionStrings:DefaultConnection"]=connection }).Build());
    public async Task InitializeAsync()
    {
        var value=Environment.GetEnvironmentVariable("LEARNMORE_SQL_AUDIT");
        if(string.IsNullOrEmpty(value))return;
        var b=new SqlConnectionStringBuilder(value);
        if(!b.DataSource.StartsWith("127.0.0.1,",StringComparison.Ordinal)||b.InitialCatalog!="master")throw new InvalidOperationException("Isolated loopback SQL only");
        master=b.ConnectionString;await Exec(master,$"CREATE DATABASE [{database}]");b.InitialCatalog=database;connection=b.ConnectionString;
        await Sql("CREATE TABLE dbo.Songs(SongUid nvarchar(64) PRIMARY KEY,YouTubeVideoUrl nvarchar(512),HighAccuracyStatus nvarchar(64),HighAccuracyStatusReason nvarchar(512)); CREATE TABLE dbo.OwnershipSentinel(Id int PRIMARY KEY,Value nvarchar(100)); INSERT dbo.OwnershipSentinel VALUES(1,N'收藏與管理權限');");
        await Store.EnsureSchemaAsync();await Store.EnsureSchemaAsync();
        await Seed(uid);
    }
    private async Task Seed(string id)
    {
        await Sql($"INSERT dbo.Songs VALUES('{id}','https://www.youtube.com/watch?v=LjuJgrdh5eY','high_accuracy_needs_review',N'待驗證'); CREATE TABLE dbo.[Songs_{id}](LyricID int PRIMARY KEY,TimeStamp float,Japanese nvarchar(100),Chinese nvarchar(100),JapaneseRuby nvarchar(200),Roman nvarchar(100)); INSERT dbo.[Songs_{id}] VALUES(1,1,N'空を見上げる',N'抬頭看天空',N'<ruby>空<rt>そら</rt></ruby>を見上げる','sora o miageru'),(2,4,N'光が差し込む',N'光芒照進來',N'<ruby>光<rt>ひかり</rt></ruby>が差し込む','hikari ga sashikomu');");
    }
    public async Task DisposeAsync()
    {
        if(master==null)return;
        SqlConnection.ClearAllPools();await Exec(master,$"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]");
    }
    private static async Task Exec(string cs,string sql){await using var d=new SqlConnection(cs);await d.OpenAsync();using var q=new SqlCommand(sql,d);await q.ExecuteNonQueryAsync();}
    private Task Sql(string sql)=>Exec(connection!,sql);
    private async Task<string> Scalar(string sql){await using var d=new SqlConnection(connection);await d.OpenAsync();using var q=new SqlCommand(sql,d);return Convert.ToString(await q.ExecuteScalarAsync())!;}

    private async Task<MusicAlignmentJob> Verified(string? id=null,bool valid=true)
    {
        var job=await Store.StartAsync(Guid.NewGuid().ToString(),id??uid,null);
        var claim=(await Store.ClaimAsync())!.Value;
        Assert.Equal(job.JobId,claim.Id);
        var snapshot=claim.Snapshot;
        var lines=snapshot.Lyrics.Select((l,i)=>new {lyricId=l.LyricID,japanese=l.Japanese,start=i==0?1.25:4.5,end=i==0?3.0:6.0,score=valid?0.95:0.1,source="whisperx_word_match",whisperWordStartIndex=i+1,whisperWordEndIndex=i+1}).ToArray();
        var body=Serialize(new {songUid=snapshot.SongUid,youtubeUrl=snapshot.YoutubeUrl,language="ja",model="kotoba-whisper+vocals-stem+whisperx",durationSeconds=10,alignedLines=lines,matchedCount=2,totalCount=2});
        var remote=new RemoteHighAccuracyAlignmentClient(Microsoft.Extensions.Options.Options.Create(new VocalOnsetDetectionOptions{UseRemoteHighAccuracyApi=true,RemoteHighAccuracyApiBaseUrl="https://example.invalid/",RemoteHighAccuracyApiToken="test-only"}),new Factory(body),NullLogger<RemoteHighAccuracyAlignmentClient>.Instance);
        await Store.CompleteAsync(job.JobId,Build(snapshot,await remote.AlignWithReportAsync(new SongLyricsProcessingSnapshot {SongUid=snapshot.SongUid,YouTubeUrl=snapshot.YoutubeUrl,Lyrics=snapshot.Lyrics})));
        return await Store.GetAsync(job.JobId);
    }
    [SqlAuditFact]
    public async Task ToolResponsesUseStableCamelCaseDespiteWebsiteDefaults()
    {
        var controller=new MusicAutomationController(Store,NullLogger<MusicAutomationController>.Instance);
        var response=Assert.IsType<JsonResult>(await controller.Start(new MusicAutomationController.StartRequest("contract",uid),CancellationToken.None));
        Assert.Equal(202,response.StatusCode);
        var options=Assert.IsType<System.Text.Json.JsonSerializerOptions>(response.SerializerSettings);
        using var json=System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(response.Value,options));
        Assert.Equal("queued",json.RootElement.GetProperty("state").GetString());
        Assert.True(json.RootElement.TryGetProperty("jobId",out _));
        Assert.False(json.RootElement.TryGetProperty("JobId",out _));
    }

    [SqlAuditFact]
    public async Task LaterInsertedLyricsAreAlignedInPlaybackOrder()
    {
        await Sql($"INSERT dbo.[Songs_{uid}] VALUES(50,2.5,N'風が吹く',N'風吹過',N'<ruby>風<rt>かぜ</rt></ruby>が吹く','kaze ga fuku')");
        var job=await Store.StartAsync("inserted-line",uid,null);
        var claim=(await Store.ClaimAsync())!.Value;
        Assert.Equal(job.JobId,claim.Id);
        Assert.Equal(new[]{1,50,2},claim.Snapshot.Lyrics.Select(x=>x.LyricID));
        Assert.Equal(new[]{1.0,2.5,4.0},claim.Snapshot.Lyrics.Select(x=>x.TimeStamp));
    }

    [SqlAuditFact]
    public async Task DurableDeduplicationAndApplyPreserveTextAndOwnership()
    {
        var first=await Store.StartAsync("initial",uid,null);
        Assert.Equal(first.JobId,(await Store.StartAsync("initial",uid,null)).JobId);
        Assert.Equal(first.JobId,(await Store.StartAsync("alias",uid,null)).JobId);
        var job=await Verified();
        var texts=await Scalar($"SELECT Japanese+Chinese+JapaneseRuby+Roman FROM dbo.[Songs_{uid}] WHERE LyricID=1");
        var applied=await Store.ApplyAsync(job.JobId,job.Report!.PreviewHash);
        Assert.Equal("applied",applied.State);Assert.NotNull(applied.AppliedAt);
        Assert.Equal("1.25",await Scalar($"SELECT TimeStamp FROM dbo.[Songs_{uid}] WHERE LyricID=1"));
        Assert.Equal(texts,await Scalar($"SELECT Japanese+Chinese+JapaneseRuby+Roman FROM dbo.[Songs_{uid}] WHERE LyricID=1"));
        Assert.Equal("收藏與管理權限",await Scalar("SELECT Value FROM dbo.OwnershipSentinel WHERE Id=1"));
        Assert.Equal("high_accuracy_completed",await Scalar($"SELECT HighAccuracyStatus FROM dbo.Songs WHERE SongUid='{uid}'"));
        Assert.Equal("applied",(await Store.ApplyAsync(job.JobId,job.Report.PreviewHash)).State);
        Assert.Equal("1",await Scalar("SELECT COUNT(*) FROM dbo.MusicAlignmentBackups"));
        Assert.Equal(first.JobId,(await Store.StartAsync("alias",uid,null)).JobId);
    }
    [SqlAuditFact]
    public async Task ConcurrentApplyCommitsExactlyOneBackup()
    {
        var job=await Verified();
        var results=await Task.WhenAll(Store.ApplyAsync(job.JobId,job.Report!.PreviewHash),Store.ApplyAsync(job.JobId,job.Report.PreviewHash));
        Assert.All(results,r=>Assert.Equal("applied",r.State));Assert.Equal("1",await Scalar("SELECT COUNT(*) FROM dbo.MusicAlignmentBackups"));
    }
    [SqlAuditFact]
    public async Task AnyConcurrentSongEditRejectsTheOldPlan()
    {
        foreach(var field in new[]{"Chinese","Japanese","TimeStamp","HighAccuracyStatus","YouTubeVideoUrl"})
        {
            var id=field=="Chinese"?uid:Guid.NewGuid().ToString("D");if(id!=uid)await Seed(id);
            var job=await Verified(id);
            var change=field switch {"TimeStamp"=>$"UPDATE dbo.[Songs_{id}] SET TimeStamp=2 WHERE LyricID=1","HighAccuracyStatus"=>$"UPDATE dbo.Songs SET HighAccuracyStatus='high_accuracy_processing' WHERE SongUid='{id}'","YouTubeVideoUrl"=>$"UPDATE dbo.Songs SET YouTubeVideoUrl='https://www.youtube.com/watch?v=aaaaaaaaaaa' WHERE SongUid='{id}'",_=>$"UPDATE dbo.[Songs_{id}] SET {field}=N'已修改' WHERE LyricID=1"};
            await Sql(change);
            Assert.Equal("song_changed_since_analysis",(await Assert.ThrowsAsync<MusicAutomationException>(()=>Store.ApplyAsync(job.JobId,job.Report!.PreviewHash))).Code);
        }
        Assert.Equal("0",await Scalar("SELECT COUNT(*) FROM dbo.MusicAlignmentBackups"));
    }
    [SqlAuditFact]
    public async Task ReadbackFailureRollsBackEveryTimestampAndBackup()
    {
        var job=await Verified();
        await Sql($"CREATE TRIGGER dbo.MusicAuditChange ON dbo.[Songs_{uid}] AFTER UPDATE AS BEGIN SET NOCOUNT ON; UPDATE dbo.[Songs_{uid}] SET Chinese=N'非預期變更' WHERE LyricID=1; END");
        Assert.Equal("readback_failed",(await Assert.ThrowsAsync<MusicAutomationException>(()=>Store.ApplyAsync(job.JobId,job.Report!.PreviewHash))).Code);
        Assert.Equal("1",await Scalar($"SELECT TimeStamp FROM dbo.[Songs_{uid}] WHERE LyricID=1"));
        Assert.Equal("抬頭看天空",await Scalar($"SELECT Chinese FROM dbo.[Songs_{uid}] WHERE LyricID=1"));
        Assert.Equal("0",await Scalar("SELECT COUNT(*) FROM dbo.MusicAlignmentBackups"));Assert.Equal("verified",(await Store.GetAsync(job.JobId)).State);
    }
    [SqlAuditFact]
    public async Task FailedVerificationAndIncorrectPreviewCannotApply()
    {
        var job=await Verified(valid:false);Assert.Equal("needs_review",job.State);Assert.False(job.Report!.Accepted);
        await Assert.ThrowsAsync<MusicAutomationException>(()=>Store.ApplyAsync(job.JobId,job.Report.PreviewHash));
        Assert.Equal("1",await Scalar($"SELECT TimeStamp FROM dbo.[Songs_{uid}] WHERE LyricID=1"));
        Assert.Equal("0",await Scalar("SELECT COUNT(*) FROM dbo.MusicAlignmentBackups"));
        var good=await Verified();
        Assert.Equal("preview_changed",(await Assert.ThrowsAsync<MusicAutomationException>(()=>Store.ApplyAsync(good.JobId,new string('0',64)))).Code);
    }
    [SqlAuditFact]
    public async Task LeaseRecoveryDoesNotReplayAnInterruptedInference()
    {
        var job=await Store.StartAsync("restart",uid,null);Assert.NotNull(await Store.ClaimAsync());
        await Sql("UPDATE dbo.MusicAlignmentJobs SET LeaseUntil=DATEADD(minute,-1,SYSUTCDATETIME())");
        Assert.Null(await Store.ClaimAsync());Assert.Equal("interrupted",(await Store.GetAsync(job.JobId)).State);
        Assert.Equal(job.JobId,(await Store.StartAsync("restart",uid,null)).JobId);
        Assert.NotEqual(job.JobId,(await Store.StartAsync("explicit-new-attempt",uid,null)).JobId);
    }
    [SqlAuditFact]
    public async Task VideoAndRequestIdentityCannotBeSwapped()
    {
        var job=await Store.StartAsync("video",null,"https://youtu.be/LjuJgrdh5eY");Assert.Equal(uid,job.SongUid);
        var other=Guid.NewGuid().ToString("D");await Seed(other);
        Assert.Equal("request_id_reused",(await Assert.ThrowsAsync<MusicAutomationException>(()=>Store.StartAsync("video",other,null))).Code);
        Assert.Equal("song_video_mismatch",(await Assert.ThrowsAsync<MusicAutomationException>(()=>Store.StartAsync("mismatch",uid,"https://youtu.be/aaaaaaaaaaa"))).Code);
        Assert.Equal("upload_song_first",(await Assert.ThrowsAsync<MusicAutomationException>(()=>Store.StartAsync("new",null,"https://youtu.be/bbbbbbbbbbb"))).Code);
    }
    private sealed class Factory(string body):IHttpClientFactory { public HttpClient CreateClient(string name)=>new(new Handler(body)); }
    private sealed class Handler(string body):HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body,Encoding.UTF8,"application/json")}); }
}
