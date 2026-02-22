namespace BtcTradingBot.Models;

public record Candle(
    long OpenTime,
    double Open,
    double High,
    double Low,
    double Close,
    double Volume,
    double QuoteVolume,
    double TakerBuy
);
