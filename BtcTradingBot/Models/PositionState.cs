namespace BtcTradingBot.Models;

public class PositionState
{
    public string Symbol { get; set; } = "";
    public string Side { get; set; } = "N";   // "L"=Long, "S"=Short, "N"=None
    public double EntryPrice { get; set; }
    public double Amount { get; set; }
    public double StopPrice { get; set; }
    public double TpPrice { get; set; }
    public double PeakPrice { get; set; }      // 하위 호환용 (deprecated)
    public double Highest { get; set; }        // Python: p.highest (Long 트레일링용)
    public double Lowest { get; set; }         // Python: p.lowest (Short 트레일링용)
    public string StrategyTag { get; set; } = ""; // "DonchianBreakout" or "BBReversal"
    public DateTime OpenTime { get; set; }
    public double EntryFee { get; set; }
    public double MarginUsed { get; set; }     // 사용 마진 (USDT)

    // ensure_brackets: SL/TP Algo 주문 ID 추적
    public long SlAlgoId { get; set; }  // 0 = 주문 없음
    public long TpAlgoId { get; set; }  // 0 = 주문 없음

    public bool HasExchangeSl => SlAlgoId > 0;
    public bool HasExchangeTp => TpAlgoId > 0;

    public bool IsOpen => Side != "N";
}
