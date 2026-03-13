namespace BtcTradingBot.Services;

public static class PriceHelper
{
    /// <summary>가격값을 적절한 소수점 자릿수로 포맷 (예: 98000 → "98,000", 0.3785 → "0.3785", 0.000123 → "0.000123")</summary>
    public static string Format(double price)
    {
        if (price >= 10000) return price.ToString("N0");
        if (price >= 100)   return price.ToString("N2");
        if (price >= 1)     return price.ToString("N4");
        if (price >= 0.01)  return price.ToString("N5");
        return price.ToString("N6");
    }
}
