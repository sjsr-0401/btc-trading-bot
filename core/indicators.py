"""기술적 지표 계산 모듈"""


def ema(data: list, period: int) -> list:
    if len(data) < period:
        return data[:]
    multiplier = 2.0 / (period + 1)
    result = [sum(data[:period]) / period]
    for i in range(period, len(data)):
        result.append(data[i] * multiplier + result[-1] * (1 - multiplier))
    return [result[0]] * period + result[1:]


def rsi(closes: list, period: int = 14) -> float:
    if len(closes) < period + 1:
        return 50.0
    deltas = [closes[i] - closes[i - 1] for i in range(1, len(closes))]
    gains = [x if x > 0 else 0 for x in deltas]
    losses = [-x if x < 0 else 0 for x in deltas]
    avg_gain = sum(gains[:period]) / period
    avg_loss = sum(losses[:period]) / period
    for i in range(period, len(deltas)):
        avg_gain = (avg_gain * (period - 1) + gains[i]) / period
        avg_loss = (avg_loss * (period - 1) + losses[i]) / period
    if avg_loss == 0:
        return 100.0
    return 100.0 - (100.0 / (1 + avg_gain / avg_loss))


def macd_histogram(closes: list) -> list:
    e12 = ema(closes, 12)
    e26 = ema(closes, 26)
    macd_line = [e12[i] - e26[i] for i in range(len(closes))]
    signal = ema(macd_line, 9)
    return [macd_line[i] - signal[i] for i in range(len(closes))]


def atr(candles: list, period: int = 14) -> float:
    if len(candles) < period + 1:
        return 0.0
    tr_list = []
    for i in range(1, len(candles)):
        h = candles[i]["h"]
        l = candles[i]["l"]
        pc = candles[i - 1]["c"]
        tr_list.append(max(h - l, abs(h - pc), abs(l - pc)))
    if len(tr_list) >= period:
        return sum(tr_list[-period:]) / period
    return sum(tr_list) / max(len(tr_list), 1)


def adx(candles: list, period: int = 14):
    """ADX, +DI, -DI 반환"""
    if len(candles) < period * 2:
        return 20.0, 0.0, 0.0
    plus_dm = []
    minus_dm = []
    tr_list = []
    for i in range(1, len(candles)):
        h = candles[i]["h"]
        l = candles[i]["l"]
        ph = candles[i - 1]["h"]
        pl = candles[i - 1]["l"]
        pc = candles[i - 1]["c"]
        pd = h - ph
        md = pl - l
        plus_dm.append(pd if pd > md and pd > 0 else 0)
        minus_dm.append(md if md > pd and md > 0 else 0)
        tr_list.append(max(h - l, abs(h - pc), abs(l - pc)))
    avg_tr = sum(tr_list[:period]) / period
    plus_smooth = sum(plus_dm[:period]) / period
    minus_smooth = sum(minus_dm[:period]) / period
    dx_list = []
    pdi = 0.0
    mdi = 0.0
    for i in range(period, len(tr_list)):
        avg_tr = (avg_tr * (period - 1) + tr_list[i]) / period
        plus_smooth = (plus_smooth * (period - 1) + plus_dm[i]) / period
        minus_smooth = (minus_smooth * (period - 1) + minus_dm[i]) / period
        if avg_tr > 0:
            pdi = 100 * plus_smooth / avg_tr
            mdi = 100 * minus_smooth / avg_tr
        total = pdi + mdi
        dx_list.append(100 * abs(pdi - mdi) / total if total > 0 else 0)
    if len(dx_list) >= period:
        adx_val = sum(dx_list[-period:]) / period
    else:
        adx_val = sum(dx_list) / max(len(dx_list), 1)
    return adx_val, pdi, mdi
