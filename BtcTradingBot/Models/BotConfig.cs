namespace BtcTradingBot.Models;

public class BotConfig
{
    public string ApiKey { get; set; } = "";
    public string ApiSecret { get; set; } = "";
    public string Symbol { get; set; } = "BTCUSDT";
    public int Leverage { get; set; } = 4;
    public double TradeUsdt { get; set; } = 35;
    public int CheckIntervalSeconds { get; set; } = 60;
    public int MaxDailyTrades { get; set; } = 4;
    public double MaxDailyLossPct { get; set; } = 3.0;
    public int MaxConsecLosses { get; set; } = 3;
    public int CooldownMinutes { get; set; } = 75;
    public string DiscordWebhook { get; set; } = "";
    public string SelectedEngine { get; set; } = "KYJ";
    public bool IsTestMode { get; set; }
    public double TestBalance { get; set; } = 100;
    public int ScanIntervalSec { get; set; } = 60;
    public int ScanCoinCount { get; set; } = 10;
    public bool AutoSwitchEnabled { get; set; }
    public int AutoEntryScore { get; set; } = 50;
    public string? LastMode { get; set; } // "Classic" or "Multi"
}
