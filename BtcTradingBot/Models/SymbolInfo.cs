namespace BtcTradingBot.Models;

public record SymbolInfo(
    string Symbol,
    string BaseAsset,
    double Price,
    double Volume24hUsdt,
    int PricePrecision,
    int QuantityPrecision,
    double MinNotional
)
{
    public string DisplayName => $"{BaseAsset} / USDT";

    public string VolumeText => Volume24hUsdt >= 1_000_000_000
        ? $"${Volume24hUsdt / 1_000_000_000:F1}B"
        : $"${Volume24hUsdt / 1_000_000:F0}M";
}
