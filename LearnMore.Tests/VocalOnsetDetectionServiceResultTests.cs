using System.Diagnostics;
using System.Net;
using LearnMore.Options;
using LearnMore.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace LearnMore.Tests;

public class VocalOnsetDetectionServiceResultTests
{
    [Fact]
    public void BuildInitialSegmentsFromWordTimings_ShouldSplitOnLongPausesAndDropShortNoise()
    {
        var segments = VocalOnsetDetectionService.BuildInitialSegmentsFromWordTimings(new[]
        {
            new VocalOnsetDetectionService.WhisperWordTiming("[音楽]", 0.1, 0.3),
            new VocalOnsetDetectionService.WhisperWordTiming("頑張っ", 1.0, 1.3),
            new VocalOnsetDetectionService.WhisperWordTiming("て", 1.3, 1.5),
            new VocalOnsetDetectionService.WhisperWordTiming("。", 1.5, 1.6),
            new VocalOnsetDetectionService.WhisperWordTiming("行け", 3.4, 3.7),
            new VocalOnsetDetectionService.WhisperWordTiming("!", 3.7, 3.8),
            new VocalOnsetDetectionService.WhisperWordTiming("あ", 5.6, 5.7)
        });

        Assert.Collection(
            segments,
            first =>
            {
                Assert.Equal(1.0, first.TimeStamp, 3);
                Assert.Equal("頑張って。", first.Japanese);
            },
            second =>
            {
                Assert.Equal(3.4, second.TimeStamp, 3);
                Assert.Equal("行け!", second.Japanese);
            });
    }

    [Fact]
    public void BuildInitialSegmentsFromWordTimings_ShouldTrimBoundaryVocalizationNoiseAndDropLowDiversitySegments()
    {
        var segments = VocalOnsetDetectionService.BuildInitialSegmentsFromWordTimings(new[]
        {
            new VocalOnsetDetectionService.WhisperWordTiming("んんんんんんんんんんんん", 1.0, 2.5),
            new VocalOnsetDetectionService.WhisperWordTiming("君は", 2.5, 3.1),
            new VocalOnsetDetectionService.WhisperWordTiming("oh", 5.0, 5.4),
            new VocalOnsetDetectionService.WhisperWordTiming("oh", 5.4, 5.8),
            new VocalOnsetDetectionService.WhisperWordTiming("oh", 5.8, 6.2),
            new VocalOnsetDetectionService.WhisperWordTiming("oh", 6.2, 6.6),
            new VocalOnsetDetectionService.WhisperWordTiming("行こう", 9.0, 9.4),
            new VocalOnsetDetectionService.WhisperWordTiming("!", 9.4, 9.5)
        });

        Assert.Collection(
            segments,
            first =>
            {
                Assert.Equal(2.5, first.TimeStamp, 3);
                Assert.Equal("君は", first.Japanese);
            },
            second =>
            {
                Assert.Equal(9.0, second.TimeStamp, 3);
                Assert.Equal("行こう!", second.Japanese);
            });
    }

    [Fact]
    public void BuildInitialSegmentsFromWordTimings_ShouldDropUnresolvableMixedNoiseTokenSegment()
    {
        var segments = VocalOnsetDetectionService.BuildInitialSegmentsFromWordTimings(new[]
        {
            new VocalOnsetDetectionService.WhisperWordTiming("んんんんんんんんんんんん君は頑張る", 1.0, 3.1),
            new VocalOnsetDetectionService.WhisperWordTiming("行こう", 9.0, 9.4),
            new VocalOnsetDetectionService.WhisperWordTiming("!", 9.4, 9.5)
        });

        Assert.Collection(
            segments,
            only =>
            {
                Assert.Equal(9.0, only.TimeStamp, 3);
                Assert.Equal("行こう!", only.Japanese);
            });
    }

    [Fact]
    public void BuildInitialSegmentsFromWordTimings_ShouldCollapseTinyInterjectionOnlyFallbackToEmpty()
    {
        var segments = VocalOnsetDetectionService.BuildInitialSegmentsFromWordTimings(new[]
        {
            new VocalOnsetDetectionService.WhisperWordTiming("oh", 46.188, 46.3),
            new VocalOnsetDetectionService.WhisperWordTiming("あ", 89.108, 89.2),
            new VocalOnsetDetectionService.WhisperWordTiming("あ", 89.2, 89.3),
            new VocalOnsetDetectionService.WhisperWordTiming("あぶんぶんぶん", 107.928, 108.4)
        });

        Assert.Empty(segments);
    }

    [Fact]
    public async Task TranscribeInitialSegmentsAsync_ShouldUseOpenAiWordFallbackWhenLocalScriptIsMissing()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenAI:ApiKey"] = "test-key"
            })
            .Build();
        var service = CreateService(config, new StubHttpClientFactory(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "words": [
                        { "word": "頑張っ", "start": 1.0, "end": 1.3 },
                        { "word": "て", "start": 1.3, "end": 1.5 },
                        { "word": "。", "start": 1.5, "end": 1.6 },
                        { "word": "行け", "start": 3.4, "end": 3.7 }
                      ]
                    }
                    """)
            }));
        var tempPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempPath, "stub");
            EnsureScriptMissing();

            var segments = await service.TranscribeInitialSegmentsAsync(tempPath);

            Assert.Collection(
                segments,
                first =>
                {
                    Assert.Equal(1.0, first.TimeStamp, 3);
                    Assert.Equal("頑張って。", first.Japanese);
                },
                second =>
                {
                    Assert.Equal(3.4, second.TimeStamp, 3);
                    Assert.Equal("行け", second.Japanese);
                });
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task AlignLyricsToAudioAsync_ShouldReportMissingAudioFileReason()
    {
        var service = CreateService();

        var result = await service.AlignLyricsToAudioAsync(
            "/tmp/does-not-exist.mp3",
            new[] { new VocalOnsetDetectionService.LyricTimingSeed("少年よ神話になれ", 8.89) });

        Assert.False(result.IsSuccess);
        Assert.Equal("audio_file_missing", result.FailureReason);
        Assert.Empty(result.Alignments);
    }

    [Fact]
    public async Task AlignLyricsToAudioAsync_ShouldReportEmptyLyricSeedsReason()
    {
        var service = CreateService();
        var tempPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempPath, "stub");

            var result = await service.AlignLyricsToAudioAsync(tempPath, Array.Empty<VocalOnsetDetectionService.LyricTimingSeed>());

            Assert.False(result.IsSuccess);
            Assert.Equal("lyric_seeds_empty", result.FailureReason);
            Assert.Empty(result.Alignments);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task AlignLyricsToAudioAsync_ShouldReportLocalScriptMissingReasonBeforeOpenAiFallback()
    {
        var service = CreateService();
        var tempPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempPath, "stub");
            EnsureScriptMissing();

            var result = await service.AlignLyricsToAudioAsync(
                tempPath,
                new[] { new VocalOnsetDetectionService.LyricTimingSeed("少年よ神話になれ", 8.89) });

            Assert.False(result.IsSuccess);
            Assert.Equal("local_faster_whisper_script_missing", result.FailureReason);
            Assert.NotNull(result.FailureDetail);
            Assert.Contains("faster_whisper_words.py", result.FailureDetail);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task AlignLyricsToAudioAsync_ShouldReportLocalFailureWithoutOpenAiFallback()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenAI:ApiKey"] = "test-key"
            })
            .Build();
        var service = CreateService(config, new StubHttpClientFactory(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("openai unauthorized")
            }));
        var tempPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempPath, "stub");
            EnsureScriptMissing();

            var result = await service.AlignLyricsToAudioAsync(
                tempPath,
                new[] { new VocalOnsetDetectionService.LyricTimingSeed("少年よ神話になれ", 8.89) });

            Assert.False(result.IsSuccess);
            Assert.Equal("local_faster_whisper_script_missing", result.FailureReason);
            Assert.NotNull(result.FailureDetail);
            Assert.Contains("faster_whisper_words.py", result.FailureDetail);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void ResolvePythonExecutablePath_ShouldPreferConfiguredPath()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PythonPath"] = @"C:\Python313\python.exe"
            })
            .Build();

        var path = VocalOnsetDetectionService.ResolvePythonExecutablePath(config);

        Assert.Equal(@"C:\Python313\python.exe", path);
    }

    [Fact]
    public void ApplyFasterWhisperEnvironment_ShouldPopulateConfiguredCacheVariables()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HuggingFaceCacheRoot"] = @"D:\Data\huggingface",
                ["LEARNMORE_FASTER_WHISPER_MODEL"] = "small",
                ["LEARNMORE_FASTER_WHISPER_DEVICE"] = "cpu",
                ["LEARNMORE_FASTER_WHISPER_COMPUTE"] = "int8"
            })
            .Build();
        var startInfo = new ProcessStartInfo();

        VocalOnsetDetectionService.ApplyFasterWhisperEnvironment(startInfo, config);

        Assert.Equal(@"D:\Data\huggingface", startInfo.Environment["HF_HOME"]);
        Assert.Equal(Path.Combine(@"D:\Data\huggingface", "transformers"), startInfo.Environment["TRANSFORMERS_CACHE"]);
        Assert.Equal(Path.Combine(@"D:\Data\huggingface", "hub"), startInfo.Environment["HUGGINGFACE_HUB_CACHE"]);
        Assert.Equal("small", startInfo.Environment["LEARNMORE_FASTER_WHISPER_MODEL"]);
        Assert.Equal("cpu", startInfo.Environment["LEARNMORE_FASTER_WHISPER_DEVICE"]);
        Assert.Equal("int8", startInfo.Environment["LEARNMORE_FASTER_WHISPER_COMPUTE"]);
    }

    [Fact]
    public void ApplyFasterWhisperEnvironment_ShouldPreferExplicitModelOverride()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LEARNMORE_FASTER_WHISPER_MODEL"] = "tiny"
            })
            .Build();
        var startInfo = new ProcessStartInfo();

        VocalOnsetDetectionService.ApplyFasterWhisperEnvironment(startInfo, new VocalOnsetDetectionOptions(), config, "small");

        Assert.Equal("small", startInfo.Environment["LEARNMORE_FASTER_WHISPER_MODEL"]);
    }

    [Fact]
    public void Constructor_ShouldUseTypedOptionsForToolingAndSecondOpinionSettings()
    {
        var service = CreateService(
            configuration: new ConfigurationBuilder().Build(),
            options: Microsoft.Extensions.Options.Options.Create(new VocalOnsetDetectionOptions
            {
                PythonPath = @"C:\Python313\python.exe",
                HuggingFaceCacheRoot = @"D:\Data\huggingface",
                FfmpegPath = @"D:\Tools\ffmpeg\bin\ffmpeg.exe",
                OpenAiApiKey = "api-from-options",
                OpenAiMyApiKey = "my-api-from-options",
                SecondaryAlignmentModel = "small",
                SecondaryAlignmentPythonPath = @"C:\Python313\python.exe"
            }));

        var optionsValue = ReadPrivateField<VocalOnsetDetectionOptions>(service, "_options");
        Assert.Equal(@"C:\Python313\python.exe", optionsValue.PythonPath);
        Assert.Equal("small", optionsValue.SecondaryAlignmentModel);
        Assert.Equal("my-api-from-options", optionsValue.OpenAiMyApiKey);
    }

    private static VocalOnsetDetectionService CreateService(IConfiguration? configuration = null, IHttpClientFactory? httpClientFactory = null, IOptions<VocalOnsetDetectionOptions>? options = null)
    {
        var env = new FakeEnv();
        var ruby = new JapaneseRubyGeneratorService(env);
        return new VocalOnsetDetectionService(
            configuration ?? new ConfigurationBuilder().Build(),
            httpClientFactory ?? new DummyFactory(),
            options ?? Microsoft.Extensions.Options.Options.Create(new VocalOnsetDetectionOptions()),
            ruby,
            new DummyLogger());
    }

    private static T ReadPrivateField<T>(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return Assert.IsType<T>(field?.GetValue(instance));
    }

    private static void EnsureScriptMissing()
    {
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "Scripts", "faster_whisper_words.py");
        if (File.Exists(scriptPath))
            File.Delete(scriptPath);
    }

    private sealed class FakeEnv : IWebHostEnvironment
    {
        public FakeEnv()
        {
            ContentRootPath = ResolveContentRootPath();
            WebRootPath = Path.Combine(ContentRootPath, "wwwroot");
            ContentRootFileProvider = new PhysicalFileProvider(ContentRootPath);
            WebRootFileProvider = new PhysicalFileProvider(WebRootPath);
            ApplicationName = "LearnMore.Tests";
            EnvironmentName = "Development";
        }

        private static string ResolveContentRootPath()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                var candidate = Path.Combine(directory.FullName, "LearnMore");
                if (File.Exists(Path.Combine(candidate, "LearnMore.csproj")))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate LearnMore project root.");
        }

        public string ApplicationName { get; set; }
        public IFileProvider WebRootFileProvider { get; set; }
        public string WebRootPath { get; set; }
        public string EnvironmentName { get; set; }
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
    }

    private sealed class DummyFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name = "") => new();
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name = "") => new(new StubHttpMessageHandler(_handler));
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }

    private sealed class DummyLogger : ILogger<VocalOnsetDetectionService>
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
