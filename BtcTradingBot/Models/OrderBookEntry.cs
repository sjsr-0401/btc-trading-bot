namespace BtcTradingBot.Models;

public record OrderBookEntry(double Price, double Qty);

public record OrderBookData(List<OrderBookEntry> Bids, List<OrderBookEntry> Asks);
