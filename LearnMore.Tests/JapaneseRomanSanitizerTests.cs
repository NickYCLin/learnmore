using LearnMore.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace LearnMore.Tests;

public class JapaneseRomanSanitizerTests
{
    private readonly JapaneseRubyGeneratorService _rubyGenerator = new(new FakeEnv());

    [Theory]
    [InlineData("残酷な天使のテーゼ", "zankoku na tenshi no tēze", "zankoku na tenshi no tēze")]
    [InlineData("君の知らない物語", "kimi no shira nai monogatari", "kimi no shiranai monogatari")]
    [InlineData("世界中を驚かせてしまう夜になる", "sekaijū o odoroka se te shimau yoru ni naru", "sekaijū o odorokasete shimau yoru ni naru")]
    [InlineData("高嶺の花子さん", "takane no hanako san", "takane no hanako san")]
    [InlineData("抱きしめていたい", "dakishimeteitai", "dakishimete itai")]
    [InlineData("笑っていたい", "waratte i tai", "waratte itai")]
    [InlineData("信じている", "shinjiteiru", "shinjite iru")]
    [InlineData("追いかけていく", "oikaketeiku", "oikakete iku")]
    [InlineData("忘れてしまいたい", "wasureteshimaitai", "wasurete shimaitai")]
    [InlineData("消えてしまいそう", "kieteshimaisō", "kiete shimaisō")]
    [InlineData("泣かないでいて", "nakanaideite", "nakanaide ite")]
    [InlineData("見つけてくれる", "mitsuketekureru", "mitsukete kureru")]
    [InlineData("届けてほしい", "todoketehoshii", "todokete hoshii")]
    [InlineData("伝えてくれた", "tsutaetekureta", "tsutaete kureta")]
    [InlineData("愛してしまった", "aishiteshimatta", "aishite shimatta")]
    [InlineData("愛してしまえば", "aishiteshimaeba", "aishite shimaeba")]
    [InlineData("叶えてほしくて", "kanaetehoshikute", "kanaete hoshikute")]
    [InlineData("生きていける", "ikiteikeru", "ikite ikeru")]
    [InlineData("消えてほしいわけじゃない", "kiete hoshiiwakejanai", "kiete hoshii wake janai")]
    [InlineData("夢じゃない", "yumejanai", "yume janai")]
    [InlineData("好きじゃない", "sukijanai", "suki janai")]
    [InlineData("泣くわけじゃない", "nakuwakejanai", "naku wake janai")]
    [InlineData("好きなわけじゃない", "suki na wakejanai", "suki na wake janai")]
    [InlineData("そんなわけない", "sonnawakenai", "sonna wake nai")]
    [InlineData("君しか見えない", "kimishika mienai", "kimi shika mienai")]
    [InlineData("これしかない", "koreshikanai", "kore shika nai")]
    [InlineData("今でも好きだよ", "imademo sukida yo", "ima demo sukida yo")]
    [InlineData("少しでも近づきたい", "sukoshidemo chikazukitai", "sukoshi demo chikazukitai")]
    [InlineData("それほどでもない", "sorehodode mo nai", "sore hodo de mo nai")]
    [InlineData("それほど遠くない", "sorehodo tōkunai", "sore hodo tōkunai")]
    [InlineData("どれほど君を想っても", "dorehodo kimi o omotte mo", "dore hodo kimi o omotte mo")]
    [InlineData("泣きそう", "nakisō", "naki sō")]
    [InlineData("泣きそうだった", "nakisōdatta", "naki sō datta")]
    [InlineData("泣きそうです", "nakisōdesu", "naki sō desu")]
    [InlineData("消えそうでした", "kiesō deshita", "kie sō deshita")]
    [InlineData("本物らしい", "hommonorashii", "honmono rashii")]
    [InlineData("映画みたい", "eigamitai", "eiga mitai")]
    [InlineData("好きみたい", "sukimitai", "suki mitai")]
    [InlineData("壊れやすい", "kowareyasui", "koware yasui")]
    [InlineData("分かりにくい", "wakarinikui", "wakari nikui")]
    [InlineData("会いたがり屋", "ai ta gari ya", "aitagariya")]
    [InlineData("知りたがり屋", "shiri ta gari ya", "shiritagariya")]
    [InlineData("会いたがり症", "ai ta gari shō", "aitagarishō")]
    [InlineData("知りたがり症", "shiri ta gari shō", "shiritagarishō")]
    [InlineData("会いたがり性", "ai ta gari sei", "aitagarisei")]
    [InlineData("知りたがり性", "shiri ta gari sei", "shiritagarisei")]
    [InlineData("怖がり屋", "kowagari ya", "kowagariya")]
    [InlineData("怖がり症", "kowagari shō", "kowagarishō")]
    [InlineData("怖がり性", "kowagari sei", "kowagarisei")]
    [InlineData("怖がりさん", "kowagari sa n", "kowagari san")]
    [InlineData("寂しがりや", "sabishi gari ya", "sabishigariya")]
    [InlineData("知りたがりや", "shiri ta gari ya", "shiritagariya")]
    [InlineData("甘えたがりや", "amae ta ga riya", "amaetagariya")]
    [InlineData("泣きたがりや", "naki ta gari ya", "nakitagariya")]
    [InlineData("子供っぽさ", "kodomoppo sa", "kodomopposa")]
    [InlineData("忘れっぽさ", "wasureppo sa", "wasurepposa")]
    [InlineData("怒りっぽさ", "okorippo sa", "okoripposa")]
    [InlineData("忘れがちだ", "wasuregachida", "wasuregachi da")]
    [InlineData("忘れがちで", "wasuregachide", "wasuregachi de")]
    [InlineData("泣きがちで", "nakigachide", "nakigachi de")]
    [InlineData("遅れ気味だ", "okure gimida", "okure gimi da")]
    [InlineData("遅れ気味で", "okure gimide", "okure gimi de")]
    [InlineData("風邪気味だ", "kaze gimida", "kaze gimi da")]
    [InlineData("疲れ気味で", "tsukare gimide", "tsukare gimi de")]
    [InlineData("忘れすぎて", "wasuresugite", "wasuresugi te")]
    [InlineData("泣きすぎて", "nakisugite", "nakisugi te")]
    [InlineData("食べすぎて", "tabesugite", "tabesugi te")]
    [InlineData("考えすぎて", "kangaesugite", "kangaesugi te")]
    [InlineData("忘れすぎだ", "wasuresugida", "wasuresugi da")]
    [InlineData("働きすぎで", "hatarakisugide", "hatarakisugi de")]
    [InlineData("男っぽげ", "otokoppoge", "otokoppo ge")]
    [InlineData("子供っぽげ", "kodomoppoge", "kodomoppo ge")]
    [InlineData("大人っぽげ", "otonappoge", "otonappo ge")]
    [InlineData("色っぽげ", "iroppoge", "iroppo ge")]
    [InlineData("子供っぽげで", "kodomoppogede", "kodomoppo ge de")]
    [InlineData("大人気ない", "daininkinai", "daininki nai")]
    [InlineData("大人げない", "otonagenai", "otonage nai")]
    [InlineData("無邪気ない", "mujakinai", "mujaki nai")]
    [InlineData("人気ない", "ninkinai", "ninki nai")]
    [InlineData("色気ない", "irokenai", "iroke nai")]
    [InlineData("男気ない", "otokokenai", "otokoke nai")]
    [InlineData("子供らしさ", "kodomorashi sa", "kodomorashisa")]
    [InlineData("大人らしさ", "otonarashi sa", "otonarashisa")]
    [InlineData("男らしさ", "otokorashi sa", "otokorashisa")]
    [InlineData("女らしさ", "onnarashi sa", "onnarashisa")]
    [InlineData("寂しげだ", "sabishigeda", "sabishige da")]
    [InlineData("寂しげで", "sabishigede", "sabishige de")]
    [InlineData("悲しげだ", "kanashigeda", "kanashige da")]
    [InlineData("眠たげだ", "nemutageda", "nemutage da")]
    [InlineData("得意げで", "tokuigede", "tokuige de")]
    [InlineData("弱気だ", "yowakida", "yowaki da")]
    [InlineData("弱気で", "yowakide", "yowaki de")]
    [InlineData("強気だ", "tsuyokida", "tsuyoki da")]
    [InlineData("大人気だ", "daininkida", "daininki da")]
    [InlineData("大人気で", "daininkide", "daininki de")]
    [InlineData("行きました", "iki mashita", "ikimashita")]
    [InlineData("行きました", "iki mashi ta", "ikimashita")]
    [InlineData("見ていました", "mite i mashita", "mite imashita")]
    [InlineData("見ていました", "mi te i mashi ta", "mite imashita")]
    [InlineData("していませんでした", "shite imasen deshita", "shite imasen deshita")]
    [InlineData("していませんでした", "shi te i mase n deshi ta", "shite imasen deshita")]
    [InlineData("好きでした", "sukideshita", "suki deshita")]
    [InlineData("好きでした", "suki deshi ta", "suki deshita")]
    [InlineData("君でした", "kimideshita", "kimi deshita")]
    [InlineData("君でした", "kimi deshi ta", "kimi deshita")]
    [InlineData("好きだった", "sukidatsuta", "sukidatta")]
    [InlineData("好きだった", "suki datsu ta", "sukidatta")]
    [InlineData("会いたかった", "aitakatsuta", "aitakatta")]
    [InlineData("会いたかった", "ai takatsu ta", "aitakatta")]
    [InlineData("会いたくなかった", "aitakunakatsuta", "aitakunakatta")]
    [InlineData("会いたくなかった", "ai taku nakatsu ta", "aitakunakatta")]
    [InlineData("行けなかった", "ikenakatsuta", "ikenakatta")]
    [InlineData("忘れられなかった", "wasurerarenakatsuta", "wasurerarenakatta")]
    [InlineData("忘れられなかった", "wasure rare nakatsu ta", "wasurerarenakatta")]
    [InlineData("泣いていたかった", "naite itakatsuta", "naite itakatta")]
    [InlineData("泣いていたかった", "nai te i takatsu ta", "naite itakatta")]
    [InlineData("届けばいい", "todokebaii", "todokeba ii")]
    [InlineData("待てばいい", "matebaii", "mateba ii")]
    [InlineData("行けたなら", "iketanara", "iketa nara")]
    [InlineData("ねぇ ｱｲｯ さあいけ!", "nee ｱｲｯsāike !", "nē ai sāike!")]
    [InlineData("(ｱｲｯ)がんばって", "( ｱｲｯ )ganbatte", "(ai) ganbatte")]
    [InlineData("1人でむせて", "1 nindemusete", "hitori de musete")]
    [InlineData("2人で飲もうよ", "2 ninde nomou yo", "futari de nomou yo")]
    [InlineData("2番出口から", "2 ban deguchi kara", "niban deguchi kara")]
    [InlineData("8時半のチャイム", "8 jihan no chaimu", "hachijihan no chaimu")]
    [InlineData("12月24日", "12 tsuki 24 nichi", "jūnigatsu nijūyokka")]
    [InlineData("あと5分しかない", "ato 5 fun shika nai", "ato gofun shika nai")]
    [InlineData("１本じゃ掴めない", "1 hon ja tsukamenai", "ippon ja tsukamenai")]
    [InlineData("100年先も", "100 nen saki mo", "hyakunen saki mo")]
    [InlineData("輝く1ページ", "kagayaku 1 pēji", "kagayaku ichipēji")]
    [InlineData("純度100パーの想い", "jundo 100 pā no omoi", "jundo hyakupā no omoi")]
    [InlineData("一日86400秒", "ichi nichi 86400byō", "ichi nichi hachimanrokusenyonhyakubyō")]
    public void NormalizeWithContext_ShouldImproveWordBoundaries(string japanese, string rawRoman, string expected)
    {
        var actual = JapaneseRomanSanitizer.NormalizeWithContext(japanese, rawRoman, _rubyGenerator);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void RubyGenerator_ShouldUseOverrideForKidoku()
    {
        var ruby = _rubyGenerator.ConvertToRubyHtml("あえて既読スルー");
        var roman = JapaneseRomanSanitizer.NormalizeWithContext("あえて既読スルー", "aete kidoku surū", _rubyGenerator);

        Assert.Contains("<ruby>既読<rt>きどく</rt></ruby>", ruby);
        Assert.Equal("aete kidoku surū", roman);
    }

    [Fact]
    public void RubyGenerator_ShouldUseOverrideForNumericHitori()
    {
        var ruby = _rubyGenerator.ConvertToRubyHtml("1人でむせて");
        var roman = JapaneseRomanSanitizer.NormalizeWithContext("1人でむせて", "1 nindemusete", _rubyGenerator);

        Assert.Contains("<ruby>1人<rt>ひとり</rt></ruby>", ruby);
        Assert.Equal("hitori de musete", roman);
    }

    [Theory]
    [InlineData("2番出口から", "<ruby>2番<rt>にばん</rt></ruby>")]
    [InlineData("8時半のチャイム", "<ruby>8時半<rt>はちじはん</rt></ruby>")]
    [InlineData("12月24日", "<ruby>12月<rt>じゅうにがつ</rt></ruby><ruby>24日<rt>にじゅうよっか</rt></ruby>")]
    [InlineData("１本じゃ掴めない", "<ruby>１本<rt>いっぽん</rt></ruby>")]
    [InlineData("一日86400秒", "<ruby>86400秒<rt>はちまんろくせんよんひゃくびょう</rt></ruby>")]
    public void RubyGenerator_ShouldAnnotateNumericCounters(string japanese, string expectedFragment)
    {
        var ruby = _rubyGenerator.ConvertToRubyHtml(japanese);

        Assert.Contains(expectedFragment, ruby);
    }

    [Theory]
    [InlineData("La-la-la-la, light is dawning", "La-la-la-la, light is dawning")]
    [InlineData("その腕の中から fly out", "から fly out")]
    [InlineData("決めた瞬間 I had to learn to fall", " I had to learn to fall")]
    public void RubyGenerator_ShouldPreserveEnglishSpacing(string japanese, string expectedFragment)
    {
        var ruby = _rubyGenerator.ConvertToRubyHtml(japanese);

        Assert.Contains(expectedFragment, ruby);
        Assert.DoesNotContain("lightisdawning", ruby);
        Assert.DoesNotContain("flyout", ruby);
        Assert.DoesNotContain("Ihadtolearntofall", ruby);
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
}
