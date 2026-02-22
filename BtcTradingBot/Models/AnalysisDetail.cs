namespace BtcTradingBot.Models;

public record AnalysisDetail(
    double Ema7, double Ema21, double Ema50, double Price,
    double Rsi, int RsiScore,
    double MacdHist, double MacdHistPrev, int MacdScore,
    double Adx, double PlusDi, double MinusDi, int AdxScore,
    double VolumeRatio, int VolumeScore,
    bool IsBullishCandle, int CandleScore,
    string TrendH1, int TrendH1Score,
    string Trend4H, int Trend4HScore,
    double AtrValue, double SlPct, double TpPct
);
