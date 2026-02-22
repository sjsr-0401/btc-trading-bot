namespace BtcTradingBot.Models;

public record Position(
    string Type, // "L", "S", "N"
    double Amount,
    double EntryPrice,
    double UnrealizedPnl
)
{
    public string Symbol { get; init; } = "";
    public int Leverage { get; init; } = 1;
}

public record OpenOrder(string Type, double StopPrice);
