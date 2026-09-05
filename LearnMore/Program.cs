using LearnMore.Controllers;
using LearnMore.Controllers.API;
using LearnMore.Options;
using LearnMore.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.StaticFiles;
using System.Data.SqlClient;
using System.IO.Compression;
using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Text;
using LearnMore.Services.Mobile;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Microsoft.IdentityModel.Tokens;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.local.json", optional: true, reloadOnChange: true);

builder.Services.Configure<WhisperRuntimeOptions>(options =>
{
    options.ApiKey = builder.Configuration["OpenAI:ApiKey"] ?? string.Empty;
    options.MyApiKey = builder.Configuration["OpenAI:MyApiKey"] ?? string.Empty;
    options.FfmpegPath = builder.Configuration["FFmpegPath"] ?? string.Empty;
    options.YtDlpPath = builder.Configuration["YtDlpPath"] ?? "yt-dlp";
    options.WhisperJsonPath = builder.Configuration["WhisperJsonPath"] ?? string.Empty;
    options.YtDlpCookiesPath = builder.Configuration["YtDlpCookiesPath"] ?? string.Empty;
    options.YtDlpDownloadTimeoutSeconds = Math.Max(30, builder.Configuration.GetValue<int?>("YtDlpDownloadTimeoutSeconds") ?? 600);
    options.EnableRuntimeOpenAiTranslation = builder.Configuration.GetValue<bool>("OpenAI:EnableRuntimeTranslation");
});

builder.Services.Configure<VocalOnsetDetectionOptions>(options =>
{
    options.PythonPath = builder.Configuration["PythonPath"] ?? string.Empty;
    options.HuggingFaceCacheRoot = builder.Configuration["HuggingFaceCacheRoot"] ?? string.Empty;
    options.FfmpegPath = builder.Configuration["FFmpegPath"] ?? string.Empty;
    options.OpenAiApiKey = builder.Configuration["OpenAI:ApiKey"] ?? string.Empty;
    options.OpenAiMyApiKey = builder.Configuration["OpenAI:MyApiKey"] ?? string.Empty;
    options.InitialSegmentationHighAccuracyModel = builder.Configuration["InitialSegmentationHighAccuracyModel"] ?? string.Empty;
    options.InitialSegmentationHighAccuracyFallbackModel = builder.Configuration["InitialSegmentationHighAccuracyFallbackModel"] ?? string.Empty;
    options.SecondaryAlignmentModel = builder.Configuration["SecondaryAlignmentModel"] ?? string.Empty;
    options.SecondaryAlignmentPythonPath = builder.Configuration["SecondaryAlignmentPythonPath"] ?? string.Empty;
    options.UseRemoteHighAccuracyApi = builder.Configuration.GetValue<bool?>("HighAccuracyRemoteApi:Enabled")
        ?? builder.Configuration.GetValue<bool?>("AudioStemProcessing:UseRemoteApi")
        ?? false;
    options.RemoteHighAccuracyApiFallbackToLocal = builder.Configuration.GetValue<bool?>("HighAccuracyRemoteApi:FallbackToLocal")
        ?? builder.Configuration.GetValue<bool?>("AudioStemProcessing:RemoteApiFallbackToLocal")
        ?? true;
    options.RemoteHighAccuracyApiBaseUrl = builder.Configuration["HighAccuracyRemoteApi:BaseUrl"]
        ?? builder.Configuration["AudioStemProcessing:RemoteApiBaseUrl"]
        ?? string.Empty;
    options.RemoteHighAccuracyApiToken = builder.Configuration["HighAccuracyRemoteApi:Token"]
        ?? builder.Configuration["AudioStemProcessing:RemoteApiToken"]
        ?? string.Empty;
    options.RemoteHighAccuracyApiTimeoutSeconds = Math.Max(60, builder.Configuration.GetValue<int?>("HighAccuracyRemoteApi:TimeoutSeconds")
        ?? builder.Configuration.GetValue<int?>("AudioStemProcessing:RemoteApiTimeoutSeconds")
        ?? 1800);
});

builder.Services.Configure<AudioStemProcessingOptions>(options =>
{
    options.Enabled = builder.Configuration.GetValue<bool?>("AudioStemProcessing:Enabled") ?? true;
    options.PythonPath = builder.Configuration["AudioStemProcessing:PythonPath"] ?? builder.Configuration["PythonPath"] ?? string.Empty;
    options.YtDlpPath = builder.Configuration["AudioStemProcessing:YtDlpPath"] ?? builder.Configuration["YtDlpPath"] ?? "yt-dlp";
    options.FfmpegPath = builder.Configuration["AudioStemProcessing:FfmpegPath"] ?? builder.Configuration["FFmpegPath"] ?? "ffmpeg";
    options.YtDlpCookiesPath = builder.Configuration["AudioStemProcessing:YtDlpCookiesPath"] ?? builder.Configuration["YtDlpCookiesPath"] ?? string.Empty;
    options.WorkRoot = builder.Configuration["AudioStemProcessing:WorkRoot"]
        ?? Path.Combine(Path.GetTempPath(), "learnmore-audio-stems");
    options.ModelName = builder.Configuration["AudioStemProcessing:ModelName"] ?? "htdemucs";
    options.Device = builder.Configuration["AudioStemProcessing:Device"] ?? string.Empty;
    options.UseRemoteApi = builder.Configuration.GetValue<bool?>("AudioStemProcessing:UseRemoteApi") ?? false;
    options.RemoteApiFallbackToLocal = builder.Configuration.GetValue<bool?>("AudioStemProcessing:RemoteApiFallbackToLocal") ?? true;
    options.RemoteApiBaseUrl = builder.Configuration["AudioStemProcessing:RemoteApiBaseUrl"] ?? string.Empty;
    options.RemoteApiToken = builder.Configuration["AudioStemProcessing:RemoteApiToken"] ?? string.Empty;
    options.RemoteApiTimeoutSeconds = Math.Max(60, builder.Configuration.GetValue<int?>("AudioStemProcessing:RemoteApiTimeoutSeconds") ?? 3600);
    options.RemoteApiDownloadTimeoutSeconds = Math.Max(60, builder.Configuration.GetValue<int?>("AudioStemProcessing:RemoteApiDownloadTimeoutSeconds") ?? 900);
    options.SegmentSeconds = Math.Max(1, builder.Configuration.GetValue<double?>("AudioStemProcessing:SegmentSeconds") ?? 7.0);
    options.Jobs = Math.Max(0, builder.Configuration.GetValue<int?>("AudioStemProcessing:Jobs") ?? 0);
    options.Shifts = Math.Max(0, builder.Configuration.GetValue<int?>("AudioStemProcessing:Shifts") ?? 0);
    options.PollIntervalSeconds = Math.Max(10, builder.Configuration.GetValue<int?>("AudioStemProcessing:PollIntervalSeconds") ?? 60);
    options.MaxAttempts = Math.Max(1, builder.Configuration.GetValue<int?>("AudioStemProcessing:MaxAttempts") ?? 3);
    options.LeaseMinutes = Math.Max(5, builder.Configuration.GetValue<int?>("AudioStemProcessing:LeaseMinutes") ?? 90);
    options.RetryDelayMinutes = Math.Max(1, builder.Configuration.GetValue<int?>("AudioStemProcessing:RetryDelayMinutes") ?? 30);
    options.DownloadTimeoutSeconds = Math.Max(30, builder.Configuration.GetValue<int?>("AudioStemProcessing:DownloadTimeoutSeconds") ?? 900);
    options.SeparationTimeoutSeconds = Math.Max(60, builder.Configuration.GetValue<int?>("AudioStemProcessing:SeparationTimeoutSeconds") ?? 3600);
    options.ConversionTimeoutSeconds = Math.Max(60, builder.Configuration.GetValue<int?>("AudioStemProcessing:ConversionTimeoutSeconds") ?? 600);
    options.NormalizeLoudness = builder.Configuration.GetValue<bool?>("AudioStemProcessing:NormalizeLoudness") ?? true;
    options.TargetIntegratedLufs = builder.Configuration.GetValue<double?>("AudioStemProcessing:TargetIntegratedLufs") ?? -12.5;
    options.TargetLoudnessRange = builder.Configuration.GetValue<double?>("AudioStemProcessing:TargetLoudnessRange") ?? 11.0;
    options.TargetTruePeakDb = builder.Configuration.GetValue<double?>("AudioStemProcessing:TargetTruePeakDb") ?? -1.5;
});

// Add services to the container.
builder.Services
    .AddControllersWithViews()
    .AddRazorRuntimeCompilation();
builder.Services.AddMemoryCache();
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
});
builder.Services.Configure<BrotliCompressionProviderOptions>(options =>
{
    options.Level = CompressionLevel.Fastest;
});
builder.Services.Configure<GzipCompressionProviderOptions>(options =>
{
    options.Level = CompressionLevel.Fastest;
});

builder.Services
    .AddDataProtection()
    .PersistKeysToFileSystem(GetPersistentSessionKeyDirectory(builder))
    .SetApplicationName("LearnMore");

// ?? �ץ��G�]�w JSON �ǦC�ƿﶵ�A�T�O UTF-8 �s�X�M����䴩
builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.PropertyNamingPolicy = null;
    options.JsonSerializerOptions.Encoder = JavaScriptEncoder.Create(UnicodeRanges.All);
    options.JsonSerializerOptions.WriteIndented = false;
    options.JsonSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never;
});

// ���U HttpClient
builder.Services.AddHttpClient();

builder.Services.AddSession(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.Path = "/";
    options.IdleTimeout = TimeSpan.FromHours(8);
});


builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<PersistentLoginSessionService>();
builder.Services.AddScoped<IMobileIdentityVerifier, MobileIdentityVerifier>();
builder.Services.AddScoped<IMobileAccountStore, MobileAccountStore>();
builder.Services.AddScoped<MobileAuthorizeFilter>();
builder.Services.AddHostedService<MobileFileCleanupService>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("mobile-auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

builder.Services.AddScoped<WhisperController>();
builder.Services.AddScoped<KuroshiroController>();
builder.Services.AddScoped<CrawlerController>();
builder.Services.AddScoped<MarumaruCrawlerService>();
builder.Services.AddScoped<YtDlpAudioDownloaderService>();
builder.Services.AddScoped<OpenAiWhisperClientService>();
builder.Services.AddScoped<IOpenAiWhisperClientService>(sp => sp.GetRequiredService<OpenAiWhisperClientService>());
builder.Services.AddScoped<WhisperTranscriptionPersistenceService>();
builder.Services.AddScoped<IWhisperSongPersistenceService, WhisperSongPersistenceService>();
builder.Services.AddSingleton<IWhisperPostProcessService, WhisperPostProcessService>();
builder.Services.AddSingleton<RemoteHighAccuracyAlignmentClient>();
builder.Services.AddSingleton<IWhisperHighAccuracyInitialPassService, WhisperHighAccuracyInitialPassService>();
builder.Services.AddScoped<IKuroshiroConversionService, KuroshiroConversionService>();
builder.Services.AddScoped<IWhisperSummonPreparationService, WhisperSummonPreparationService>();
builder.Services.AddScoped<IWhisperManageQueryService, WhisperManageQueryService>();
builder.Services.AddScoped<IWhisperEditQueryService, WhisperEditQueryService>();
builder.Services.AddScoped<IWhisperEditMutationService, WhisperEditMutationService>();
builder.Services.AddScoped<IWhisperLyricsQueryService, WhisperLyricsQueryService>();
builder.Services.AddScoped<IWhisperLyricsMutationService, WhisperLyricsMutationService>();
builder.Services.AddScoped<IWhisperTranslationSourceService, WhisperTranslationSourceService>();
builder.Services.AddScoped<IYouTubeMetadataResolverService, YouTubeMetadataResolverService>();
builder.Services.AddScoped<IWhisperAudioPreprocessService, WhisperAudioPreprocessService>();
builder.Services.AddScoped<IWhisperTranscribeWorkflowService, WhisperTranscribeWorkflowService>();
builder.Services.AddScoped<IYouTubeSubtitleDownloadService, YouTubeSubtitleDownloadService>();
builder.Services.AddScoped<IAudioStemJobService, AudioStemJobService>();
builder.Services.AddScoped<DemucsAudioStemProcessor>();
builder.Services.AddScoped<RemoteApiAudioStemProcessor>();
builder.Services.AddScoped<IAudioStemProcessor>(sp =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AudioStemProcessingOptions>>().Value;
    if (options.UseRemoteApi && !string.IsNullOrWhiteSpace(options.RemoteApiBaseUrl))
    {
        return sp.GetRequiredService<RemoteApiAudioStemProcessor>();
    }

    return sp.GetRequiredService<DemucsAudioStemProcessor>();
});
builder.Services.AddScoped<YouTubeSubtitleParserService>();
builder.Services.AddScoped<LrcLibService>();
builder.Services.AddScoped<NetEaseLrcService>();
builder.Services.AddScoped<TypingTubeLyricsService>();
builder.Services.AddScoped<VocalOnsetDetectionService>();
builder.Services.AddHostedService<HighAccuracyQueueRecoveryHostedService>();
builder.Services.AddHostedService<AudioStemProcessingHostedService>();
builder.Services.AddSingleton<JapaneseRubyGeneratorService>();

builder.Services.AddHttpClient("jcinfo", client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
    client.DefaultRequestHeaders.Referrer = new Uri("https://www.jcinfo.net/zh-hans/tools/ja-roman");
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    CookieContainer = new CookieContainer()
});


var app = builder.Build();

await EnsureSongsPerformerColumnAsync(app.Configuration);

app.UseSession();

app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/api/mobile"))
    {
        await context.RequestServices.GetRequiredService<PersistentLoginSessionService>().RestoreSessionAsync(context);
        // Revokes old website sessions too after an account is deleted, including a reused email address.
        if (context.Session.GetString("Email") is string email)
        {
            await using var db = new SqlConnection(app.Configuration.GetConnectionString("DefaultConnection"));
            await db.OpenAsync(context.RequestAborted);
            using var check = new SqlCommand("SELECT COUNT(*) FROM dbo.Users WHERE Id = @Id AND Email = @Email", db);
            check.Parameters.AddWithValue("@Id", int.TryParse(context.Session.GetString("UserId"), out var id) ? id : -1);
            check.Parameters.AddWithValue("@Email", email);
            if (Convert.ToInt32(await check.ExecuteScalarAsync(context.RequestAborted)) != 1)
            {
                context.Session.Clear();
                context.RequestServices.GetRequiredService<PersistentLoginSessionService>().ClearPersistentCookie(context);
            }
        }
    }
    await next();
});

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}

app.UseResponseCompression();

app.Use(async (context, next) =>
{
    ApplySecurityHeaders(context.Response.Headers);

    if (IsBlockedPublicAsset(context.Request.Path))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    await next();
});

var staticFileContentTypes = new FileExtensionContentTypeProvider();
staticFileContentTypes.Mappings[".flac"] = "audio/flac";
staticFileContentTypes.Mappings[".m4a"] = "audio/mp4";
staticFileContentTypes.Mappings[".wav"] = "audio/wav";
staticFileContentTypes.Mappings[".ogg"] = "audio/ogg";

app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = staticFileContentTypes,
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers["Cache-Control"] = "public, max-age=2592000, immutable";
        ctx.Context.Response.Headers.Remove("Pragma");
        ctx.Context.Response.Headers.Remove("Expires");
        ApplySecurityHeaders(ctx.Context.Response.Headers);
    }
});

// 動態內容不使用快取 + Security Headers
app.Use(async (context, next) =>
{
    // Cache Control
    context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
    context.Response.Headers["Pragma"] = "no-cache";
    context.Response.Headers["Expires"] = "0";

    ApplySecurityHeaders(context.Response.Headers);

    await next();
});

app.UseRouting();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/api/mobile")) { await next(); return; }
    try { await next(); }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (Exception exception) when (!context.Response.HasStarted)
    {
        var (status, message) = exception switch
        {
            MobileAuthException auth => (auth.Status, auth.Message),
            SecurityTokenException => (401, "登入驗證失敗，請重新登入。"),
            SqlException sql when sql.Number is 2601 or 2627 => (409, "帳號或歌單資料已變更，請重新整理後再試。"),
            _ => (503, "服務暫時無法使用，請稍後再試。")
        };
        // Do not log OAuth responses or credentials, including exception messages containing JWTs.
        app.Logger.LogWarning("Mobile request failed: {ExceptionType}, status {Status}", exception.GetType().Name, status);
        context.Response.Clear();
        context.Response.StatusCode = status;
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(new { error = message });
    }
});

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();

static async Task EnsureSongsPerformerColumnAsync(IConfiguration configuration)
{
    var connectionString = configuration.GetConnectionString("DefaultConnection");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return;
    }

    const string sql = @"
IF COL_LENGTH('dbo.Songs', 'Performer') IS NULL
BEGIN
    ALTER TABLE [language].[dbo].[Songs] ADD [Performer] NVARCHAR(255) NULL;
END";

    await using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync();
    await using var command = new SqlCommand(sql, connection);
    await command.ExecuteNonQueryAsync();
}

static DirectoryInfo GetPersistentSessionKeyDirectory(WebApplicationBuilder builder)
{
    var configuredPath = builder.Configuration["PersistentSession:DataProtectionKeyPath"];
    if (!string.IsNullOrWhiteSpace(configuredPath))
    {
        return Directory.CreateDirectory(configuredPath);
    }

    var candidatePaths = new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LearnMore", "DataProtectionKeys"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LearnMore", "DataProtectionKeys"),
        Path.Combine(AppContext.BaseDirectory, "DataProtectionKeys")
    };

    foreach (var path in candidatePaths.Where(path => !string.IsNullOrWhiteSpace(path)))
    {
        try
        {
            return Directory.CreateDirectory(path);
        }
        catch (UnauthorizedAccessException)
        {
            // Try the next stable location. IIS identities may not have a loaded profile or ProgramData write permission.
        }
        catch (IOException)
        {
            // Try the next stable location if the current candidate cannot be created.
        }
    }

    throw new InvalidOperationException("No writable DataProtectionKeys directory could be created for LearnMore persistent sessions.");
}

static bool IsBlockedPublicAsset(PathString path)
{
    var value = path.Value;
    if (string.IsNullOrWhiteSpace(value))
    {
        return false;
    }

    var fileName = Path.GetFileName(value);
    if (fileName.Equals("package.json", StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("package-lock.json", StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    var extension = Path.GetExtension(value);
    return extension.Equals(".map", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".pdb", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".cshtml", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".config", StringComparison.OrdinalIgnoreCase);
}

static void ApplySecurityHeaders(IHeaderDictionary headers)
{
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "SAMEORIGIN";
    headers["X-XSS-Protection"] = "1; mode=block";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    headers["Permissions-Policy"] = "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()";
    headers["Content-Security-Policy"] =
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline' 'unsafe-eval' https://www.youtube.com https://s.ytimg.com https://cdn.jsdelivr.net https://cdnjs.cloudflare.com https://www.googletagmanager.com https://www.google-analytics.com https://accounts.google.com https://apis.google.com; " +
        "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com https://cdn.jsdelivr.net https://cdnjs.cloudflare.com; " +
        "font-src 'self' https://fonts.gstatic.com https://cdn.jsdelivr.net https://cdnjs.cloudflare.com; " +
        "img-src 'self' data: https: blob:; " +
        "frame-src 'self' https://www.youtube.com https://www.youtube-nocookie.com https://accounts.google.com https://ycspace.myvnc.com; " +
        "connect-src 'self' https://www.youtube.com https://www.google-analytics.com https://www.google.com https://*.googleapis.com https://accounts.google.com https://ycspace.myvnc.com wss://ycspace.myvnc.com; " +
        "media-src 'self' https: blob:; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self';";
}
