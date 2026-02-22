namespace BtcTradingBot.Models;

public class CoinScanResult
{
    public required SymbolInfo Symbol { get; init; }
    public required SignalResult Signal { get; init; }
    public int ReadinessScore { get; init; }        // 0-100
    public double EmaGapPct { get; init; }          // (EMA7-EMA21)/EMA21 * 100
    public bool EmaGapShrinking { get; init; }      // EMA 수렴 중
    public int CrossCount { get; init; }            // 50봉 내 크로스 횟수
    public string MarketState { get; init; } = "";  // "추세" / "횡보" / "압축"
    public double Rsi { get; init; }
    public double Adx { get; init; }
    public double VolumeRatio { get; init; }
    public double PriceChange24h { get; init; }   // 24시간 변동률 %
    public double Volume24hUsdt { get; init; }   // 24시간 USDT 거래량

    // === Display helpers ===

    public string DirectionText => Signal.Direction switch
    {
        "L" => "LONG",
        "S" => "SHORT",
        _ => "대기"
    };

    public string DirectionColor => Signal.Direction switch
    {
        "L" => "#22C55E",
        "S" => "#EF4444",
        _ => "#888888"
    };

    public string ReadinessColor => ReadinessScore switch
    {
        >= 70 => "#22C55E",
        >= 45 => "#F59E0B",
        >= 25 => "#3B82F6",
        _ => "#888888"
    };

    public string EmaGapText => $"{(EmaGapPct >= 0 ? "+" : "")}{EmaGapPct:F2}%";

    public string PriceText
    {
        get
        {
            double p = Symbol.Price;
            if (p >= 1000) return $"${p:N0}";
            if (p >= 1) return $"${p:N2}";
            return $"${p:N4}";
        }
    }

    public string RsiText => $"{Rsi:F0}";
    public string AdxText => $"{Adx:F0}";
    public string VolumeText => $"x{VolumeRatio:F1}";
    public string Volume24hText => Volume24hUsdt switch
    {
        >= 1_000_000_000 => $"{Volume24hUsdt / 1_000_000_000:F1}B",
        >= 1_000_000 => $"{Volume24hUsdt / 1_000_000:F0}M",
        >= 1_000 => $"{Volume24hUsdt / 1_000:F0}K",
        _ => $"{Volume24hUsdt:F0}"
    };
    public string ConvergenceText => EmaGapShrinking ? "수렴" : "";
    public string Change24hText => $"{(PriceChange24h >= 0 ? "+" : "")}{PriceChange24h:F1}%";
    public string Change24hColor => PriceChange24h >= 0 ? "#22C55E" : "#EF4444";
}
