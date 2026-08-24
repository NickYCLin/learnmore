using System.IO;
using Xunit;

namespace LearnMore.Tests;

public class SongTitleAliasUploadViewTests
{
    [Theory]
    [InlineData("LearnMore/Views/Media/Upload.cshtml")]
    [InlineData("LearnMore/Views/Media/Summon.cshtml")]
    public void SongCreationViewsExposeOptionalChineseTitleAliasField(string relativePath)
    {
        string content = File.ReadAllText(GetRepositoryFile(relativePath));

        Assert.Contains("chineseTitleAlias", content);
        Assert.Contains("中文歌名（選填）", content);
        Assert.Contains("未填時不會呼叫 OpenAI API", content);
    }

    [Fact]
    public void UploadViewSendsChineseTitleAliasDuringTranscribeAndFinalSave()
    {
        string content = File.ReadAllText(GetRepositoryFile("LearnMore/Views/Media/Upload.cshtml"));

        Assert.Contains("ChineseTitleAlias: chineseTitleAlias", content);
        Assert.Contains("chineseTitleAlias: chineseTitleAlias", content);
    }

    [Fact]
    public void SummonViewSendsChineseTitleAliasDuringFinalSave()
    {
        string content = File.ReadAllText(GetRepositoryFile("LearnMore/Views/Media/Summon.cshtml"));

        Assert.Contains("ChineseTitleAlias: chineseTitleAlias", content);
    }

    [Fact]
    public void MediaControllerDoesNotCallOpenAiWhenChineseTitleAliasIsMissing()
    {
        string content = File.ReadAllText(GetRepositoryFile("LearnMore/Controllers/MediaController.cs"));
        int helperIndex = content.IndexOf("EnsureChineseTitleAliasAsync", StringComparison.Ordinal);
        Assert.True(helperIndex >= 0, "MediaController should keep the alias helper for the upload/summon flow.");

        string helperSource = content[helperIndex..];
        Assert.DoesNotContain("TranslateSongTitleToTraditionalChineseAsync", helperSource);
    }

    private static string GetRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate repository file: {relativePath}");
    }
}
