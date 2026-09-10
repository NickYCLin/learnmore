using System.Security.Cryptography;
using System.Text;
using LearnMore.Controllers.API;
using LearnMore.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LearnMore.Tests;

public class MobileSessionTests
{
    private const string Verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    private static string Challenge => WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(Verifier)));

    [Fact]
    public void CodeRequiresProofAndCanOnlyBeExchangedOnce()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new MobileSessionService(cache);
        var code = service.IssueCode(new(42, "測試者"), Challenge);
        Assert.Null(service.Exchange(code, new string('x', 43)));
        var result = service.Exchange(code, Verifier);
        Assert.NotNull(result);
        Assert.Equal(42, service.Read("Bearer " + result.Value.Token)!.Id);
        Assert.Null(service.Exchange(code, Verifier));
        Assert.Null(service.Read("Bearer forged"));
        service.Revoke("Bearer " + result.Value.Token);
        Assert.Null(service.Read("Bearer " + result.Value.Token));
    }

    [Fact]
    public void ConcurrentCodeExchangeHasOneWinner()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new MobileSessionService(cache);
        var code = service.IssueCode(new(42, "測試者"), Challenge);
        var winners = 0;
        Parallel.For(0, 12, _ => { if (service.Exchange(code, Verifier) is not null) Interlocked.Increment(ref winners); });
        Assert.Equal(1, winners);
    }

    [Theory]
    [InlineData("../../Users")]
    [InlineData("abc]; DROP TABLE Users--")]
    [InlineData("")]
    public void UnsafeSongTableNamesAreRejected(string uid) => Assert.False(MobileLibraryService.ValidSongUid(uid));

    [Fact]
    public async Task PrivateActionsRejectAnonymousRequestsBeforeDatabaseAccess()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var configuration = new ConfigurationBuilder().Build();
        var controller = new MobileApiController(new(configuration), new(cache), configuration)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        Assert.IsType<UnauthorizedResult>(controller.GetGroups());
        Assert.IsType<UnauthorizedResult>(controller.CreateGroup(new("收藏")));
        Assert.IsType<UnauthorizedResult>(await controller.Songs(favorites: true));
        Assert.IsType<UnauthorizedResult>(await controller.SetMembership(1, "test-song", new(true), default));
        Assert.IsType<BadRequestResult>(await controller.Song("bad]name", default));
        Assert.IsType<BadRequestResult>(await controller.Songs(page: 0));
    }
}
