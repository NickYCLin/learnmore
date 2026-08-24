using LearnMore.Controllers;
using LearnMore.Controllers.API;
using LearnMore.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LearnMore.Tests;

public class WhisperSongPersistenceDelegationTests
{
    [Fact]
    public void WhisperController_ShouldNotExposeLegacyServiceWrapperMethods()
    {
        var controllerType = typeof(WhisperController);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public;

        Assert.Null(controllerType.GetMethod("DownloadYTAudioAsync", flags));
        Assert.Null(controllerType.GetMethod("DownloadYTAudioRawAsync", flags));
        Assert.Null(controllerType.GetMethod("TranscribeAudioAsync", flags));
        Assert.Null(controllerType.GetMethod("AddSongToDatabaseAsync", flags));
        Assert.Null(controllerType.GetMethod("CreateDynamicSongTableAsync", flags));
        Assert.Null(controllerType.GetMethod("ParseTranscriptionToSegmentsChineseAsync", flags));
        Assert.Null(controllerType.GetMethod("TranslateSegmentToChineseAsync", flags));
        Assert.Null(controllerType.GetMethod("BatchTranslateToChineseAsync", flags));
    }

    [Fact]
    public void MediaController_ShouldNotDependOnWhisperController()
    {
        var constructor = Assert.Single(typeof(MediaController).GetConstructors());
        var parameters = constructor.GetParameters();

        Assert.DoesNotContain(parameters, parameter => parameter.ParameterType == typeof(WhisperController));
        Assert.Null(typeof(MediaController).GetField("_whisperController", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic));
    }

    [Fact]
    public void WhisperSongPersistenceService_ShouldExposePlaceholderWorkflowMethod()
    {
        var method = typeof(IWhisperSongPersistenceService).GetMethod("CreateSongWithPlaceholdersAsync");

        Assert.NotNull(method);
    }

    [Fact]
    public void MediaController_ShouldNotContainInlineSongInitializationSql()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "LearnMore", "Controllers", "MediaController.cs"));

        Assert.DoesNotContain("INSERT INTO [language].[dbo].[Songs]", source);
        Assert.DoesNotContain("OUTPUT INSERTED.LyricID", source);
    }

    [Fact]
    public void WhisperSongPersistenceService_ShouldExposeTranslationPersistenceMethod()
    {
        var method = typeof(IWhisperSongPersistenceService).GetMethod("UpdateSongTranslationsAsync");

        Assert.NotNull(method);
    }

    [Fact]
    public void MediaController_ShouldNotContainInlineTranslationUpdateSql()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "LearnMore", "Controllers", "MediaController.cs"));

        Assert.DoesNotContain("DELETE FROM [language].[dbo].[{tableName}]", source);
        Assert.DoesNotContain("UPDATE [language].[dbo].[{tableName}] SET Chinese = @Chinese", source);
    }

    [Fact]
    public void WhisperSongPersistenceService_ShouldExposeProducerLinkMethod()
    {
        var method = typeof(IWhisperSongPersistenceService).GetMethod("AppendProducerSongAsync");

        Assert.NotNull(method);
    }

    [Fact]
    public void MediaController_ShouldNotContainInlineProducerSql()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "LearnMore", "Controllers", "MediaController.cs"));

        Assert.DoesNotContain("SELECT Producer FROM [language].[dbo].[Users] WHERE Email = @Email", source);
        Assert.DoesNotContain("UPDATE [language].[dbo].[Users] SET Producer = @UpdatedProducer WHERE Email = @Email", source);
    }

    [Fact]
    public void WhisperSongPersistenceService_ShouldExposePostProcessEnqueueMethod()
    {
        var method = typeof(IWhisperPostProcessService).GetMethod("EnqueueRubyRomanEnrichment");

        Assert.NotNull(method);
    }

    [Fact]
    public void WhisperSongPersistenceService_ShouldExposeImmediatePostProcessMethod()
    {
        var method = typeof(IWhisperPostProcessService).GetMethod("RunRubyRomanEnrichmentAsync");

        Assert.NotNull(method);
    }

    [Fact]
    public void WhisperSongPersistenceService_ShouldExposeManualSegmentInsertMethod()
    {
        var method = typeof(IWhisperSongPersistenceService).GetMethod("InsertManualSegmentsAsync");

        Assert.NotNull(method);
    }

    [Fact]
    public void MediaController_ShouldNotContainInlineRubyRomanTaskRun()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "LearnMore", "Controllers", "MediaController.cs"));

        Assert.DoesNotContain("Task.Run(async () =>", source);
        Assert.DoesNotContain("背景補 Ruby/Roman 失敗", source);
    }

    [Fact]
    public void MediaController_ShouldNotDependOnKuroshiroController()
    {
        var constructor = Assert.Single(typeof(MediaController).GetConstructors());
        var parameters = constructor.GetParameters();

        Assert.DoesNotContain(parameters, parameter => parameter.ParameterType == typeof(KuroshiroController));
        Assert.Null(typeof(MediaController).GetField("_kuroshiroController", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic));
    }

    [Fact]
    public void MediaController_ShouldNotContainInlineManualSegmentInsertSql()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "LearnMore", "Controllers", "MediaController.cs"));

        Assert.DoesNotContain("INSERT INTO {tableName} (TimeStamp, Japanese, Chinese) VALUES (@Start, @Text, @Chinese)", source);
        Assert.DoesNotContain("SELECT Producer FROM Users WHERE Email = @Email", source);
        Assert.DoesNotContain("UPDATE Users SET Producer = @UpdatedProducer WHERE Email = @Email", source);
        Assert.DoesNotContain("_kuroshiroController.ConvertAndUpdateOptimized", source);
    }

    [Fact]
    public void WhisperSongPersistenceService_ShouldExposeSummonPersistenceMethod()
    {
        var method = typeof(IWhisperSongPersistenceService).GetMethod("CreateSummonedSongAsync");

        Assert.NotNull(method);
    }

    [Fact]
    public void MediaController_ShouldNotContainInlineSummonSql()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "LearnMore", "Controllers", "MediaController.cs"));

        Assert.DoesNotContain("INSERT INTO Songs (SongUid, Title, Artist, Cover, Translator, YouTubeVideoUrl)", source);
        Assert.DoesNotContain("CREATE TABLE [{lyricsTableName}]", source);
        Assert.DoesNotContain("INSERT INTO [{lyricsTableName}] (TimeStamp, Japanese, Chinese, JapaneseRuby, Roman)", source);
    }

    [Fact]
    public void WhisperSongPersistenceService_ShouldExposeSummonPreparationMethod()
    {
        var method = typeof(IWhisperSummonPreparationService).GetMethod("PrepareLyricsAsync");

        Assert.NotNull(method);
    }

    [Fact]
    public void MediaController_ShouldNotDependOnKuroshiroConversionService()
    {
        var constructor = Assert.Single(typeof(MediaController).GetConstructors());
        var parameters = constructor.GetParameters();

        Assert.DoesNotContain(parameters, parameter => parameter.ParameterType == typeof(IKuroshiroConversionService));
        Assert.Null(typeof(MediaController).GetField("_kuroshiroConversionService", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic));
    }

    [Fact]
    public void MediaController_ShouldNotContainInlineSummonLyricPreparation()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "LearnMore", "Controllers", "MediaController.cs"));

        Assert.DoesNotContain("JapaneseRubySanitizer.NormalizeRubyHtml", source);
        Assert.DoesNotContain("ConvertSingleLineAsync(", source);
        Assert.DoesNotContain("preparedLyrics.Add(new LyricEntry", source);
    }

    [Fact]
    public void WhisperSongPersistenceService_ShouldExposeManageQueryMethod()
    {
        var method = typeof(IWhisperManageQueryService).GetMethod("GetManageViewModelAsync");

        Assert.NotNull(method);
    }

    [Fact]
    public void WhisperSongPersistenceService_ShouldExposeSongProcessingStatusMutationMethod()
    {
        var method = typeof(IWhisperSongPersistenceService).GetMethod("UpdateHighAccuracyStatusAsync");

        Assert.NotNull(method);
    }

    [Fact]
    public void MediaController_ShouldNotContainInlineManageSql()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "LearnMore", "Controllers", "MediaController.cs"));

        Assert.DoesNotContain("SELECT Producer, Collaboration FROM Users WHERE Email = @Email", source);
        Assert.DoesNotContain("SELECT Title, Artist, Cover, YouTubeVideoUrl, SongUid FROM Songs WHERE SongUid IN", source);
        Assert.DoesNotContain("private List<Songs> GetSongsByUids", source);
    }

    [Fact]
    public void WhisperSongPersistenceService_ShouldExposeEditQueryMethod()
    {
        var method = typeof(IWhisperEditQueryService).GetMethod("GetEditSongViewModelAsync");

        Assert.NotNull(method);
    }

    [Fact]
    public void MediaController_ShouldNotContainInlineEditQuerySql()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "LearnMore", "Controllers", "MediaController.cs"));

        Assert.DoesNotContain("SELECT [Producer] FROM [Users] WHERE [Email] = @Email", source);
        Assert.DoesNotContain("SELECT [Title], [Artist], [Cover], [Translator], [YouTubeVideoUrl], [SongUid]", source);
        Assert.DoesNotContain("SELECT [Email], [Collaboration]", source);
    }

    [Fact]
    public void WhisperSongPersistenceService_ShouldExposeEditMutationMethod()
    {
        var method = typeof(IWhisperEditMutationService).GetMethod("UpdateSongAndCollaboratorsAsync");

        Assert.NotNull(method);
    }

    [Fact]
    public void MediaController_ShouldNotContainInlineEditMutationSql()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "LearnMore", "Controllers", "MediaController.cs"));

        Assert.DoesNotContain("UPDATE [Songs]", source);
        Assert.DoesNotContain("SELECT Email, Collaboration FROM [Users]", source);
        Assert.DoesNotContain("SET [Collaboration] = @Collab", source);
    }

    [Fact]
    public void WhisperSongPersistenceService_ShouldExposeLyricsQueryMethod()
    {
        var method = typeof(IWhisperLyricsQueryService).GetMethod("GetEditLyricsViewModelAsync");

        Assert.NotNull(method);
    }

    [Fact]
    public void MediaController_ShouldNotContainInlineEditLyricsQuerySql()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "LearnMore", "Controllers", "MediaController.cs"));

        Assert.DoesNotContain("SELECT [Producer], [Collaboration] FROM [Users] WHERE [Email] = @Email", source);
        Assert.DoesNotContain("SELECT [Title], [YouTubeVideoUrl] FROM [Songs] WHERE [SongUid] = @SongUid", source);
        Assert.DoesNotContain("SELECT [LyricID], [TimeStamp], [Japanese], [Chinese], [Roman]", source);
    }

    [Fact]
    public void WhisperSongPersistenceService_ShouldExposeLyricsMutationMethods()
    {
        Assert.NotNull(typeof(IWhisperLyricsMutationService).GetMethod("UpdateLyricsAsync"));
        Assert.NotNull(typeof(IWhisperLyricsMutationService).GetMethod("UpdateOrderAsync"));
        Assert.NotNull(typeof(IWhisperLyricsMutationService).GetMethod("DeleteLyricAsync"));
    }

    [Fact]
    public void MediaController_ShouldNotContainInlineLyricsMutationSql()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "LearnMore", "Controllers", "MediaController.cs"));

        Assert.DoesNotContain("UPDATE [Songs_\" + request.SongUid", source);
        Assert.DoesNotContain("SELECT [LyricID], [TimeStamp], [Japanese], [Chinese], [JapaneseRuby], [Roman]", source);
        Assert.DoesNotContain("DELETE FROM [language].[dbo].[Songs_", source);
    }

    [Fact]
    public void MediaController_ShouldNotDependOnCrawlerController()
    {
        var constructor = Assert.Single(typeof(MediaController).GetConstructors());
        var parameters = constructor.GetParameters();

        Assert.DoesNotContain(parameters, parameter => parameter.ParameterType == typeof(CrawlerController));
        Assert.Null(typeof(MediaController).GetField("_crawlerController", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic));
    }

    [Fact]
    public void MediaController_ShouldNotWrapCrawlerCallsInTaskRun()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "LearnMore", "Controllers", "MediaController.cs"));

        Assert.DoesNotContain("Task.Run(() =>\n                            _marumaruCrawlerService.SearchAndFetchAsync", source);
        Assert.DoesNotContain("Task.Run(() =>\n                            _marumaruCrawlerService.SearchBahaLyricsAsync", source);
    }

    [Fact]
    public void WhisperSongPersistenceService_ShouldExposeTranslationSourceWorkflowMethods()
    {
        Assert.NotNull(typeof(IWhisperTranslationSourceService).GetMethod("TryPreAlignAsync"));
        Assert.NotNull(typeof(IWhisperTranslationSourceService).GetMethod("ResolveFinalSegmentsAsync"));
    }

    [Fact]
    public void MediaController_ShouldNotDependOnMarumaruCrawlerService()
    {
        var constructor = Assert.Single(typeof(MediaController).GetConstructors());
        var parameters = constructor.GetParameters();

        Assert.DoesNotContain(parameters, parameter => parameter.ParameterType == typeof(MarumaruCrawlerService));
        Assert.Null(typeof(MediaController).GetField("_marumaruCrawlerService", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic));
    }

    [Fact]
    public void MediaController_ShouldNotContainInlineTranslationSourceCrawlerCalls()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "LearnMore", "Controllers", "MediaController.cs"));

        Assert.DoesNotContain("_marumaruCrawlerService.SearchAndFetchAsync", source);
        Assert.DoesNotContain("_marumaruCrawlerService.SearchBahaLyricsAsync", source);
        Assert.DoesNotContain("_marumaruCrawlerService.AlignWithLrc", source);
    }

    [Fact]
    public void WhisperTranslationSourceService_ShouldCompletePreAlignedTranslationsBeforeShortCircuit()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "LearnMore", "Services", "WhisperTranslationSourceService.cs"));

        Assert.DoesNotContain("preAlignedSegments.Any(s => !string.IsNullOrWhiteSpace(s.Chinese))", source);
        Assert.Contains("FillMissingTranslationsWithGptAsync(", source);
        Assert.Contains("ClearSuspiciousDuplicateTranslations(preAlignedSegments)", source);
        Assert.Contains("HasCompleteChineseTranslations(completedPreAligned)", source);
        Assert.Contains("!string.Equals(chinese.Trim(), \"翻譯中...\", StringComparison.Ordinal)", source);
    }

    [Fact]
    public void MediaController_ShouldNotShortCircuitOnPartiallyTranslatedPreAlignedSegments()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "LearnMore", "Controllers", "MediaController.cs"));

        Assert.DoesNotContain("preAlignedSegments.Any(s => !string.IsNullOrWhiteSpace(s.Chinese))", source);
    }

    [Fact]
    public void MediaController_ShouldPreAlignMarumaruForAsrTimestampFallback()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "LearnMore", "Controllers", "MediaController.cs"));

        Assert.Contains("if (hasTitleAndArtist && !fromTypingTube)", source);
        Assert.Contains("preferMarumaruLineCount: !fromLrcLibOrNetEase", source);
        Assert.Contains("正式歌詞逐句對齊", source);
        Assert.Contains("TryDownloadAutoCaptionTimeAnchorsAsync", source);
        Assert.Contains("僅用於對齊正式歌詞", source);
    }

    [Fact]
    public void MediaController_ShouldOnlyApplyCompleteMonotonicAudioAlignment()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "LearnMore", "Controllers", "MediaController.cs"));

        Assert.Contains("TryApplyCompleteMonotonicAlignments", source);
        Assert.Contains("AreAlignmentTimestampsMonotonic", source);
        Assert.Contains("matchedCount != segments.Count", source);
        Assert.Contains("保留來源時間錨", source);
        Assert.Contains("保留同步歌詞時間戳", source);
    }

    [Fact]
    public void RemoteHighAccuracyAlignmentClient_ShouldGateLatinForcedSequenceMatches()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "LearnMore", "Services", "RemoteHighAccuracyAlignmentClient.cs"));

        Assert.Contains("IsLatinDominantLyric(line.Japanese)", source);
        Assert.Contains("IsReliableLatinAlignmentSource(source)", source);
        Assert.Contains("whisperx_lyric_forced_alignment_sequence", source);
        Assert.Contains("source.Contains(\"unverified\"", source);
    }

    [Fact]
    public void WhisperTranslationSourceService_ShouldUseMarumaruLineCountWhenRequested()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "LearnMore", "Services", "WhisperTranslationSourceService.cs"));

        Assert.Contains("bool preferMarumaruLineCount = false", source);
        Assert.Contains("AlignLyricsWithTimestamps(", source);
    }

    [Fact]
    public void WhisperTranslationSourceService_ShouldExposeStructuredResolutionResult()
    {
        Assert.NotNull(typeof(TranslationSourceResolutionResult));

        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "LearnMore", "Controllers", "MediaController.cs"));
        Assert.DoesNotContain("ReferenceEquals(finalSegments, preAlignedSegments)", source);
    }

    [Fact]
    public void MediaController_ShouldNotDependOnIConfiguration()
    {
        var constructor = Assert.Single(typeof(MediaController).GetConstructors());
        var parameters = constructor.GetParameters();

        Assert.DoesNotContain(parameters, parameter => parameter.ParameterType == typeof(IConfiguration));
        Assert.Null(typeof(MediaController).GetField("_config", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic));
    }

    [Fact]
    public void MediaController_ShouldNotContainInlineYouTubeMetadataResolution()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "LearnMore", "Controllers", "MediaController.cs"));

        Assert.DoesNotContain("_config[\"YtDlpPath\"]", source);
        Assert.DoesNotContain("--print title --print artist --print creator --print uploader --print channel", source);
        Assert.DoesNotContain("TryNormalizeYouTubeMetadata(", source);
        Assert.DoesNotContain("NormalizeYouTubeSongTitle(", source);
        Assert.DoesNotContain("NormalizeYouTubeArtist(", source);
    }

    [Fact]
    public void WhisperSongPersistenceService_ShouldExposeWhisperAudioPreprocessMethod()
    {
        var method = typeof(IWhisperAudioPreprocessService).GetMethod("TrimLeadingSilenceAsync");

        Assert.NotNull(method);
    }

    [Fact]
    public void MediaController_ShouldNotContainInlineWhisperAudioPreprocessLogic()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "LearnMore", "Controllers", "MediaController.cs"));

        Assert.DoesNotContain("silenceremove=start_periods=1:start_duration=0.3:start_threshold=-50dB", source);
        Assert.DoesNotContain("private static async Task<double> GetAudioDurationAsync", source);
        Assert.DoesNotContain("FileName = \"ffprobe\"", source);
    }
}
