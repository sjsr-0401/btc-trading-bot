namespace BtcTradingBot.Models;

public record CandleUpdate(Candle Candle, int Index, bool IsNew, bool IsClosed);
