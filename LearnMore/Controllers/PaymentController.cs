using Microsoft.AspNetCore.Mvc;
using LearnMore.Models;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using static LearnMore.Models.NewebPayViewModel;

namespace LearnMore.Controllers
{
    public class PaymentController : Controller
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<PaymentController> _logger;

        public PaymentController(
            IConfiguration configuration,
            ILogger<PaymentController> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        #region 捐贈頁面
        public IActionResult Donate()
        {
            return View();
        }
        #endregion

        #region 藍新金流
        /// <summary>
        /// 傳送訂單至藍新金流
        /// </summary>
        /// <param name="inModel"></param>
        /// <returns></returns>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult SendToNewebPay(SendToNewebPayIn? inModel)
        {
            if (inModel is null)
                return BadRequest("付款資料不可為空");

            SendToNewebPayOut outModel = new SendToNewebPayOut();
            string merchantId = _configuration["MerchantID"] ?? string.Empty;
            string returnUrl = _configuration["ReturnURL"] ?? string.Empty;
            string notifyUrl = _configuration["NotifyURL"] ?? string.Empty;
            string clientBackUrl = _configuration["ClientBackURL"] ?? string.Empty;
            string hashKey = _configuration["HashKey"] ?? string.Empty;
            string hashIV = _configuration["HashIV"] ?? string.Empty;

            if (!int.TryParse(inModel.Amt, out var amount) || amount <= 0)
                return BadRequest("付款金額必須是正整數");
            if (string.IsNullOrWhiteSpace(inModel.ItemDesc))
                return BadRequest("商品資訊不可為空");
            if (inModel.ChannelID is not ("CREDIT" or "VACC"))
                return BadRequest("不支援的付款方式");
            if (string.IsNullOrWhiteSpace(merchantId)
                || string.IsNullOrWhiteSpace(returnUrl)
                || string.IsNullOrWhiteSpace(notifyUrl)
                || string.IsNullOrWhiteSpace(clientBackUrl)
                || !Uri.TryCreate(returnUrl, UriKind.Absolute, out var returnUri)
                || !Uri.TryCreate(notifyUrl, UriKind.Absolute, out var notifyUri)
                || !Uri.TryCreate(clientBackUrl, UriKind.Absolute, out var clientBackUri)
                || returnUri.Scheme != Uri.UriSchemeHttps
                || notifyUri.Scheme != Uri.UriSchemeHttps
                || clientBackUri.Scheme != Uri.UriSchemeHttps
                || returnUri == notifyUri
                || hashKey.Length != 32
                || hashIV.Length != 16)
                return StatusCode(StatusCodes.Status503ServiceUnavailable, "付款服務尚未完成設定");

            var customerUrl = new Uri(returnUri, "./CallbackCustomer").AbsoluteUri;

            // 藍新金流線上付款

            //交易欄位
            List<KeyValuePair<string, string>> TradeInfo = new List<KeyValuePair<string, string>>();
            // 商店代號
            TradeInfo.Add(new KeyValuePair<string, string>("MerchantID", merchantId));
            // 回傳格式
            TradeInfo.Add(new KeyValuePair<string, string>("RespondType", "String"));
            // TimeStamp
            TradeInfo.Add(new KeyValuePair<string, string>("TimeStamp", DateTimeOffset.Now.ToOffset(new TimeSpan(8, 0, 0)).ToUnixTimeSeconds().ToString()));
            // 串接程式版本
            TradeInfo.Add(new KeyValuePair<string, string>("Version", "2.0"));
            // 商店訂單編號
            TradeInfo.Add(new KeyValuePair<string, string>("MerchantOrderNo", DateTime.Now.ToString("yyyyMMddHHmmssfff")));
            // 訂單金額
            TradeInfo.Add(new KeyValuePair<string, string>("Amt", amount.ToString()));
            // 商品資訊
            TradeInfo.Add(new KeyValuePair<string, string>("ItemDesc", inModel.ItemDesc));
            // 繳費有效期限(適用於非即時交易)
            TradeInfo.Add(new KeyValuePair<string, string>("ExpireDate", DateTime.Now.AddDays(3).ToString("yyyyMMdd")));
            // 支付完成返回商店網址
            TradeInfo.Add(new KeyValuePair<string, string>("ReturnURL", returnUri.AbsoluteUri));
            // 支付通知網址
            TradeInfo.Add(new KeyValuePair<string, string>("NotifyURL", notifyUri.AbsoluteUri));
            // 商店取號網址
            TradeInfo.Add(new KeyValuePair<string, string>(
                "CustomerURL",
                customerUrl));
            // 支付取消返回商店網址
            TradeInfo.Add(new KeyValuePair<string, string>(
                "ClientBackURL",
                clientBackUri.AbsoluteUri));
            // 付款人電子信箱
            TradeInfo.Add(new KeyValuePair<string, string>("Email", inModel.Email));
            // 付款人電子信箱 是否開放修改(1=可修改 0=不可修改)
            TradeInfo.Add(new KeyValuePair<string, string>("EmailModify", "0"));

            //信用卡 付款
            if (inModel.ChannelID == "CREDIT")
            {
                TradeInfo.Add(new KeyValuePair<string, string>("CREDIT", "1"));
            }
            //ATM 付款
            if (inModel.ChannelID == "VACC")
            {
                TradeInfo.Add(new KeyValuePair<string, string>("VACC", "1"));
            }
            string TradeInfoParam = string.Join(
                "&",
                TradeInfo.Select(x => $"{HttpUtility.UrlEncode(x.Key)}={HttpUtility.UrlEncode(x.Value)}"));

            // API 傳送欄位
            // 商店代號
            outModel.MerchantID = merchantId;
            // 串接程式版本
            outModel.Version = "2.0";
            //交易資料 AES 加解密
            string TradeInfoEncrypt = EncryptAESHex(TradeInfoParam, hashKey, hashIV);
            outModel.TradeInfo = TradeInfoEncrypt;
            //交易資料 SHA256 加密
            outModel.TradeSha = EncryptSHA256($"HashKey={hashKey}&{TradeInfoEncrypt}&HashIV={hashIV}");

            return Json(outModel);
        }

        /// <summary>
        /// 支付完成返回網址
        /// </summary>
        /// <returns></returns>
        [HttpPost]
        [IgnoreAntiforgeryToken]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public async Task<IActionResult> CallbackReturn()
        {
            var callback = await ReadVerifiedCallbackAsync("支付回傳訊息");
            return callback is null
                ? BadRequest("付款回傳驗證失敗")
                : View(callback);
        }

        /// <summary>
        /// 商店取號網址
        /// </summary>
        /// <returns></returns>
        [HttpPost]
        [IgnoreAntiforgeryToken]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public async Task<IActionResult> CallbackCustomer()
        {
            var callback = await ReadVerifiedCallbackAsync("商店取號結果");
            return callback is null
                ? BadRequest("付款回傳驗證失敗")
                : View(callback);
        }

        /// <summary>
        /// 支付通知網址
        /// </summary>
        /// <returns></returns>
        [HttpPost]
        [IgnoreAntiforgeryToken]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public async Task<IActionResult> CallbackNotify()
        {
            var callback = await ReadVerifiedCallbackAsync("支付通知");
            if (callback is null)
                return BadRequest("付款回傳驗證失敗");

            _logger.LogInformation(
                "收到已驗證的付款通知，訂單 {MerchantOrderNo}，狀態 {Status}",
                callback.GetValue("MerchantOrderNo"),
                callback.GetValue("Status"));

            return Content("OK", "text/plain", Encoding.UTF8);
        }

        private async Task<PaymentCallbackViewModel?> ReadVerifiedCallbackAsync(string title)
        {
            if (!Request.HasFormContentType)
                return null;

            IFormCollection form;
            try
            {
                form = await Request.ReadFormAsync(HttpContext.RequestAborted);
            }
            catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException)
            {
                _logger.LogWarning(ex, "付款回傳表單格式錯誤");
                return null;
            }
            var tradeInfo = form["TradeInfo"].ToString();
            var tradeSha = form["TradeSha"].ToString();
            var hashKey = _configuration["HashKey"] ?? string.Empty;
            var hashIV = _configuration["HashIV"] ?? string.Empty;
            var merchantId = _configuration["MerchantID"] ?? string.Empty;

            if (hashKey.Length != 32
                || hashIV.Length != 16
                || string.IsNullOrWhiteSpace(merchantId)
                || !IsValidTradeSha(tradeInfo, tradeSha, hashKey, hashIV))
            {
                return null;
            }

            try
            {
                var decrypted = DecryptAESHex(tradeInfo, hashKey, hashIV);
                var values = HttpUtility.ParseQueryString(decrypted);
                if (!string.Equals(form["MerchantID"].ToString(), merchantId, StringComparison.Ordinal)
                    || !string.Equals(values["MerchantID"], merchantId, StringComparison.Ordinal))
                {
                    return null;
                }

                var fields = values.AllKeys
                    .Where(key => key is not null)
                    .Select(key => new PaymentCallbackField(key!, values[key!] ?? string.Empty))
                    .ToList();

                return fields.Count == 0
                    ? null
                    : new PaymentCallbackViewModel(title, fields);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
            {
                _logger.LogWarning(ex, "付款回傳資料通過驗章但無法解密");
                return null;
            }
        }

        private static bool IsValidTradeSha(
            string tradeInfo,
            string tradeSha,
            string hashKey,
            string hashIV)
        {
            if (string.IsNullOrWhiteSpace(tradeInfo) || string.IsNullOrWhiteSpace(tradeSha))
                return false;

            var expected = EncryptSHA256Value($"HashKey={hashKey}&{tradeInfo}&HashIV={hashIV}");
            var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
            var providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(tradeSha.ToUpperInvariant()));
            return CryptographicOperations.FixedTimeEquals(expectedHash, providedHash);
        }

        /// <summary>
        /// 加密後再轉 16 進制字串
        /// </summary>
        /// <param name="source">加密前字串</param>
        /// <param name="cryptoKey">加密金鑰</param>
        /// <param name="cryptoIV">cryptoIV</param>
        /// <returns>加密後的字串</returns>
        public string EncryptAESHex(string source, string cryptoKey, string cryptoIV)
        {
            string result = string.Empty;

            if (!string.IsNullOrEmpty(source))
            {
                var encryptValue = EncryptAES(Encoding.UTF8.GetBytes(source), cryptoKey, cryptoIV);

                if (encryptValue != null)
                {
                    result = BitConverter.ToString(encryptValue).Replace("-", string.Empty).ToLower();
                }
            }

            return result;
        }

        /// <summary>
        /// 字串加密AES
        /// </summary>
        /// <param name="source">加密前字串</param>
        /// <param name="cryptoKey">加密金鑰</param>
        /// <param name="cryptoIV">cryptoIV</param>
        /// <returns>加密後字串</returns>
        public byte[] EncryptAES(byte[] source, string cryptoKey, string cryptoIV)
        {
            byte[] dataKey = Encoding.UTF8.GetBytes(cryptoKey);
            byte[] dataIV = Encoding.UTF8.GetBytes(cryptoIV);

            using (var aes = System.Security.Cryptography.Aes.Create())
            {
                aes.Mode = System.Security.Cryptography.CipherMode.CBC;
                aes.Padding = System.Security.Cryptography.PaddingMode.PKCS7;
                aes.Key = dataKey;
                aes.IV = dataIV;

                using (var encryptor = aes.CreateEncryptor())
                {
                    return encryptor.TransformFinalBlock(source, 0, source.Length);
                }
            }
        }

        /// <summary>
        /// 字串加密SHA256
        /// </summary>
        /// <param name="source">加密前字串</param>
        /// <returns>加密後字串</returns>
        public string EncryptSHA256(string source)
            => EncryptSHA256Value(source);

        private static string EncryptSHA256Value(string source)
        {
            string result = string.Empty;

            using (SHA256 algorithm = SHA256.Create())
            {
                var hash = algorithm.ComputeHash(Encoding.UTF8.GetBytes(source));

                if (hash != null)
                {
                    result = BitConverter.ToString(hash).Replace("-", string.Empty).ToUpper();
                }

            }
            return result;
        }

        /// <summary>
        /// 16 進制字串解密
        /// </summary>
        /// <param name="source">加密前字串</param>
        /// <param name="cryptoKey">加密金鑰</param>
        /// <param name="cryptoIV">cryptoIV</param>
        /// <returns>解密後的字串</returns>
        public string DecryptAESHex(string source, string cryptoKey, string cryptoIV)
        {
            string result = string.Empty;

            if (!string.IsNullOrEmpty(source))
            {
                // 將 16 進制字串 轉為 byte[] 後
                byte[] sourceBytes = ToByteArray(source);

                if (sourceBytes.Length > 0)
                {
                    // 使用金鑰解密後，轉回 加密前 value
                    result = Encoding.UTF8.GetString(DecryptAES(sourceBytes, cryptoKey, cryptoIV)).Trim();
                }
            }

            return result;
        }

        /// <summary>
        /// 將16進位字串轉換為byteArray
        /// </summary>
        /// <param name="source">欲轉換之字串</param>
        /// <returns></returns>
        public byte[] ToByteArray(string source)
        {
            byte[] result = Array.Empty<byte>();

            if (!string.IsNullOrWhiteSpace(source))
            {
                if (source.Length % 2 != 0)
                    throw new FormatException("加密資料不是有效的 16 進位字串");

                var outputLength = source.Length / 2;
                var output = new byte[outputLength];

                for (var i = 0; i < outputLength; i++)
                {
                    output[i] = Convert.ToByte(source.Substring(i * 2, 2), 16);
                }
                result = output;
            }

            return result;
        }

        /// <summary>
        /// 字串解密AES
        /// </summary>
        /// <param name="source">解密前字串</param>
        /// <param name="cryptoKey">解密金鑰</param>
        /// <param name="cryptoIV">cryptoIV</param>
        /// <returns>解密後字串</returns>
        public static byte[] DecryptAES(byte[] source, string cryptoKey, string cryptoIV)
        {
            byte[] dataKey = Encoding.UTF8.GetBytes(cryptoKey);
            byte[] dataIV = Encoding.UTF8.GetBytes(cryptoIV);

            using (var aes = System.Security.Cryptography.Aes.Create())
            {
                aes.Mode = System.Security.Cryptography.CipherMode.CBC;
                // 智付通無法直接用PaddingMode.PKCS7，會跳"填補無效，而且無法移除。"
                // 所以改為PaddingMode.None並搭配RemovePKCS7Padding
                aes.Padding = System.Security.Cryptography.PaddingMode.None;
                aes.Key = dataKey;
                aes.IV = dataIV;

                using (var decryptor = aes.CreateDecryptor())
                {
                    byte[] data = decryptor.TransformFinalBlock(source, 0, source.Length);
                    if (data.Length == 0)
                        throw new CryptographicException("解密結果為空");

                    int paddingLength = data[^1];
                    if (paddingLength < 1
                        || paddingLength > aes.BlockSize / 8
                        || paddingLength > data.Length
                        || data.Skip(data.Length - paddingLength).Any(value => value != paddingLength))
                    {
                        throw new CryptographicException("解密資料的填補格式無效");
                    }

                    var output = new byte[data.Length - paddingLength];
                    Buffer.BlockCopy(data, 0, output, 0, output.Length);
                    return output;
                }
            }
        }
        #endregion
    }
}
