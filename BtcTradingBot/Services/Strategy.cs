using BtcTradingBot.Models;

namespace BtcTradingBot.Services;

public static class Strategy
{
    public static SignalResult CheckEmaSignal(List<Candle> c15, List<Candle> c1h, List<Candle> c4h, int lookback = 1)
    {
        if (c15.Count < 100 || c1h.Count < 50 || c4h.Count < 50)
            return new("W", 0, new() { "데이터 부족" });

        var closes = c15.Select(c => c.Close).ToArray();
        var e7 = Indicators.Ema(closes, 7);
        var e21 = Indicators.Ema(closes, 21);
        var e50 = Indicators.Ema(closes, 50);
        double cur = closes[^1];

        // 항상 기본 지표 계산
        double rsiVal = Indicators.Rsi(closes);
        var macdHist = Indicators.MacdHistogram(closes);
        double macdH = macdHist[^1], macdHPrev = macdHist[^2];
        var (adxVal, plusDi, minusDi) = Indicators.Adx(c1h);
        bool bullish = c15[^1].Close > c15[^1].Open;

        // 거래량 비율
        double volRatio = 0;
        var vols = c15.TakeLast(21).Select(c => c.Volume).ToArray();
        if (vols.Length >= 21)
        {
            double av = vols.Take(20).Average();
            volRatio = av > 0 ? vols[^1] / av : 0;
        }

        // 1H/4H 추세
        var c1hc = c1h.Select(c => c.Close).ToArray();
        var c4hc = c4h.Select(c => c.Close).ToArray();
        var e21h = Indicators.Ema(c1hc, 21);
        var e50h = Indicators.Ema(c1hc, 50);
        var e21_4 = Indicators.Ema(c4hc, 21);

        string h1Trend = c1hc[^1] > e21h[^1] && c1hc[^1] > e50h[^1] ? "강세"
                       : c1hc[^1] > e21h[^1] ? "상승"
                       : c1hc[^1] < e21h[^1] && c1hc[^1] < e50h[^1] ? "약세"
                       : c1hc[^1] < e21h[^1] ? "하락" : "중립";
        string h4Trend = c4hc[^1] > e21_4[^1] ? "상승" : "하락";

        // 기본 Detail (점수 0으로 — 크로스 발생 시 아래서 갱신)
        AnalysisDetail MakeDetail(int rsiSc, int macdSc, int adxSc, int volSc, int candleSc, int h1Sc, int h4Sc) =>
            new(e7[^1], e21[^1], e50[^1], cur,
                rsiVal, rsiSc, macdH, macdHPrev, macdSc,
                adxVal, plusDi, minusDi, adxSc,
                volRatio, volSc, bullish, candleSc,
                h1Trend, h1Sc, h4Trend, h4Sc, 0, 0, 0);

        // 크로스 탐색 (lookback=1: 직전 1봉, lookback>1: 사전분석용)
        bool crossUp = false, crossDn = false;
        for (int bi = closes.Length - 1; bi >= Math.Max(1, closes.Length - lookback); bi--)
        {
            if (e7[bi] > e21[bi] && e7[bi - 1] <= e21[bi - 1]) { crossUp = true; break; }
            if (e7[bi] < e21[bi] && e7[bi - 1] >= e21[bi - 1]) { crossDn = true; break; }
        }
        // 크로스 빈도 분석 (최근 50봉 = 12.5시간)
        int recentCrossCount = 0;
        for (int bi = closes.Length - 1; bi >= Math.Max(1, closes.Length - 50); bi--)
        {
            if ((e7[bi] > e21[bi] && e7[bi - 1] <= e21[bi - 1]) ||
                (e7[bi] < e21[bi] && e7[bi - 1] >= e21[bi - 1]))
                recentCrossCount++;
        }

        if (!crossUp && !crossDn)
            return new("W", 0, new() { $"크로스 대기 (50봉내 {recentCrossCount}회)" }, MakeDetail(0, 0, 0, 0, 0, 0, 0));

        // 크로스 빈도 필터: 50봉 내 4회 이상 → 잦은 전환 = 횡보장
        if (recentCrossCount >= 4)
            return new("W", 0, new() { $"횡보장 (크로스 {recentCrossCount}회/50봉)" }, MakeDetail(0, 0, 0, 0, 0, 0, 0));

        // ATR 횡보장 필터: 현재 ATR이 최근 평균 대비 60% 미만이면 스킵
        var atrArr = Indicators.AtrArray(c15, 14);
        if (atrArr.Length >= 20)
        {
            double curAtr = atrArr[^1];
            double avgAtr = atrArr.Skip(atrArr.Length - 20).Take(20).Average();
            if (avgAtr > 0 && curAtr < avgAtr * 0.6)
                return new("W", 0, new() { "횡보장 (ATR↓)" }, MakeDetail(0, 0, 0, 0, 0, 0, 0));
        }

        int score = 0;
        var reasons = new List<string>();
        int rsiScore = 0, macdScore = 0, adxScore = 0, volScore = 0, candleScore = 0;
        int h1Score = 0, h4Score = 0;

        // 크로스 빈도 감점: 3회 이상 = 불안정 (4회 이상은 위에서 이미 스킵)
        if (recentCrossCount >= 3) { score -= 5; }

        if (crossUp)
        {
            score += 20; reasons.Add($"+ 골든크로스 (50봉내 {recentCrossCount}회)");
            if (cur <= e50[^1]) return new("W", 0, new() { "! 50EMA아래" }, MakeDetail(0, 0, 0, 0, 0, 0, 0));

            if (c4hc[^1] <= e21_4[^1]) return new("W", 0, new() { "! 4H하락" }, MakeDetail(0, 0, 0, 0, 0, 0, 0));
            score += 15; h4Score = 15; h4Trend = "상승";

            if (c1hc[^1] > e21h[^1] && c1hc[^1] > e50h[^1]) { h1Score = 15; score += 15; reasons.Add("+ 1H강세"); h1Trend = "강세"; }
            else if (c1hc[^1] > e21h[^1]) { h1Score = 8; score += 8; reasons.Add("+ 1H상승"); h1Trend = "상승"; }
            else { h1Score = -10; score -= 10; h1Trend = "약세"; }

            if (rsiVal > 70) return new("W", 0, new() { $"! RSI{(int)rsiVal}" }, MakeDetail(0, 0, 0, 0, 0, h1Score, h4Score));
            if (rsiVal > 30 && rsiVal < 45)  { rsiScore = 10;  score += 10; reasons.Add($"+ RSI{(int)rsiVal}"); }
            else if (rsiVal < 55)            { rsiScore = 5;   score += 5; }
            else if (rsiVal < 65)            { rsiScore = -5;  score -= 5;  reasons.Add($"- RSI과열({(int)rsiVal})"); }
            else                             { rsiScore = -10; score -= 10; reasons.Add($"- RSI과열({(int)rsiVal})"); }

            if (macdH > 0 && macdH > macdHPrev) { macdScore = 10; score += 10; reasons.Add("+ MACD↑"); }
            else if (macdH > macdHPrev) { macdScore = 5; score += 5; }
            else if (macdH < 0 && macdH < macdHPrev) { macdScore = -10; score -= 10; }

            if (adxVal > 25 && plusDi > minusDi) { adxScore = 10; score += 10; reasons.Add($"+ ADX{(int)adxVal}"); }
            else if (adxVal < 20) { adxScore = -5; score -= 5; }

            if (vols.Length >= 21)
            {
                volScore = (int)Math.Clamp((volRatio - 1.0) * 10, -8, 8);
                score += volScore;
                if (volScore >= 5) reasons.Add("+ 거래량↑");
            }

            if (bullish) { candleScore = 5; score += 5; } else { candleScore = -3; score -= 3; }

            // EMA7 이격도
            double emaDevLong = e7[^1] > 0 ? (cur - e7[^1]) / e7[^1] * 100 : 0;
            if      (emaDevLong > 3.0)                        { score -= 12; reasons.Add($"- 이격과다 (+{emaDevLong:F1}%)"); }
            else if (emaDevLong > 1.5)                        { score -= 6; }
            else if (emaDevLong >= -0.5 && emaDevLong <= 0.5) { score += 8;  reasons.Add($"+ EMA근접 ({emaDevLong:F1}%)"); }
            else if (emaDevLong < -1.5)                       { score -= 6;  reasons.Add($"- EMA하회 ({emaDevLong:F1}%)"); }
            // 0.5~1.5%: 중립 (0점) / -1.5~-0.5%: 중립 (0점)

            reasons.Add($"score:{score}");
            var detail = MakeDetail(rsiScore, macdScore, adxScore, volScore, candleScore, h1Score, h4Score);
            return score >= 45 ? new("L", score, reasons, detail) : new("W", score, reasons, detail);
        }
        else
        {
            score += 20; reasons.Add($"+ 데드크로스 (50봉내 {recentCrossCount}회)");
            if (cur >= e50[^1]) return new("W", 0, new() { "! 50EMA위" }, MakeDetail(0, 0, 0, 0, 0, 0, 0));

            if (c4hc[^1] >= e21_4[^1]) return new("W", 0, new() { "! 4H상승" }, MakeDetail(0, 0, 0, 0, 0, 0, 0));
            score += 15; h4Score = 15; h4Trend = "하락";

            if (c1hc[^1] < e21h[^1] && c1hc[^1] < e50h[^1]) { h1Score = 15; score += 15; reasons.Add("+ 1H약세"); h1Trend = "약세"; }
            else if (c1hc[^1] < e21h[^1]) { h1Score = 8; score += 8; reasons.Add("+ 1H하락"); h1Trend = "하락"; }
            else { h1Score = -10; score -= 10; h1Trend = "강세"; }

            if (rsiVal < 30) return new("W", 0, new() { $"! RSI{(int)rsiVal}" }, MakeDetail(0, 0, 0, 0, 0, h1Score, h4Score));
            if (rsiVal > 55 && rsiVal < 70)  { rsiScore = 10;  score += 10; reasons.Add($"+ RSI{(int)rsiVal}"); }
            else if (rsiVal > 45)            { rsiScore = 5;   score += 5; }
            else if (rsiVal > 35)            { rsiScore = -5;  score -= 5;  reasons.Add($"- RSI과냉({(int)rsiVal})"); }
            else                             { rsiScore = -10; score -= 10; reasons.Add($"- RSI과냉({(int)rsiVal})"); }

            if (macdH < 0 && macdH < macdHPrev) { macdScore = 10; score += 10; reasons.Add("+ MACD↓"); }
            else if (macdH < macdHPrev) { macdScore = 5; score += 5; }
            else if (macdH > 0 && macdH > macdHPrev) { macdScore = -10; score -= 10; }

            if (adxVal > 25 && minusDi > plusDi) { adxScore = 10; score += 10; reasons.Add($"+ ADX{(int)adxVal}"); }
            else if (adxVal < 20) { adxScore = -5; score -= 5; }

            if (vols.Length >= 21)
            {
                volScore = (int)Math.Clamp((volRatio - 1.0) * 10, -8, 8);
                score += volScore;
                if (volScore >= 5) reasons.Add("+ 거래량↑");
            }

            if (c15[^1].Close < c15[^1].Open) { candleScore = 5; score += 5; } else { candleScore = -3; score -= 3; }

            // EMA7 이격도
            double emaDevShort = e7[^1] > 0 ? (e7[^1] - cur) / e7[^1] * 100 : 0;
            if      (emaDevShort > 3.0)                          { score -= 12; reasons.Add($"- 이격과다 (-{emaDevShort:F1}%)"); }
            else if (emaDevShort > 1.5)                          { score -= 6; }
            else if (emaDevShort >= -0.5 && emaDevShort <= 0.5)  { score += 8;  reasons.Add($"+ EMA근접 ({emaDevShort:F1}%)"); }
            else if (emaDevShort < -1.5)                         { score -= 6;  reasons.Add($"- EMA상회 ({emaDevShort:F1}%)"); }
            // 0.5~1.5%: 중립 (0점) / -1.5~-0.5%: 중립 (0점)

            reasons.Add($"score:{score}");
            var detail = MakeDetail(rsiScore, macdScore, adxScore, volScore, candleScore, h1Score, h4Score);
            return score >= 45 ? new("S", score, reasons, detail) : new("W", score, reasons, detail);
        }
    }

    public static bool ConfirmSignal(List<Candle> c15, string sigType, double sigPrice)
    {
        if (c15.Count < 3) return false;
        double cur = c15[^1].Close;
        if (sigType == "L")
        {
            if (cur > sigPrice * 1.001 && c15[^1].Close > c15[^1].Open) return true;
            if (cur > sigPrice * 1.001 && c15[^2].Close > c15[^2].Open && cur > c15[^2].Close) return true;
        }
        else if (sigType == "S")
        {
            if (cur < sigPrice * 0.999 && c15[^1].Close < c15[^1].Open) return true;
            if (cur < sigPrice * 0.999 && c15[^2].Close < c15[^2].Open && cur < c15[^2].Close) return true;
        }
        return false;
    }

    /// <summary>봇 시작 시 사전분석: 최근 8봉 내 EMA 크로스 탐색</summary>
    public static SignalResult PreAnalyze(List<Candle> c15, List<Candle> c1h, List<Candle> c4h)
        => CheckEmaSignal(c15, c1h, c4h, lookback: 8);

    /// <summary>EMA 근접도 분석: 스캐너용</summary>
    public static (double emaGapPct, bool shrinking, int crossCount) GetEmaProximity(List<Candle> c15)
    {
        if (c15.Count < 100) return (99, false, 0);

        var closes = c15.Select(c => c.Close).ToArray();
        var e7 = Indicators.Ema(closes, 7);
        var e21 = Indicators.Ema(closes, 21);

        // EMA 갭 %
        double gap = e21[^1] != 0 ? (e7[^1] - e21[^1]) / e21[^1] * 100 : 99;

        // 수렴 중인지 확인 (최근 5봉에서 갭이 줄어드는 추세)
        bool shrinking = false;
        if (closes.Length >= 6)
        {
            double gapNow = Math.Abs(e7[^1] - e21[^1]);
            double gap5Ago = Math.Abs(e7[^6] - e21[^6]);
            shrinking = gapNow < gap5Ago * 0.8; // 20% 이상 줄어들면 수렴
        }

        // 크로스 횟수 (50봉 내)
        int crossCount = 0;
        for (int i = closes.Length - 1; i >= Math.Max(1, closes.Length - 50); i--)
        {
            if ((e7[i] > e21[i] && e7[i - 1] <= e21[i - 1]) ||
                (e7[i] < e21[i] && e7[i - 1] >= e21[i - 1]))
                crossCount++;
        }

        return (Math.Round(gap, 3), shrinking, crossCount);
    }

    public static (double sl, double tp, double atr) CalcSlTp(List<Candle> c15)
    {
        double atrVal = Indicators.Atr(c15);
        double price = c15[^1].Close;
        if (price == 0 || atrVal == 0) return (2.5, 5.0, 0);
        double sl = (atrVal * 3.0 / price) * 100;
        double tp = (atrVal * 6.0 / price) * 100;
        sl = Math.Clamp(sl, 1.8, 5.5);
        tp = Math.Clamp(tp, 3.5, 12.0);
        if (tp < sl * 2.0) tp = sl * 2.0;
        return (Math.Round(sl, 2), Math.Round(tp, 2), Math.Round(atrVal, 2));
    }
}
