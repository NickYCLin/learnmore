using LearnMore.Controllers;
using LearnMore.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace LearnMore.Tests;

public sealed class PaymentCallbackSecurityTests
{
    private const string HashKey = "12345678901234567890123456789012";
    private const string HashIV = "1234567890123456";
    private const string MerchantId = "TESTMERCHANT";

    [Fact]
    public async Task CallbackReturn_ShouldRejectMalformedTradeInfoBeforeDecrypting()
    {
        var controller = CreateController();
        SetCallbackForm(controller, "not-hex", "invalid-sha");

        var result = await controller.CallbackReturn();

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task CallbackReturn_ShouldExposeOnlyVerifiedDecryptedFieldsToEncodedView()
    {
        var controller = CreateController();
        var tradeInfo = controller.EncryptAESHex(
            "Status=SUCCESS&MerchantID=TESTMERCHANT&MerchantOrderNo=ORDER001&Amt=100&Message=%3Cscript%3Ealert%281%29%3C%2Fscript%3E",
            HashKey,
            HashIV);
        var tradeSha = controller.EncryptSHA256($"HashKey={HashKey}&{tradeInfo}&HashIV={HashIV}");
        SetCallbackForm(controller, tradeInfo, tradeSha);

        var result = Assert.IsType<ViewResult>(await controller.CallbackReturn());
        var model = Assert.IsType<PaymentCallbackViewModel>(result.Model);

        Assert.Equal("ORDER001", model.GetValue("MerchantOrderNo"));
        Assert.Equal("<script>alert(1)</script>", model.GetValue("Message"));

        var returnView = Source("LearnMore", "Views", "Payment", "CallbackReturn.cshtml");
        var customerView = Source("LearnMore", "Views", "Payment", "CallbackCustomer.cshtml");
        Assert.DoesNotContain("Html.Raw", returnView);
        Assert.DoesNotContain("Html.Raw", customerView);
        Assert.Contains("@field.Value", returnView);
        Assert.Contains("@field.Value", customerView);
    }

    [Fact]
    public async Task CallbackNotify_ShouldAcknowledgeOnlyVerifiedNotifications()
    {
        var controller = CreateController();
        var tradeInfo = controller.EncryptAESHex(
            "Status=SUCCESS&MerchantID=TESTMERCHANT&MerchantOrderNo=ORDER002&Amt=100",
            HashKey,
            HashIV);
        var tradeSha = controller.EncryptSHA256($"HashKey={HashKey}&{tradeInfo}&HashIV={HashIV}");
        SetCallbackForm(controller, tradeInfo, tradeSha);

        var result = Assert.IsType<ContentResult>(await controller.CallbackNotify());

        Assert.Equal("OK", result.Content);
        Assert.StartsWith("text/plain", result.ContentType, StringComparison.Ordinal);
        Assert.True(result.StatusCode is null or StatusCodes.Status200OK);
    }

    [Fact]
    public void PaymentController_ShouldValidateTradeShaBeforeDecrypting()
    {
        var source = Source("LearnMore", "Controllers", "PaymentController.cs");
        var validation = source.IndexOf("IsValidTradeSha(tradeInfo, tradeSha", StringComparison.Ordinal);
        var decryption = source.IndexOf("DecryptAESHex(tradeInfo, hashKey, hashIV)", StringComparison.Ordinal);

        Assert.True(validation >= 0);
        Assert.True(decryption > validation, "TradeSha must be verified before decrypting callback data.");
        Assert.DoesNotContain("ViewData[\"ReceiveObj\"]", source);
    }

    private static PaymentController CreateController()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HashKey"] = HashKey,
                ["HashIV"] = HashIV,
                ["MerchantID"] = MerchantId
            })
            .Build();

        return new PaymentController(configuration, NullLogger<PaymentController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
    }

    private static void SetCallbackForm(
        PaymentController controller,
        string tradeInfo,
        string tradeSha)
    {
        var fields = new Dictionary<string, StringValues>
        {
            ["MerchantID"] = MerchantId,
            ["TradeInfo"] = tradeInfo,
            ["TradeSha"] = tradeSha
        };
        var form = new FormCollection(fields);
        controller.HttpContext.Request.ContentType = "application/x-www-form-urlencoded";
        controller.HttpContext.Features.Set<IFormFeature>(new FormFeature(controller.HttpContext.Request)
        {
            Form = form
        });
    }

    private static string Source(params string[] pathParts)
    {
        return File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            Path.Combine(pathParts)));
    }
}
