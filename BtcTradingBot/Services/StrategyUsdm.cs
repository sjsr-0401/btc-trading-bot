using BtcTradingBot.Models;

namespace BtcTradingBot.Services;

/// <summary>
/// USDM 전략 엔진: ADX 레짐 분기 → Donchian Breakout (추세) / BB Mean Reversion (횡보)
/// Python binance_usdm_bot/strategies.py 이식
/// </summary>
public static class StrategyUsdm
{
    // === 레짐 파라미터 ===
    public const double AdxTrendMin = 22;
    public const double AdxRangeMax = 18;

    // === 추세 전략 파라미터 ===
    public const int DonchianWindow = 20;
    public const int EmaFast = 50;
    public const int EmaSlow = 200;
    public const int AtrLen = 14;
    public const double StopAtrMultTrend = 2.8;
    public const double TpRTrend = 2.2;
    public const double TrailAtrMultTrend = 2.8;

    // === 횡보 전략 파라미터 ===
    public const int RsiLen = 14;
    public const double RsiLow = 30;
    public const double RsiHigh = 70;
    public const int BbLen = 20;
    public const double BbStd = 2.0;
    public const double StopAtrMultMr = 2.2;
    public const double TpRMr = 1.4;

    /// <summary>USDM 신호 분석: ADX 레짐 → 전략 분기 (마지막 확정 캔들 기준, Python 동일)</summary>
    public static UsdmSignalResult Analyze(List<Candle> candles)
    {
        if (candles.Count < EmaSlow + 10)
            return UsdmSignalResult.Wait("데이터 부족");

        var closes = candles.Select(c => c.Close).ToArray();

        // Python: signal_at(feat, len(feat)-2) — 마지막 확정 캔들 사용
        // closes[^1]은 현재 미완성 봉이므로, closes[^2]가 마지막 확정 캔들
        if (closes.Length < 3) return UsdmSignalResult.Wait("데이터 부족");
        double cp = closes[^2]; // 마지막 확정 캔들의 close

        // 지표 계산
        var (adx, _, _) = IndicatorsUsdm.AdxValue(candles);
        double rsi = IndicatorsUsdm.RsiValue(closes, RsiLen);
        double atr = IndicatorsUsdm.AtrValue(candles, AtrLen);
        // Python: prev["don_hi"] — 신호 캔들 이전 봉까지의 Donchian
        var (dcHigh, dcLow) = IndicatorsUsdm.DonchianChannel(candles, DonchianWindow);
        var ema50 = IndicatorsUsdm.Ema(closes, EmaFast);
        var ema200 = IndicatorsUsdm.Ema(closes, EmaSlow);
        var (bbSma, bbUpper, bbLower) = IndicatorsUsdm.BollingerBands(closes, BbLen, BbStd);

        if (atr <= 0 || cp <= 0)
            return UsdmSignalResult.Wait("ATR/가격 이상");

        // 지표 배열 길이 검증 (방어)
        if (ema50.Length < 2 || ema200.Length < 2 || bbUpper.Length < 2 || bbLower.Length < 2)
            return UsdmSignalResult.Wait("지표 계산 실패");

        // 신호 캔들에 해당하는 EMA/BB 값 (^2 = 끝에서 두 번째)
        double ema50Val = ema50[^2];
        double ema200Val = ema200[^2];
        double bbUpperVal = bbUpper[^2];
        double bbSmaVal = bbSma[^2];
        double bbLowerVal = bbLower[^2];

        // 레짐 판정
        string regime;
        if (adx >= AdxTrendMin) regime = "Trend";
        else if (adx <= AdxRangeMax) regime = "Range";
        else regime = "Neutral";

        var detail = new UsdmSignalDetail(
            regime, adx, rsi,
            dcHigh, dcLow,
            bbUpperVal, bbSmaVal, bbLowerVal,
            ema50Val, ema200Val,
            atr, 0, 0, "");

        // === 추세 전략: Donchian Breakout (Python _trend_signal) ===
        if (regime == "Trend")
        {
            // Python: up_trend = ema_fast > ema_slow (DI 필터 없음)
            bool upTrend = ema50Val > ema200Val;
            bool downTrend = ema50Val < ema200Val;

            if (upTrend && cp > dcHigh)
            {
                double sl = cp - atr * StopAtrMultTrend;
                double stopDist = cp - sl;
                double tp = cp + stopDist * TpRTrend;
                var d = detail with { StopPrice = sl, TpPrice = tp, StrategyTag = "DonchianBreakout" };
                return new UsdmSignalResult("L", atr * StopAtrMultTrend, sl, tp,
                    "DonchianBreakout", d,
                    new() { $"Donchian돌파(H:{dcHigh:N2})", $"EMA50>200", $"ADX:{adx:F1}" });
            }

            if (downTrend && cp < dcLow)
            {
                double sl = cp + atr * StopAtrMultTrend;
                double stopDist = sl - cp;
                double tp = cp - stopDist * TpRTrend;
                var d = detail with { StopPrice = sl, TpPrice = tp, StrategyTag = "DonchianBreakout" };
                return new UsdmSignalResult("S", atr * StopAtrMultTrend, sl, tp,
                    "DonchianBreakout", d,
                    new() { $"Donchian이탈(L:{dcLow:N2})", $"EMA50<200", $"ADX:{adx:F1}" });
            }

            return UsdmSignalResult.WithDetail("Trend", detail, "추세 대기 (돌파 미발생)");
        }

        // === 횡보 전략: 비활성화 (추세추종 전용 모드) ===
        if (regime == "Range")
        {
            return UsdmSignalResult.WithDetail("Range", detail, "횡보 레짐 — 진입 안함");
        }

        // Neutral (ADX 18~22): 신호 없음
        return UsdmSignalResult.WithDetail("Neutral", detail, $"중립 (ADX:{adx:F1})");
    }

    // Python: trail_update_min_gap = 0.001 (0.1%)
    public const double TrailUpdateMinGap = 0.001;

    /// <summary>
    /// 트레일링 스탑 업데이트 (Breakeven + Wider Trail)
    /// - trail이 진입가를 넘어야만 SL 갱신 (노이즈 구간 보호)
    /// - TrailAtrMultTrend = 2.8 (초기 SL과 동일한 여유)
    /// </summary>
    public static double? UpdateTrailingStop(PositionState pos, double high, double low, double atr)
    {
        if (pos.StrategyTag != "DonchianBreakout") return null;
        if (atr <= 0 || pos.EntryPrice <= 0) return null;

        double trailDist = atr * TrailAtrMultTrend;

        if (pos.Side == "L")
        {
            pos.Highest = Math.Max(pos.Highest, high);
            double trail = pos.Highest - trailDist;

            // Breakeven 조건: trail이 진입가 이상일 때만 갱신
            if (trail <= pos.EntryPrice) return null;

            if (trail > pos.StopPrice * (1 + TrailUpdateMinGap))
            {
                double newSl = Math.Max(pos.StopPrice, trail);
                return newSl;
            }
        }
        else if (pos.Side == "S")
        {
            pos.Lowest = Math.Min(pos.Lowest, low);
            double trail = pos.Lowest + trailDist;

            // Breakeven 조건: trail이 진입가 이하일 때만 갱신
            if (trail >= pos.EntryPrice) return null;

            if (trail < pos.StopPrice * (1 - TrailUpdateMinGap))
            {
                double newSl = Math.Min(pos.StopPrice, trail);
                return newSl;
            }
        }

        return null;
    }
}

/// <summary>USDM 분석 결과</summary>
public class UsdmSignalResult
{
    public string Direction { get; init; } = "W";   // "L", "S", "W"
    public double StopDistance { get; init; }         // SL까지 가격 차이
    public double StopPrice { get; init; }
    public double TpPrice { get; init; }
    public string StrategyTag { get; init; } = "";
    public UsdmSignalDetail? Detail { get; init; }
    public List<string> Reasons { get; init; } = new();

    public UsdmSignalResult() { }

    public UsdmSignalResult(string direction, double stopDist, double sl, double tp,
        string tag, UsdmSignalDetail detail, List<string> reasons)
    {
        Direction = direction;
        StopDistance = stopDist;
        StopPrice = sl;
        TpPrice = tp;
        StrategyTag = tag;
        Detail = detail;
        Reasons = reasons;
    }

    public static UsdmSignalResult Wait(string reason) =>
        new() { Reasons = new() { reason } };

    public static UsdmSignalResult WithDetail(string regime, UsdmSignalDetail detail, string reason) =>
        new() { Detail = detail, Reasons = new() { reason } };
}
