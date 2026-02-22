using BtcTradingBot.Models;

namespace BtcTradingBot.Services;

public enum TimeFrame
{
    M1 = 1,
    M5 = 5,
    M15 = 15,
    H1 = 60,
    H4 = 240,
    D1 = 1440,
}

/// <summary>
/// 틱을 OHLCV 캔들로 집계한다.
/// 타임프레임 bucket 경계에서 캔들을 확정하고 새 캔들을 생성한다.
/// </summary>
public class CandleAggregator
{
    private TimeFrame _tf;
    private long _currentBucketMs;
    private double _open, _high, _low, _close, _volume, _quoteVolume, _takerBuy;

    public Candle? CurrentCandle { get; private set; }
    public bool HasCandle => CurrentCandle != null;

    public CandleAggregator(TimeFrame tf)
    {
        _tf = tf;
    }

    public void SetTimeFrame(TimeFrame tf)
    {
        _tf = tf;
        Reset();
    }

    public void Reset()
    {
        _currentBucketMs = 0;
        CurrentCandle = null;
    }

    /// <summary>
    /// 히스토리 캔들의 마지막 bucket을 기준으로 초기화한다.
    /// REST로 로드한 캔들의 마지막 것이 "진행중" 캔들이 된다.
    /// </summary>
    public void InitFromHistory(List<Candle> candles)
    {
        if (candles.Count == 0) return;
        // 마지막 캔들을 현재 진행중으로 설정
        // bucket은 0으로 두고 첫 틱에서 갱신
        _currentBucketMs = 0;
    }

    /// <summary>
    /// 틱을 받아 캔들을 업데이트한다.
    /// 반환: (업데이트된 캔들, 새 캔들인지, 이전 캔들이 확정되었는지)
    /// </summary>
    public CandleUpdate? Update(PriceTick tick)
    {
        long bucketMs = FloorToBucket(tick.Timestamp);

        if (_currentBucketMs == 0)
        {
            // 첫 틱
            _currentBucketMs = bucketMs;
            _open = _high = _low = _close = tick.Price;
            _volume = tick.Quantity;
            _quoteVolume = tick.Price * tick.Quantity;
            _takerBuy = tick.Quantity;
            CurrentCandle = MakeCandle();
            return new CandleUpdate(CurrentCandle, -1, true, false);
        }

        if (bucketMs == _currentBucketMs)
        {
            // 같은 bucket — H/L/C/V 갱신
            if (tick.Price > _high) _high = tick.Price;
            if (tick.Price < _low) _low = tick.Price;
            _close = tick.Price;
            _volume += tick.Quantity;
            _quoteVolume += tick.Price * tick.Quantity;
            CurrentCandle = MakeCandle();
            return new CandleUpdate(CurrentCandle, -1, false, false);
        }

        // 새 bucket — 이전 캔들 확정, 새 캔들 시작
        _currentBucketMs = bucketMs;
        _open = _high = _low = _close = tick.Price;
        _volume = tick.Quantity;
        _quoteVolume = tick.Price * tick.Quantity;
        _takerBuy = tick.Quantity;
        CurrentCandle = MakeCandle();

        return new CandleUpdate(CurrentCandle, -1, true, true);
    }

    /// <summary>이전 확정 캔들을 반환 (bucket 전환 직전 상태)</summary>
    public Candle GetClosedCandle() => new(_currentBucketMs, _open, _high, _low, _close, _volume, _quoteVolume, _takerBuy);

    private Candle MakeCandle() => new(_currentBucketMs, _open, _high, _low, _close, _volume, _quoteVolume, _takerBuy);

    private long FloorToBucket(long timestampMs)
    {
        long intervalMs = (long)_tf * 60 * 1000;
        return timestampMs / intervalMs * intervalMs;
    }
}
