using BtcTradingBot.Models;

namespace BtcTradingBot.Services;

/// <summary>
/// USDM 전략용 지표: Python indicators.py 1:1 이식
/// 모든 스무딩은 ewm(adjust=False) = Wilder smoothing 기반
/// 신호 캔들 = iloc[-2] (마지막 확정 캔들) 기준
/// </summary>
public static class IndicatorsUsdm
{
    /// <summary>Donchian Channel: 신호 캔들(^2) 이전 N봉의 최고가/최저가 (Python prev["don_hi"] 동일)</summary>
    public static (double high, double low) DonchianChannel(List<Candle> candles, int window = 20)
    {
        // Python: signal_at uses iloc[-2] as signal, prev=iloc[-3]
        // prev["don_hi"] = rolling(window).max() ending at candle[-3]
        if (candles.Count < window + 2) return (0, 0);

        double high = double.MinValue, low = double.MaxValue;
        for (int i = candles.Count - 2 - window; i < candles.Count - 2; i++)
        {
            if (candles[i].High > high) high = candles[i].High;
            if (candles[i].Low < low) low = candles[i].Low;
        }
        return (high, low);
    }

    /// <summary>
    /// EMA: Python ewm(span=period, adjust=False).mean() 동일
    /// alpha = 2/(span+1), seed = x[0]
    /// </summary>
    public static double[] Ema(double[] data, int period)
    {
        if (data.Length == 0) return Array.Empty<double>();

        double alpha = 2.0 / (period + 1);
        var result = new double[data.Length];
        result[0] = data[0];

        for (int i = 1; i < data.Length; i++)
            result[i] = alpha * data[i] + (1 - alpha) * result[i - 1];

        return result;
    }

    /// <summary>
    /// RSI (Wilder EWM): Python rsi() 동일
    /// ewm(alpha=1/period, adjust=False), 신호 캔들(^2)까지 계산
    /// </summary>
    public static double RsiValue(double[] closes, int period = 14)
    {
        // Python: delta = close.diff() → NaN at 0, ewm skips NaN → seed = delta[1]
        // Compute up to closes[^2] (second-to-last = signal candle)
        int end = closes.Length - 1; // exclusive
        if (end < 2) return 50;

        double alpha = 1.0 / period;

        // Seed from first delta (Python: ewm starts from first non-NaN)
        double d0 = closes[1] - closes[0];
        double avgUp = d0 > 0 ? d0 : 0;
        double avgDown = d0 < 0 ? -d0 : 0;

        for (int i = 2; i < end; i++)
        {
            double d = closes[i] - closes[i - 1];
            avgUp = alpha * (d > 0 ? d : 0) + (1 - alpha) * avgUp;
            avgDown = alpha * (d < 0 ? -d : 0) + (1 - alpha) * avgDown;
        }

        if (avgDown < 1e-12) return 100;
        return 100 - (100 / (1 + avgUp / avgDown));
    }

    /// <summary>
    /// ATR (Wilder EWM): Python atr() 동일
    /// ewm(alpha=1/period, adjust=False), 신호 캔들(^2)까지 계산
    /// </summary>
    public static double AtrValue(List<Candle> candles, int period = 14)
    {
        if (candles.Count < 3) return 0;

        double alpha = 1.0 / period;

        // TR[0] = high-low (Python: prev_close=NaN → max(h-l, NaN, NaN) = h-l)
        double atr = candles[0].High - candles[0].Low;

        // Compute up to second-to-last candle (Python: feat["atr"].iloc[-2])
        int end = candles.Count - 1;
        for (int i = 1; i < end; i++)
        {
            double h = candles[i].High, l = candles[i].Low, pc = candles[i - 1].Close;
            double tr = Math.Max(h - l, Math.Max(Math.Abs(h - pc), Math.Abs(l - pc)));
            atr = alpha * tr + (1 - alpha) * atr;
        }

        return atr;
    }

    /// <summary>
    /// ADX (Wilder EWM): Python adx() 동일
    /// DM, DI, DX, ADX 모두 ewm(alpha=1/period, adjust=False)
    /// 신호 캔들(^2)까지 계산
    /// </summary>
    public static (double adx, double plusDi, double minusDi) AdxValue(List<Candle> candles, int period = 14)
    {
        if (candles.Count < period + 2) return (20, 0, 0);

        int n = candles.Count;
        double alpha = 1.0 / period;

        // TR, +DM, -DM 배열
        double[] tr = new double[n];
        double[] pdm = new double[n];
        double[] mdm = new double[n];

        // Index 0: Python diff() at 0 = NaN → comparison false → DM = 0
        tr[0] = candles[0].High - candles[0].Low;
        pdm[0] = 0;
        mdm[0] = 0;

        for (int i = 1; i < n; i++)
        {
            double h = candles[i].High, l = candles[i].Low, pc = candles[i - 1].Close;
            tr[i] = Math.Max(h - l, Math.Max(Math.Abs(h - pc), Math.Abs(l - pc)));

            double upMove = candles[i].High - candles[i - 1].High;
            double downMove = candles[i - 1].Low - candles[i].Low;
            pdm[i] = (upMove > downMove && upMove > 0) ? upMove : 0;
            mdm[i] = (downMove > upMove && downMove > 0) ? downMove : 0;
        }

        // Wilder EWM smoothing (seed = index 0 values)
        double smoothTr = tr[0];
        double smoothPdm = 0; // pdm[0] = 0
        double smoothMdm = 0; // mdm[0] = 0

        double pdi = 0, mdi = 0;
        double adxVal = 0; // DX[0] = 0 (because +DI[0]=0, -DI[0]=0)

        // Compute up to second-to-last candle (Python: feat["adx"].iloc[-2])
        int end = n - 1;
        for (int i = 1; i < end; i++)
        {
            smoothTr = alpha * tr[i] + (1 - alpha) * smoothTr;
            smoothPdm = alpha * pdm[i] + (1 - alpha) * smoothPdm;
            smoothMdm = alpha * mdm[i] + (1 - alpha) * smoothMdm;

            pdi = 100 * smoothPdm / (smoothTr + 1e-12);
            mdi = 100 * smoothMdm / (smoothTr + 1e-12);

            double total = pdi + mdi;
            double dx = total > 1e-12 ? 100 * Math.Abs(pdi - mdi) / total : 0;

            adxVal = alpha * dx + (1 - alpha) * adxVal;
        }

        return (adxVal, pdi, mdi);
    }

    /// <summary>Bollinger Bands: SMA + StdDev (Python bollinger() 동일 — ddof=0)</summary>
    public static (double[] sma, double[] upper, double[] lower) BollingerBands(
        double[] closes, int period = 20, double mult = 2.0)
        => Indicators.BollingerBands(closes, period, mult);
}
