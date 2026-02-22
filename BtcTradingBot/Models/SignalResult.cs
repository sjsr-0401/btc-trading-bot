namespace BtcTradingBot.Models;

public record SignalResult(
    string Direction, // "L", "S", "W"
    int Score,
    List<string> Reasons,
    AnalysisDetail? Detail = null
);
