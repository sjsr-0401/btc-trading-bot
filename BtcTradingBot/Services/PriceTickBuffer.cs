using BtcTradingBot.Models;

namespace BtcTradingBot.Services;

/// <summary>
/// WebSocket 스레드에서 기록하고 UI 스레드(100ms)에서 읽는 스레드-안전 버퍼.
/// volatile로 최신 틱만 유지하여 GC 압박을 최소화한다.
/// </summary>
public class PriceTickBuffer
{
    private volatile PriceTick? _latest;

    public void Push(PriceTick tick) => _latest = tick;

    public PriceTick? TakeLatest()
    {
        var tick = _latest;
        _latest = null;
        return tick;
    }

    public PriceTick? Peek() => _latest;

    public void Clear() => _latest = null;
}
