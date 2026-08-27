using LearnMore.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO;
using System.Web;
using static LearnMore.Models.NewebPayViewModel;
using Xunit;

namespace LearnMore.Tests;

public sealed class PaymentRequestSecurityTests
{
    private const string HashKey = "12345678901234567890123456789012";
    private const string HashIV = "1234567890123456";

    [Fact]
    public void PaymentRequest_ShouldUseOnlyServerConfiguredCallbackUrls()
    {
        var controller = CreateController();

        var result = Assert.IsType<JsonResult>(controller.SendToNewebPay(new SendToNewebPayIn
        {
            ChannelID = "VACC",
            Amt = "100",
            ItemDesc = "測試商品",
            Email = "buyer@example.invalid",
            CustomerURL = "https://attacker.invalid/customer",
            ClientBackURL = "https://attacker.invalid/back"
        }));
        var output = Assert.IsType<SendToNewebPayOut>(result.Value);
        var fields = HttpUtility.ParseQueryString(
            controller.DecryptAESHex(output.TradeInfo, HashKey, HashIV));

        Assert.Equal("https://shop.example/LearnMore/Payment/CallbackReturn", fields["ReturnURL"]);
        Assert.Equal("https://shop.example/LearnMore/Payment/CallbackNotify", fields["NotifyURL"]);
        Assert.Equal("https://shop.example/LearnMore/Payment/CallbackCustomer", fields["CustomerURL"]);
        Assert.Equal("https://shop.example/LearnMore", fields["ClientBackURL"]);
        Assert.DoesNotContain("attacker.invalid", fields.ToString());
    }

    [Fact]
    public void PaymentRequest_ShouldNotTrustClientSuppliedCallbackUrls()
    {
        var controller = Source("LearnMore", "Controllers", "PaymentController.cs");
        var action = Slice(
            controller,
            "public IActionResult SendToNewebPay",
            "public async Task<IActionResult> CallbackReturn");

        Assert.DoesNotContain("inModel.CustomerURL", action);
        Assert.DoesNotContain("inModel.ClientBackURL", action);
        Assert.Contains("_configuration[\"ClientBackURL\"]", action);
        Assert.Contains("new Uri(returnUri, \"./CallbackCustomer\")", action);
        Assert.DoesNotContain("Request.Host", action);
        Assert.DoesNotContain("Request.Scheme", action);
        Assert.Contains("HttpUtility.UrlEncode", action);
        Assert.Contains("amount <= 0", action);
        Assert.Contains("ChannelID is not (\"CREDIT\" or \"VACC\")", action);
        Assert.Contains("returnUri == notifyUri", action);
    }

    [Fact]
    public void CreateOrder_ShouldNotExposeCallbackFieldsToTheBrowser()
    {
        var controller = Source("LearnMore", "Controllers", "UserController.cs");
        var action = Slice(
            controller,
            "public IActionResult CreateOrder()",
            "#endregion");

        var view = Source("LearnMore", "Views", "User", "CreateOrder.cshtml");

        Assert.DoesNotContain("Request.Path", action);
        Assert.DoesNotContain("CustomerURL", view);
        Assert.DoesNotContain("ClientBackURL", view);
    }

    private static string Source(params string[] pathParts)
    {
        return File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            Path.Combine(pathParts)));
    }

    private static PaymentController CreateController()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HashKey"] = HashKey,
                ["HashIV"] = HashIV,
                ["MerchantID"] = "TESTMERCHANT",
                ["ReturnURL"] = "https://shop.example/LearnMore/Payment/CallbackReturn",
                ["NotifyURL"] = "https://shop.example/LearnMore/Payment/CallbackNotify",
                ["ClientBackURL"] = "https://shop.example/LearnMore"
            })
            .Build();

        return new PaymentController(configuration, NullLogger<PaymentController>.Instance);
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Start marker not found: {startMarker}");

        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"End marker not found after start: {endMarker}");

        return source[start..end];
    }
}
