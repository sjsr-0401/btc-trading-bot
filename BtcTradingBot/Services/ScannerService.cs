using BtcTradingBot.Models;

namespace BtcTradingBot.Services;

public class ScannerService
{
    /// <summary>상위 코인들을 스캔하여 진입 준비도 분석</summary>
    public async Task<List<CoinScanResult>> ScanAsync(
        List<SymbolInfo> symbols,
        CancellationToken ct,
        Action<int, int>? progress = null)
    {
        var results = new List<CoinScanResult>();
        using var api = new BinanceApi("", ""); // 읽기 전용 (공개 API)

        // 24h 티커 한 번에 조회 (weight ~40, 코인별 호출보다 효율적)
        Dictionary<string, double> change24h = new();
        Dictionary<string, double> vol24h = new();
        try
        {
            var tickers = await api.GetTicker24h();
            if (tickers.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var t in tickers.EnumerateArray())
                {
                    var sym2 = t.GetProperty("symbol").GetString();
                    if (sym2 == null) continue;
                    if (double.TryParse(
                        t.GetProperty("priceChangePercent").GetString(),
                        System.Globalization.CultureInfo.InvariantCulture, out double pct))
                        change24h[sym2] = pct;
                    if (double.TryParse(
                        t.GetProperty("quoteVolume").GetString(),
                        System.Globalization.CultureInfo.InvariantCulture, out double qv))
                        vol24h[sym2] = qv;
                }
            }
        }
        catch { /* 24h 데이터 없어도 스캔 계속 */ }

        int current = 0;
        foreach (var sym in symbols)
        {
            ct.ThrowIfCancellationRequested();
            current++;
            progress?.Invoke(current, symbols.Count);

            try
            {
                // 3개 타임프레임 데이터 수집 (150봉 = 37.5시간, EMA 안정성 향상)
                var c15 = await api.GetKlines(sym.Symbol, "15m", 150);
                await Task.Delay(70, ct);
                var c1h = await api.GetKlines(sym.Symbol, "1h", 50);
                await Task.Delay(70, ct);
                var c4h = await api.GetKlines(sym.Symbol, "4h", 50);
                await Task.Delay(70, ct);

                if (c15 == null || c1h == null || c4h == null || c15.Count < 100)
                {
                    results.Add(CreateEmptyResult(sym, "데이터 부족"));
                    continue;
                }

                // KYJ 엔진 분석 (lookback: 16 = 최근 4시간, phantom signal 방지)
                var signal = Strategy.CheckEmaSignal(c15, c1h, c4h, lookback: 16);

                // EMA 근접도
                var (emaGapPct, shrinking, crossCount) = Strategy.GetEmaProximity(c15);

                // 추가 지표
                var closes = c15.Select(c => c.Close).ToArray();
                double rsi = Indicators.Rsi(closes);
                var (adx, _, _) = Indicators.Adx(c1h);
                double volRatio = 0;
                var vols = c15.TakeLast(21).Select(c => c.Volume).ToArray();
                if (vols.Length >= 21)
                {
                    double av = vols.Take(20).Average();
                    volRatio = av > 0 ? vols[^1] / av : 0;
                }

                // 24h 데이터
                change24h.TryGetValue(sym.Symbol, out double pctChange);
                vol24h.TryGetValue(sym.Symbol, out double quoteVol);

                // 시장 상태 판단
                string marketState = DetermineMarketState(adx, crossCount, shrinking);

                // 준비도 점수 계산
                int readiness = CalcReadiness(signal, emaGapPct, shrinking, adx, crossCount);

                results.Add(new CoinScanResult
                {
                    Symbol = sym,
                    Signal = signal,
                    ReadinessScore = readiness,
                    EmaGapPct = emaGapPct,
                    EmaGapShrinking = shrinking,
                    CrossCount = crossCount,
                    MarketState = marketState,
                    Rsi = rsi,
                    Adx = adx,
                    VolumeRatio = volRatio,
                    PriceChange24h = pctChange,
                    Volume24hUsdt = quoteVol,
                });
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                results.Add(CreateEmptyResult(sym, "분석 실패"));
            }
        }

        // ReadinessScore 내림차순 정렬
        results.Sort((a, b) => b.ReadinessScore.CompareTo(a.ReadinessScore));
        return results;
    }

    private static int CalcReadiness(SignalResult signal, double emaGapPct, bool shrinking, double adx, int crossCount)
    {
        // 신호가 있으면 (L/S): signal.Score 기반
        if (signal.Direction is "L" or "S")
            return Math.Clamp(signal.Score, 0, 100);

        // 대기 (W): EMA 갭 근접도 + 추세 정렬 + ADX 보너스로 0-40 범위
        int score = 0;

        // EMA 갭 근접도 (0-20점): 갭이 작을수록 크로스에 가까움
        double absGap = Math.Abs(emaGapPct);
        if (absGap < 0.05) score += 20;
        else if (absGap < 0.1) score += 16;
        else if (absGap < 0.2) score += 12;
        else if (absGap < 0.5) score += 8;
        else if (absGap < 1.0) score += 4;

        // 수렴 보너스 (0-10점)
        if (shrinking) score += 10;

        // ADX 보너스 (0-8점): 추세 강도가 적당하면 보너스
        if (adx >= 20 && adx <= 35) score += 8;
        else if (adx >= 15) score += 4;

        // 크로스 빈도 감점: 너무 많으면 횡보장
        if (crossCount >= 4) score -= 10;
        else if (crossCount >= 3) score -= 5;

        return Math.Clamp(score, 0, 40);
    }

    private static string DetermineMarketState(double adx, int crossCount, bool shrinking)
    {
        if (crossCount >= 4) return "횡보";
        if (shrinking && adx < 20) return "압축";
        if (adx >= 25) return "추세";
        return "횡보";
    }

    private static CoinScanResult CreateEmptyResult(SymbolInfo sym, string reason) => new()
    {
        Symbol = sym,
        Signal = new("W", 0, new() { reason }),
        ReadinessScore = 0,
        MarketState = "--",
    };
}
