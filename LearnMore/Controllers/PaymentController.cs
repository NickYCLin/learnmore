using Microsoft.AspNetCore.Mvc;
using System.Collections.Specialized;
using System.Text;
using System.Web;
using static LearnMore.Models.NewebPayViewModel;

namespace LearnMore.Controllers
{
    public class PaymentController : Controller
    {
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
        [ValidateAntiForgeryToken]
        public IActionResult SendToNewebPay(SendToNewebPayIn inModel)
        {
            IConfiguration Config = new ConfigurationBuilder().AddJsonFile("appSettings.json").Build();
            SendToNewebPayOut outModel = new SendToNewebPayOut();
            string merchantId = Config.GetSection("MerchantID").Value ?? string.Empty;
            string returnUrl = Config.GetSection("ReturnURL").Value ?? string.Empty;
            string notifyUrl = Config.GetSection("NotifyURL").Value ?? string.Empty;
            string hashKey = Config.GetSection("HashKey").Value ?? string.Empty;
            string hashIV = Config.GetSection("HashIV").Value ?? string.Empty;

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
            TradeInfo.Add(new KeyValuePair<string, string>("Amt", inModel.Amt));
            // 商品資訊
            TradeInfo.Add(new KeyValuePair<string, string>("ItemDesc", inModel.ItemDesc));
            // 繳費有效期限(適用於非即時交易)
            TradeInfo.Add(new KeyValuePair<string, string>("ExpireDate", DateTime.Now.AddDays(3).ToString("yyyyMMdd")));
            // 支付完成返回商店網址
            TradeInfo.Add(new KeyValuePair<string, string>("ReturnURL", returnUrl));
            // 支付通知網址
            TradeInfo.Add(new KeyValuePair<string, string>("NotifyURL", notifyUrl));
            // 商店取號網址
            TradeInfo.Add(new KeyValuePair<string, string>("CustomerURL", inModel.CustomerURL));
            // 支付取消返回商店網址
            TradeInfo.Add(new KeyValuePair<string, string>("ClientBackURL", inModel.ClientBackURL));
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
            string TradeInfoParam = string.Join("&", TradeInfo.Select(x => $"{x.Key}={x.Value}"));

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
        public IActionResult CallbackReturn()
        {
            // 接收參數
            StringBuilder receive = new StringBuilder();
            foreach (var item in Request.Form)
            {
                receive.AppendLine(item.Key + "=" + item.Value + "<br>");
            }
            ViewData["ReceiveObj"] = receive.ToString();

            // 解密訊息
            IConfiguration Config = new ConfigurationBuilder().AddJsonFile("appSettings.json").Build();
            string hashKey = Config.GetSection("HashKey").Value ?? string.Empty;//API 串接金鑰
            string hashIV = Config.GetSection("HashIV").Value ?? string.Empty;//API 串接密碼
            string tradeInfo = Request.Form["TradeInfo"].ToString();

            string TradeInfoDecrypt = DecryptAESHex(tradeInfo, hashKey, hashIV);
            NameValueCollection decryptTradeCollection = HttpUtility.ParseQueryString(TradeInfoDecrypt);
            receive.Length = 0;
            foreach (string? key in decryptTradeCollection.AllKeys)
            {
                if (key is null)
                {
                    continue;
                }

                receive.AppendLine(key + "=" + decryptTradeCollection[key] + "<br>");
            }
            ViewData["TradeInfo"] = receive.ToString();

            return View();
        }

        /// <summary>
        /// 商店取號網址
        /// </summary>
        /// <returns></returns>
        public IActionResult CallbackCustomer()
        {
            // 接收參數
            StringBuilder receive = new StringBuilder();
            foreach (var item in Request.Form)
            {
                receive.AppendLine(item.Key + "=" + item.Value + "<br>");
            }
            ViewData["ReceiveObj"] = receive.ToString();

            // 解密訊息
            IConfiguration Config = new ConfigurationBuilder().AddJsonFile("appSettings.json").Build();
            string hashKey = Config.GetSection("HashKey").Value ?? string.Empty;//API 串接金鑰
            string hashIV = Config.GetSection("HashIV").Value ?? string.Empty;//API 串接密碼
            string tradeInfo = Request.Form["TradeInfo"].ToString();
            string TradeInfoDecrypt = DecryptAESHex(tradeInfo, hashKey, hashIV);
            NameValueCollection decryptTradeCollection = HttpUtility.ParseQueryString(TradeInfoDecrypt);
            receive.Length = 0;
            foreach (string? key in decryptTradeCollection.AllKeys)
            {
                if (key is null)
                {
                    continue;
                }

                receive.AppendLine(key + "=" + decryptTradeCollection[key] + "<br>");
            }
            ViewData["TradeInfo"] = receive.ToString();
            return View();
        }

        /// <summary>
        /// 支付通知網址
        /// </summary>
        /// <returns></returns>
        public IActionResult CallbackNotify()
        {
            // 接收參數
            StringBuilder receive = new StringBuilder();
            foreach (var item in Request.Form)
            {
                receive.AppendLine(item.Key + "=" + item.Value + "<br>");
            }
            ViewData["ReceiveObj"] = receive.ToString();

            // 解密訊息
            IConfiguration Config = new ConfigurationBuilder().AddJsonFile("appSettings.json").Build();
            string hashKey = Config.GetSection("HashKey").Value ?? string.Empty;//API 串接金鑰
            string hashIV = Config.GetSection("HashIV").Value ?? string.Empty;//API 串接密碼
            string tradeInfo = Request.Form["TradeInfo"].ToString();
            string TradeInfoDecrypt = DecryptAESHex(tradeInfo, hashKey, hashIV);
            NameValueCollection decryptTradeCollection = HttpUtility.ParseQueryString(TradeInfoDecrypt);
            receive.Length = 0;
            foreach (string? key in decryptTradeCollection.AllKeys)
            {
                if (key is null)
                {
                    continue;
                }

                receive.AppendLine(key + "=" + decryptTradeCollection[key] + "<br>");
            }
            ViewData["TradeInfo"] = receive.ToString();

            return View();
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
        {
            string result = string.Empty;

            using (System.Security.Cryptography.SHA256 algorithm = System.Security.Cryptography.SHA256.Create())
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
                    int iLength = data[data.Length - 1];
                    var output = new byte[data.Length - iLength];
                    Buffer.BlockCopy(data, 0, output, 0, output.Length);
                    return output;
                }
            }
        }
        #endregion
    }
}
