namespace BtcTradingBot.Services;

/// <summary>
/// EMA를 O(1)로 업데이트하는 상태 객체.
/// 매 틱마다 전체 재계산하지 않고, 캔들 확정 시 1스텝만 계산한다.
/// </summary>
public class EmaState
{
    public int Period { get; }
    public double Alpha { get; }
    public double Value { get; private set; }
    public bool IsInitialized { get; private set; }

    private readonly List<double> _seedValues = new();

    public EmaState(int period)
    {
        Period = period;
        Alpha = 2.0 / (period + 1);
    }

    /// <summary>
    /// 캔들 close 값으로 EMA를 1스텝 업데이트한다.
    /// 초기 Period 개까지는 SMA seed로 사용한다.
    /// </summary>
    public double Update(double close)
    {
        if (!IsInitialized)
        {
            _seedValues.Add(close);
            if (_seedValues.Count >= Period)
            {
                Value = _seedValues.Average();
                IsInitialized = true;
                _seedValues.Clear();
            }
            else
            {
                Value = _seedValues.Average();
            }
            return Value;
        }

        Value = Alpha * close + (1 - Alpha) * Value;
        return Value;
    }

    /// <summary>진행중 캔들의 임시 EMA (상태를 변경하지 않음)</summary>
    public double Peek(double close)
    {
        if (!IsInitialized) return close;
        return Alpha * close + (1 - Alpha) * Value;
    }

    public void Reset()
    {
        Value = 0;
        IsInitialized = false;
        _seedValues.Clear();
    }
}

/// <summary>EMA 7/21/50 세트를 관리</summary>
public class IndicatorSet
{
    public EmaState Ema7 { get; } = new(7);
    public EmaState Ema21 { get; } = new(21);
    public EmaState Ema50 { get; } = new(50);

    /// <summary>캔들 확정 시 세 EMA 모두 업데이트</summary>
    public (double e7, double e21, double e50) Update(double close)
    {
        return (Ema7.Update(close), Ema21.Update(close), Ema50.Update(close));
    }

    /// <summary>진행중 캔들의 임시 EMA (상태 불변)</summary>
    public (double e7, double e21, double e50) Peek(double close)
    {
        return (Ema7.Peek(close), Ema21.Peek(close), Ema50.Peek(close));
    }

    /// <summary>히스토리 캔들 배열로 초기화</summary>
    public void Initialize(IList<Models.Candle> candles)
    {
        Reset();
        foreach (var c in candles)
            Update(c.Close);
    }

    public void Reset()
    {
        Ema7.Reset();
        Ema21.Reset();
        Ema50.Reset();
    }
}
