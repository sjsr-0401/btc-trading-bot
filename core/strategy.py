"""EMA 크로스 전략 로직"""
from .indicators import ema, rsi, macd_histogram, atr, adx


def check_ema_signal(c15, c1h, c4h):
    """
    멀티타임프레임 EMA 크로스 신호 분석
    Returns: (direction, score, reasons)
        direction: "L" / "S" / "W"(wait)
    """
    if not c15 or not c1h or not c4h:
        return "W", 0, []
    if len(c15) < 100 or len(c1h) < 50 or len(c4h) < 50:
        return "W", 0, []

    closes = [c["c"] for c in c15]
    e7 = ema(closes, 7)
    e21 = ema(closes, 21)
    e50 = ema(closes, 50)
    cur = closes[-1]

    cross_up = e7[-1] > e21[-1] and e7[-2] <= e21[-2]
    cross_dn = e7[-1] < e21[-1] and e7[-2] >= e21[-2]
    if not cross_up and not cross_dn:
        return "W", 0, []

    score = 0
    reasons = []

    if cross_up:
        direction = "L"
        score += 20
        reasons.append("+ 골든크로스")

        if cur <= e50[-1]:
            return "W", 0, ["! 50EMA아래"]

        c4hc = [c["c"] for c in c4h]
        e21_4 = ema(c4hc, 21)
        if c4hc[-1] <= e21_4[-1]:
            return "W", 0, ["! 4H하락"]
        score += 15

        c1hc = [c["c"] for c in c1h]
        e21h = ema(c1hc, 21)
        e50h = ema(c1hc, 50)
        if c1hc[-1] > e21h[-1] and c1hc[-1] > e50h[-1]:
            score += 15
            reasons.append("+ 1H강세")
        elif c1hc[-1] > e21h[-1]:
            score += 8
            reasons.append("+ 1H상승")
        else:
            score -= 10

        r = rsi(closes)
        if r > 65:
            return "W", 0, [f"! RSI{int(r)}"]
        if 30 < r < 45:
            score += 10
            reasons.append(f"+ RSI{int(r)}")
        elif r < 55:
            score += 5

        h = macd_histogram(closes)
        if h[-1] > 0 and h[-1] > h[-2]:
            score += 10
            reasons.append("+ MACD↑")
        elif h[-1] > h[-2]:
            score += 5
        elif h[-1] < 0 and h[-1] < h[-2]:
            score -= 10

        a, pi, mi = adx(c1h)
        if a > 25 and pi > mi:
            score += 10
            reasons.append(f"+ ADX{int(a)}")
        elif a < 18:
            score -= 5

        vols = [c["v"] for c in c15[-21:]]
        if len(vols) >= 21:
            av = sum(vols[:-1]) / 20
            if vols[-1] > av * 1.5:
                score += 8
                reasons.append("+ 거래량↑")
            elif vols[-1] < av * 0.5:
                score -= 8

        if c15[-1]["c"] > c15[-1]["o"]:
            score += 5
        else:
            score -= 3

    else:
        direction = "S"
        score += 20
        reasons.append("+ 데드크로스")

        if cur >= e50[-1]:
            return "W", 0, ["! 50EMA위"]

        c4hc = [c["c"] for c in c4h]
        e21_4 = ema(c4hc, 21)
        if c4hc[-1] >= e21_4[-1]:
            return "W", 0, ["! 4H상승"]
        score += 15

        c1hc = [c["c"] for c in c1h]
        e21h = ema(c1hc, 21)
        e50h = ema(c1hc, 50)
        if c1hc[-1] < e21h[-1] and c1hc[-1] < e50h[-1]:
            score += 15
            reasons.append("+ 1H약세")
        elif c1hc[-1] < e21h[-1]:
            score += 8
            reasons.append("+ 1H하락")
        else:
            score -= 10

        r = rsi(closes)
        if r < 35:
            return "W", 0, [f"! RSI{int(r)}"]
        if 55 < r < 70:
            score += 10
            reasons.append(f"+ RSI{int(r)}")
        elif r > 45:
            score += 5

        h = macd_histogram(closes)
        if h[-1] < 0 and h[-1] < h[-2]:
            score += 10
            reasons.append("+ MACD↓")
        elif h[-1] < h[-2]:
            score += 5
        elif h[-1] > 0 and h[-1] > h[-2]:
            score -= 10

        a, pi, mi = adx(c1h)
        if a > 25 and mi > pi:
            score += 10
            reasons.append(f"+ ADX{int(a)}")
        elif a < 18:
            score -= 5

        vols = [c["v"] for c in c15[-21:]]
        if len(vols) >= 21:
            av = sum(vols[:-1]) / 20
            if vols[-1] > av * 1.5:
                score += 8
                reasons.append("+ 거래량↑")
            elif vols[-1] < av * 0.5:
                score -= 8

        if c15[-1]["c"] < c15[-1]["o"]:
            score += 5
        else:
            score -= 3

    reasons.append(f"score:{score}")
    if score >= 40:
        return direction, score, reasons
    return "W", score, reasons


def confirm_signal(c15, sig_type: str, sig_price: float) -> bool:
    """신호 발생 후 가격 움직임으로 확인"""
    if not c15 or len(c15) < 3:
        return False
    cur = c15[-1]["c"]
    if sig_type == "L":
        if cur > sig_price * 1.001 and c15[-1]["c"] > c15[-1]["o"]:
            return True
        if (cur > sig_price * 1.001
                and c15[-2]["c"] > c15[-2]["o"]
                and cur > c15[-2]["c"]):
            return True
    elif sig_type == "S":
        if cur < sig_price * 0.999 and c15[-1]["c"] < c15[-1]["o"]:
            return True
        if (cur < sig_price * 0.999
                and c15[-2]["c"] < c15[-2]["o"]
                and cur < c15[-2]["c"]):
            return True
    return False


def calc_sl_tp(c15):
    """ATR 기반 동적 SL/TP 계산"""
    atr_val = atr(c15)
    price = c15[-1]["c"]
    if price == 0 or atr_val == 0:
        return 2.5, 5.0
    sl = (atr_val * 3.0 / price) * 100
    tp = (atr_val * 6.0 / price) * 100
    sl = max(min(sl, 5.5), 1.8)
    tp = max(min(tp, 12.0), 3.5)
    if tp < sl * 2.0:
        tp = sl * 2.0
    return round(sl, 2), round(tp, 2)
