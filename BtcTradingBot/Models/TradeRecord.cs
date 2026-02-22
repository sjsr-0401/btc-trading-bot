namespace BtcTradingBot.Models;

public class TradeRecord
{
    public DateTime Time { get; init; }
    public string Symbol { get; init; } = "";
    public string Side { get; init; } = "";
    public double EntryPrice { get; init; }
    public double ExitPrice { get; init; }
    public double Pnl { get; init; }
    public double Balance { get; init; }
    public string CloseReason { get; init; } = "";

    public string TimeText => Time.ToString("MM/dd HH:mm");
    public string SymbolShort => Symbol.Replace("USDT", "");
    public string PnlText => $"{(Pnl >= 0 ? "+" : "")}{Pnl:F2}";
    public string BalanceText => $"${Balance:F2}";
    public string SideColor => Side == "LONG" ? "#22C55E" : "#EF4444";
    public string PnlColor => Pnl >= 0 ? "#22C55E" : "#EF4444";

    public double PnlPercent => EntryPrice > 0 && ExitPrice > 0
        ? (Side == "LONG"
            ? (ExitPrice - EntryPrice) / EntryPrice * 100
            : (EntryPrice - ExitPrice) / EntryPrice * 100)
        : 0;
    public string PnlPercentText => $"({(PnlPercent >= 0 ? "+" : "")}{PnlPercent:F2}%)";

    public string CloseReasonColor => CloseReason switch
    {
        "TP" => "#22C55E",
        "SL" => "#EF4444",
        "Trail" => "#F59E0B",
        _ => "#9CA3AF",
    };
}
