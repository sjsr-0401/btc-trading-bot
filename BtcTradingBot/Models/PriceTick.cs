namespace BtcTradingBot.Models;

public record PriceTick(long Timestamp, double Price, double Quantity, string Source);
