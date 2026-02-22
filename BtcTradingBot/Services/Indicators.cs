namespace BtcTradingBot.Services;

using BtcTradingBot.Models;

public static class Indicators
{
    public static double[] Ema(double[] data, int period)
    {
        if (data.Length < period)
            return (double[])data.Clone();

        double multiplier = 2.0 / (period + 1);
        var result = new List<double>();
        double seed = 0;
        for (int i = 0; i < period; i++) seed += data[i];
        seed /= period;
        result.Add(seed);

        for (int i = period; i < data.Length; i++)
            result.Add(data[i] * multiplier + result[^1] * (1 - multiplier));

        var final = new double[data.Length];
        for (int i = 0; i < period; i++) final[i] = result[0];
        for (int i = 0; i < result.Count; i++) final[period - 1 + i] = result[i];
        return final;
    }

    public static double Rsi(double[] closes, int period = 14)
    {
        if (closes.Length < period + 1) return 50;
        double avgGain = 0, avgLoss = 0;
        for (int i = 1; i <= period; i++)
        {
            double d = closes[i] - closes[i - 1];
            if (d > 0) avgGain += d; else avgLoss -= d;
        }
        avgGain /= period;
        avgLoss /= period;
        for (int i = period + 1; i < closes.Length; i++)
        {
            double d = closes[i] - closes[i - 1];
            avgGain = (avgGain * (period - 1) + (d > 0 ? d : 0)) / period;
            avgLoss = (avgLoss * (period - 1) + (d < 0 ? -d : 0)) / period;
        }
        if (avgLoss == 0) return 100;
        return 100 - (100 / (1 + avgGain / avgLoss));
    }

    public static double[] MacdHistogram(double[] closes)
    {
        var e12 = Ema(closes, 12);
        var e26 = Ema(closes, 26);
        var macdLine = new double[closes.Length];
        for (int i = 0; i < closes.Length; i++)
            macdLine[i] = e12[i] - e26[i];
        var signal = Ema(macdLine, 9);
        var hist = new double[closes.Length];
        for (int i = 0; i < closes.Length; i++)
            hist[i] = macdLine[i] - signal[i];
        return hist;
    }

    public static double Atr(List<Candle> candles, int period = 14)
    {
        if (candles.Count < period + 1) return 0;
        var trList = new List<double>();
        for (int i = 1; i < candles.Count; i++)
        {
            double h = candles[i].High, l = candles[i].Low, pc = candles[i - 1].Close;
            trList.Add(Math.Max(h - l, Math.Max(Math.Abs(h - pc), Math.Abs(l - pc))));
        }
        if (trList.Count >= period)
            return trList.Skip(trList.Count - period).Take(period).Average();
        return trList.Average();
    }

    public static (double adx, double plusDi, double minusDi) Adx(List<Candle> candles, int period = 14)
    {
        if (candles.Count < period * 2) return (20, 0, 0);
        var pdm = new List<double>();
        var mdm = new List<double>();
        var trList = new List<double>();
        for (int i = 1; i < candles.Count; i++)
        {
            double h = candles[i].High, l = candles[i].Low;
            double ph = candles[i - 1].High, pl = candles[i - 1].Low, pc = candles[i - 1].Close;
            double pd = h - ph, md = pl - l;
            pdm.Add(pd > md && pd > 0 ? pd : 0);
            mdm.Add(md > pd && md > 0 ? md : 0);
            trList.Add(Math.Max(h - l, Math.Max(Math.Abs(h - pc), Math.Abs(l - pc))));
        }
        double avgTr = trList.Take(period).Average();
        double plusSmooth = pdm.Take(period).Average();
        double minusSmooth = mdm.Take(period).Average();
        var dxList = new List<double>();
        double pdi = 0, mdi = 0;
        for (int i = period; i < trList.Count; i++)
        {
            avgTr = (avgTr * (period - 1) + trList[i]) / period;
            plusSmooth = (plusSmooth * (period - 1) + pdm[i]) / period;
            minusSmooth = (minusSmooth * (period - 1) + mdm[i]) / period;
            if (avgTr > 0) { pdi = 100 * plusSmooth / avgTr; mdi = 100 * minusSmooth / avgTr; }
            double total = pdi + mdi;
            dxList.Add(total > 0 ? 100 * Math.Abs(pdi - mdi) / total : 0);
        }
        double adxVal = dxList.Count >= period
            ? dxList.Skip(dxList.Count - period).Take(period).Average()
            : dxList.Count > 0 ? dxList.Average() : 0;
        return (adxVal, pdi, mdi);
    }

    // === KSJ v0.1 지표 ===

    /// <summary>Rolling Standard Deviation</summary>
    public static double[] StdDev(double[] data, int period = 20)
    {
        var result = new double[data.Length];
        for (int i = period - 1; i < data.Length; i++)
        {
            double sum = 0, sumSq = 0;
            for (int j = i - period + 1; j <= i; j++)
            {
                sum += data[j];
                sumSq += data[j] * data[j];
            }
            double mean = sum / period;
            double variance = sumSq / period - mean * mean;
            result[i] = Math.Sqrt(Math.Max(0, variance));
        }
        // 초기 구간은 첫 유효값으로 채움
        if (period - 1 < data.Length)
            for (int i = 0; i < period - 1; i++)
                result[i] = result[period - 1];
        return result;
    }

    /// <summary>Simple Moving Average</summary>
    public static double[] Sma(double[] data, int period)
    {
        var result = new double[data.Length];
        double sum = 0;
        for (int i = 0; i < data.Length; i++)
        {
            sum += data[i];
            if (i >= period) sum -= data[i - period];
            result[i] = i >= period - 1 ? sum / period : sum / (i + 1);
        }
        return result;
    }

    /// <summary>Bollinger Bands: SMA(period) ± mult × StdDev(period)</summary>
    public static (double[] sma, double[] upper, double[] lower) BollingerBands(
        double[] closes, int period = 20, double mult = 2.0)
    {
        var sma = Sma(closes, period);
        var std = StdDev(closes, period);
        var upper = new double[closes.Length];
        var lower = new double[closes.Length];
        for (int i = 0; i < closes.Length; i++)
        {
            upper[i] = sma[i] + mult * std[i];
            lower[i] = sma[i] - mult * std[i];
        }
        return (sma, upper, lower);
    }

    /// <summary>Keltner Channels: EMA(period) ± mult × RollingATR(period)</summary>
    public static (double[] ema, double[] upper, double[] lower) KeltnerChannels(
        List<Candle> candles, int period = 20, double mult = 1.5)
    {
        var closes = candles.Select(c => c.Close).ToArray();
        var ema = Ema(closes, period);

        // Rolling ATR
        var atrArr = new double[candles.Count];
        if (candles.Count > 1)
        {
            var trList = new double[candles.Count];
            trList[0] = candles[0].High - candles[0].Low;
            for (int i = 1; i < candles.Count; i++)
            {
                double h = candles[i].High, l = candles[i].Low, pc = candles[i - 1].Close;
                trList[i] = Math.Max(h - l, Math.Max(Math.Abs(h - pc), Math.Abs(l - pc)));
            }
            // SMA of TR as rolling ATR
            double sum = 0;
            for (int i = 0; i < candles.Count; i++)
            {
                sum += trList[i];
                if (i >= period) sum -= trList[i - period];
                atrArr[i] = i >= period - 1 ? sum / period : sum / (i + 1);
            }
        }

        var upper = new double[candles.Count];
        var lower = new double[candles.Count];
        for (int i = 0; i < candles.Count; i++)
        {
            upper[i] = ema[i] + mult * atrArr[i];
            lower[i] = ema[i] - mult * atrArr[i];
        }
        return (ema, upper, lower);
    }

    /// <summary>Rolling ATR array (단일 값이 아닌 전체 배열)</summary>
    public static double[] AtrArray(List<Candle> candles, int period = 14)
    {
        var result = new double[candles.Count];
        if (candles.Count < 2) return result;

        var tr = new double[candles.Count];
        tr[0] = candles[0].High - candles[0].Low;
        for (int i = 1; i < candles.Count; i++)
        {
            double h = candles[i].High, l = candles[i].Low, pc = candles[i - 1].Close;
            tr[i] = Math.Max(h - l, Math.Max(Math.Abs(h - pc), Math.Abs(l - pc)));
        }

        double sum = 0;
        for (int i = 0; i < candles.Count; i++)
        {
            sum += tr[i];
            if (i >= period) sum -= tr[i - period];
            result[i] = i >= period - 1 ? sum / period : sum / (i + 1);
        }
        return result;
    }
}
