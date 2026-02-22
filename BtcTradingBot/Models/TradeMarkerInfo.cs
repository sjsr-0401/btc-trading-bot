namespace BtcTradingBot.Models;

public record TradeMarkerInfo(
    string Action,       // "ENTRY_LONG", "ENTRY_SHORT", "EXIT"
    double Price,
    double? SlPrice,     // 진입 시에만
    double? TpPrice,     // 진입 시에만
    DateTime Time
);
