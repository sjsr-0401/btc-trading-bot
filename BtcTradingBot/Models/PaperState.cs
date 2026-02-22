namespace BtcTradingBot.Models;

public class PaperState
{
    public string Symbol { get; set; } = "BTCUSDT";
    public string PositionType { get; set; } = "N";
    public double Amount { get; set; }
    public double EntryPrice { get; set; }
    public double SlPrice { get; set; }
    public double TpPrice { get; set; }
    public double Balance { get; set; }
    public double EntryFee { get; set; }
    public DateTime? OpenTime { get; set; }
    public int TotalTrades { get; set; }
    public int TotalWins { get; set; }
    public int TotalLosses { get; set; }
    public double TotalPnl { get; set; }
    public double PeakBalance { get; set; }
    public double MaxDrawdownPct { get; set; }
    public double TotalGrossProfit { get; set; }
    public double TotalGrossLoss { get; set; }
}
