using BtcTradingBot.Models;

namespace BtcTradingBot.Services;

/// <summary>
/// USDM 종목 셀렉터: 유동성 + 변동성 스코어링으로 top_n 종목 선택
/// Python binance_usdm_bot/selector.py 이식
/// </summary>
public class SymbolSelector
{
    public int TopN { get; set; } = 10;
    public int Candidates { get; set; } = 40;
    public double MaxAbsFunding { get; set; } = 0.002;
    public int RefreshIntervalSec { get; set; } = 21600; // 6h
    public double WVolume { get; set; } = 0.6;
    public double WVolatility { get; set; } = 0.4;
    public int VolatilityLookbackBars { get; set; } = 288; // 5m × 288 = 24h

    private DateTime _lastRefresh = DateTime.MinValue;
    public List<SelectedSymbol> CurrentSymbols { get; private set; } = new();

    /// <summary>갱신 필요 여부</summary>
    public bool NeedsRefresh => (DateTime.UtcNow - _lastRefresh).TotalSeconds >= RefreshIntervalSec;

    /// <summary>
    /// 종목 선택: 거래대금 상위 candidates개에서 스코어링 후 top_n 반환
    /// </summary>
    public async Task<List<SelectedSymbol>> SelectSymbols(BinanceApi api)
    {
        // 거래대금 상위 후보 조회
        var allSymbols = await api.GetTopSymbols(Candidates);
        if (allSymbols.Count == 0) return CurrentSymbols;

        // Python 동일: 펀딩비 필터 (극단적 펀딩 제외)
        var fundingRates = await api.GetFundingRates();

        var scored = new List<(SymbolInfo symbol, double score, double volume, double atrPct)>();

        foreach (var sym in allSymbols)
        {
            // 펀딩비 필터: abs(rate) > 0.2% 제외
            if (fundingRates.TryGetValue(sym.Symbol, out var rate) && Math.Abs(rate) > MaxAbsFunding)
                continue;

            try
            {
                // 5분봉 데이터로 변동성 계산
                var candles = await api.GetKlines(sym.Symbol, "5m", VolatilityLookbackBars);
                if (candles == null || candles.Count < 50) continue;

                // 거래대금 (24h 추정: 최근 288봉의 volume × close 합)
                double quoteVol = candles.Sum(c => c.Volume * c.Close);

                // ATR% (변동성)
                double atr = Indicators.Atr(candles, 14);
                double lastClose = candles[^1].Close;
                double atrPct = lastClose > 0 ? atr / lastClose : 0;

                scored.Add((sym, 0, quoteVol, atrPct));
            }
            catch
            {
                continue; // 데이터 로드 실패 시 스킵
            }
        }

        if (scored.Count == 0) return CurrentSymbols;

        // Python 동일: log1p + min-max 정규화
        var logVols = scored.Select(s => Math.Log(1 + s.volume)).ToArray();
        var atrPcts = scored.Select(s => s.atrPct).ToArray();
        var volNorms = MinMaxScale(logVols);
        var atrNorms = MinMaxScale(atrPcts);

        var result = scored
            .Select((s, idx) =>
            {
                double score = WVolume * volNorms[idx] + WVolatility * atrNorms[idx];
                return new SelectedSymbol(s.symbol, score, s.volume, s.atrPct);
            })
            .OrderByDescending(s => s.Score)
            .Take(TopN)
            .ToList();

        CurrentSymbols = result;
        _lastRefresh = DateTime.UtcNow;
        return result;
    }

    /// <summary>기존 종목 유지 (갱신 불필요 시)</summary>
    public List<SelectedSymbol> GetCurrent() => CurrentSymbols;

    /// <summary>Python _minmax: min-max 정규화</summary>
    private static double[] MinMaxScale(double[] arr)
    {
        if (arr.Length == 0) return arr;
        double mn = arr.Min(), mx = arr.Max();
        if (mx - mn < 1e-12) return new double[arr.Length]; // all zeros
        return arr.Select(x => (x - mn) / (mx - mn)).ToArray();
    }
}

/// <summary>선택된 종목 + 스코어 정보</summary>
public record SelectedSymbol(
    SymbolInfo Symbol,
    double Score,
    double QuoteVolume24h,
    double AtrPercent
);
