namespace BtcTradingBot.Models;

public record UsdmSignalDetail(
    string Regime,           // "Trend", "Range", "Neutral"
    double Adx,
    double Rsi,
    double DonchianHigh,
    double DonchianLow,
    double BbUpper,
    double BbSma,
    double BbLower,
    double Ema50,
    double Ema200,
    double Atr,
    double StopPrice,
    double TpPrice,
    string StrategyTag       // "DonchianBreakout", "BBReversal"
);
