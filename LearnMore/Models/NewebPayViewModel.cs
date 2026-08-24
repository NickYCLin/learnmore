namespace LearnMore.Models
{
    public class NewebPayViewModel
    {
        public class SendToNewebPayIn
        {
            public string ChannelID { get; set; } = string.Empty;
            public string MerchantID { get; set; } = string.Empty;
            public string MerchantOrderNo { get; set; } = string.Empty;
            public string ItemDesc { get; set; } = string.Empty;
            public string Amt { get; set; } = string.Empty;
            public string ExpireDate { get; set; } = string.Empty;
            public string ReturnURL { get; set; } = string.Empty;
            public string CustomerURL { get; set; } = string.Empty;
            public string NotifyURL { get; set; } = string.Empty;
            public string ClientBackURL { get; set; } = string.Empty;
            public string Email { get; set; } = string.Empty;
        }

        public class SendToNewebPayOut
        {
            public string MerchantID { get; set; } = string.Empty;
            public string Version { get; set; } = string.Empty;
            public string TradeInfo { get; set; } = string.Empty;
            public string TradeSha { get; set; } = string.Empty;
        }
    }
}
