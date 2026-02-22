import requests
import hmac
import hashlib
import time
import datetime
from urllib.parse import urlencode
import gc

# ============================================
# 설정 API키랑 API시크릿 붙여넣기,  원하면 디스코드 웹훅
# ============================================
API_KEY = ""
API_SECRET = ""

SYMBOL = "BTCUSDT"
LEVERAGE = 4
TRADE_USDT = 35

MAX_CONSECUTIVE_LOSSES = 3
COOLDOWN_MINUTES = 75
MAX_DAILY_TRADES = 4
MAX_DAILY_LOSS_PCT = 3.0

CHECK_INTERVAL = 60  # 초

DISCORD_WEBHOOK = ""
BASE = "https://fapi.binance.com"

# ============================================
# 전역 상태
# ============================================
tt = 0; tw = 0; tl = 0; tp2 = 0.0
cl = 0; dt = 0; dp = 0.0
cdu = None  # 쿨다운
hp = 0.0    # 최고수익
pending = {"type": "N", "score": 0, "cnt": 0, "price": 0}
last_trade_time = 0
prec = 3
cur_sl = 2.5; cur_tp = 5.0
ltd = datetime.date.today()

# ============================================
# API
# ============================================
def get_server_time():
    try:
        r = requests.get(BASE + "/fapi/v1/time", timeout=5)
        return r.json()["serverTime"]
    except:
        return int(time.time() * 1000)

def sign(params):
    q = urlencode(params)
    return hmac.new(API_SECRET.encode(), q.encode(), hashlib.sha256).hexdigest()

def api_get(path, params=None, signed=False):
    if params is None: params = {}
    h = {"X-MBX-APIKEY": API_KEY}
    if signed:
        params["timestamp"] = get_server_time()
        params["signature"] = sign(params)
    try:
        r = requests.get(BASE + path, params=params, headers=h, timeout=10)
        return r.json()
    except:
        return None

def api_post(path, params=None):
    if params is None: params = {}
    h = {"X-MBX-APIKEY": API_KEY}
    params["timestamp"] = get_server_time()
    params["signature"] = sign(params)
    try:
        r = requests.post(BASE + path, params=params, headers=h, timeout=10)
        return r.json()
    except:
        return None

def api_delete(path, params=None):
    if params is None: params = {}
    h = {"X-MBX-APIKEY": API_KEY}
    params["timestamp"] = get_server_time()
    params["signature"] = sign(params)
    try:
        r = requests.delete(BASE + path, params=params, headers=h, timeout=10)
        return r.json()
    except:
        return None

# ============================================
# 알림
# ============================================
def msg(text):
    now = datetime.datetime.now().strftime("%m/%d %H:%M:%S")
    m = f"[{now}] {text}"
    print(m)
    if DISCORD_WEBHOOK:
        try:
            requests.post(DISCORD_WEBHOOK, json={"content": m}, timeout=5)
        except:
            pass

# ============================================
# 데이터
# ============================================
def get_klines(symbol, interval, limit=200):
    data = api_get("/fapi/v1/klines",
                   {"symbol": symbol, "interval": interval, "limit": limit})
    if data is None or isinstance(data, dict):
        return None
    candles = []
    for k in data:
        candles.append({
            "o": float(k[1]), "h": float(k[2]),
            "l": float(k[3]), "c": float(k[4]),
            "v": float(k[5]), "qv": float(k[7]),
            "tb": float(k[9])
        })
    return candles

def get_price():
    data = api_get("/fapi/v1/ticker/price", {"symbol": SYMBOL})
    if data and "price" in data:
        return float(data["price"])
    return 0

def get_position():
    data = api_get("/fapi/v2/positionRisk", {"symbol": SYMBOL}, signed=True)
    if data and isinstance(data, list):
        for p in data:
            if p["symbol"] == SYMBOL:
                amt = float(p["positionAmt"])
                entry = float(p["entryPrice"])
                pnl = float(p["unRealizedProfit"])
                if amt > 0: return "L", abs(amt), entry, pnl
                elif amt < 0: return "S", abs(amt), entry, pnl
    return "N", 0, 0, 0

def get_bal():
    data = api_get("/fapi/v2/balance", signed=True)
    if data and isinstance(data, list):
        for b in data:
            if b["asset"] == "USDT":
                return float(b["availableBalance"])
    return 0

def get_precision():
    data = api_get("/fapi/v1/exchangeInfo")
    if data and "symbols" in data:
        for s in data["symbols"]:
            if s["symbol"] == SYMBOL:
                for f in s["filters"]:
                    if f["filterType"] == "LOT_SIZE":
                        step = float(f["stepSize"])
                        if step >= 1: return 0
                        p = 0
                        while step < 1: step *= 10; p += 1
                        return p
    return 3

# ============================================
# 지표
# ============================================
def ema(d, p):
    if len(d) < p: return d[:]
    m = 2.0 / (p + 1); r = [sum(d[:p]) / p]
    for i in range(p, len(d)):
        r.append(d[i] * m + r[-1] * (1 - m))
    return [r[0]] * p + r[1:]

def rsi(c, p=14):
    if len(c) < p + 1: return 50
    d = [c[i] - c[i-1] for i in range(1, len(c))]
    g = [x if x > 0 else 0 for x in d]
    l = [-x if x < 0 else 0 for x in d]
    ag = sum(g[:p]) / p; al = sum(l[:p]) / p
    for i in range(p, len(d)):
        ag = (ag * (p-1) + g[i]) / p
        al = (al * (p-1) + l[i]) / p
    if al == 0: return 100
    return 100 - (100 / (1 + ag / al))

def macd_h(c):
    e12 = ema(c, 12); e26 = ema(c, 26)
    m = [e12[i] - e26[i] for i in range(len(c))]
    s = ema(m, 9)
    return [m[i] - s[i] for i in range(len(c))]

def atr(candles, p=14):
    if len(candles) < p + 1: return 0
    tr = []
    for i in range(1, len(candles)):
        h = candles[i]["h"]; l = candles[i]["l"]; pc = candles[i-1]["c"]
        tr.append(max(h - l, abs(h - pc), abs(l - pc)))
    return sum(tr[-p:]) / p if len(tr) >= p else sum(tr) / max(len(tr), 1)

def adx_calc(candles, p=14):
    if len(candles) < p * 2: return 20, 0, 0
    pdm = []; mdm = []; tr = []
    for i in range(1, len(candles)):
        h = candles[i]["h"]; l = candles[i]["l"]
        ph = candles[i-1]["h"]; pl = candles[i-1]["l"]; pc = candles[i-1]["c"]
        pd = h - ph; md = pl - l
        pdm.append(pd if pd > md and pd > 0 else 0)
        mdm.append(md if md > pd and md > 0 else 0)
        tr.append(max(h - l, abs(h - pc), abs(l - pc)))
    at = sum(tr[:p]) / p; ps = sum(pdm[:p]) / p; ms = sum(mdm[:p]) / p
    dxl = []; pdi = 0; mdi = 0
    for i in range(p, len(tr)):
        at = (at * (p-1) + tr[i]) / p
        ps = (ps * (p-1) + pdm[i]) / p
        ms = (ms * (p-1) + mdm[i]) / p
        if at > 0: pdi = 100 * ps / at; mdi = 100 * ms / at
        dxl.append(100 * abs(pdi - mdi) / (pdi + mdi) if pdi + mdi > 0 else 0)
    a = sum(dxl[-p:]) / p if len(dxl) >= p else sum(dxl) / max(len(dxl), 1)
    return a, pdi, mdi

# ============================================
# EMA 크로스 시그널
# ============================================
def check_ema_signal(c15, c1h, c4h):
    if not c15 or not c1h or not c4h: return "W", 0, []
    if len(c15) < 100 or len(c1h) < 50 or len(c4h) < 50:
        return "W", 0, []

    closes = [c["c"] for c in c15]
    e7 = ema(closes, 7); e21 = ema(closes, 21); e50 = ema(closes, 50)
    cur = closes[-1]

    cross_up = e7[-1] > e21[-1] and e7[-2] <= e21[-2]
    cross_dn = e7[-1] < e21[-1] and e7[-2] >= e21[-2]
    if not cross_up and not cross_dn:
        return "W", 0, []

    score = 0; reasons = []

    if cross_up:
        d = "L"; score += 20; reasons.append("+ 골든크로스")

        if cur <= e50[-1]:
            return "W", 0, ["! 50EMA아래"]

        c4hc = [c["c"] for c in c4h]; e21_4 = ema(c4hc, 21)
        if c4hc[-1] <= e21_4[-1]:
            return "W", 0, ["! 4H하락"]
        score += 15

        c1hc = [c["c"] for c in c1h]; e21h = ema(c1hc, 21); e50h = ema(c1hc, 50)
        if c1hc[-1] > e21h[-1] and c1hc[-1] > e50h[-1]:
            score += 15; reasons.append("+ 1H강세")
        elif c1hc[-1] > e21h[-1]:
            score += 8; reasons.append("+ 1H상승")
        else:
            score -= 10

        r = rsi(closes)
        if r > 65: return "W", 0, [f"! RSI{int(r)}"]
        if 30 < r < 45: score += 10; reasons.append(f"+ RSI{int(r)}")
        elif r < 55: score += 5

        h = macd_h(closes)
        if h[-1] > 0 and h[-1] > h[-2]: score += 10; reasons.append("+ MACD↑")
        elif h[-1] > h[-2]: score += 5
        elif h[-1] < 0 and h[-1] < h[-2]: score -= 10

        a, pi, mi = adx_calc(c1h)
        if a > 25 and pi > mi: score += 10; reasons.append(f"+ ADX{int(a)}")
        elif a < 18: score -= 5

        vols = [c["v"] for c in c15[-21:]]
        if len(vols) >= 21:
            av = sum(vols[:-1]) / 20
            if vols[-1] > av * 1.5: score += 8; reasons.append("+ 거래량↑")
            elif vols[-1] < av * 0.5: score -= 8

        if c15[-1]["c"] > c15[-1]["o"]: score += 5
        else: score -= 3

    else:
        d = "S"; score += 20; reasons.append("+ 데드크로스")

        if cur >= e50[-1]:
            return "W", 0, ["! 50EMA위"]

        c4hc = [c["c"] for c in c4h]; e21_4 = ema(c4hc, 21)
        if c4hc[-1] >= e21_4[-1]:
            return "W", 0, ["! 4H상승"]
        score += 15

        c1hc = [c["c"] for c in c1h]; e21h = ema(c1hc, 21); e50h = ema(c1hc, 50)
        if c1hc[-1] < e21h[-1] and c1hc[-1] < e50h[-1]:
            score += 15; reasons.append("+ 1H약세")
        elif c1hc[-1] < e21h[-1]:
            score += 8; reasons.append("+ 1H하락")
        else:
            score -= 10

        r = rsi(closes)
        if r < 35: return "W", 0, [f"! RSI{int(r)}"]
        if 55 < r < 70: score += 10; reasons.append(f"+ RSI{int(r)}")
        elif r > 45: score += 5

        h = macd_h(closes)
        if h[-1] < 0 and h[-1] < h[-2]: score += 10; reasons.append("+ MACD↓")
        elif h[-1] < h[-2]: score += 5
        elif h[-1] > 0 and h[-1] > h[-2]: score -= 10

        a, pi, mi = adx_calc(c1h)
        if a > 25 and mi > pi: score += 10; reasons.append(f"+ ADX{int(a)}")
        elif a < 18: score -= 5

        vols = [c["v"] for c in c15[-21:]]
        if len(vols) >= 21:
            av = sum(vols[:-1]) / 20
            if vols[-1] > av * 1.5: score += 8; reasons.append("+ 거래량↑")
            elif vols[-1] < av * 0.5: score -= 8

        if c15[-1]["c"] < c15[-1]["o"]: score += 5
        else: score -= 3

    reasons.append(f"score:{score}")
    if score >= 40: return d, score, reasons
    return "W", score, reasons

# ============================================
# 확인 로직
# ============================================
def confirm_signal(c15, sig_type, sig_price):
    if not c15 or len(c15) < 3: return False
    cur = c15[-1]["c"]
    if sig_type == "L":
        if cur > sig_price * 1.001 and c15[-1]["c"] > c15[-1]["o"]:
            return True
        if cur > sig_price * 1.001 and c15[-2]["c"] > c15[-2]["o"] and cur > c15[-2]["c"]:
            return True
    elif sig_type == "S":
        if cur < sig_price * 0.999 and c15[-1]["c"] < c15[-1]["o"]:
            return True
        if cur < sig_price * 0.999 and c15[-2]["c"] < c15[-2]["o"] and cur < c15[-2]["c"]:
            return True
    return False

def reset_pending():
    global pending
    pending = {"type": "N", "score": 0, "cnt": 0, "price": 0}

# ============================================
# SL/TP
# ============================================
def calc_sl_tp(c15):
    atr_val = atr(c15)
    price = c15[-1]["c"]
    if price == 0 or atr_val == 0: return 2.5, 5.0
    sl = (atr_val * 3.0 / price) * 100
    tp = (atr_val * 6.0 / price) * 100
    sl = max(min(sl, 5.5), 1.8)
    tp = max(min(tp, 12.0), 3.5)
    if tp < sl * 2.0: tp = sl * 2.0
    return round(sl, 2), round(tp, 2)

# ============================================
# 주문
# ============================================
def open_long(usdt, sl, tp):
    price = get_price()
    qty = round((usdt * LEVERAGE) / price, prec)
    if qty <= 0: msg("수량부족"); return None
    order = api_post("/fapi/v1/order", {
        "symbol": SYMBOL, "side": "BUY",
        "type": "MARKET", "quantity": qty
    })
    if order and "orderId" in order:
        msg(f"🟢 LONG {qty} @ {price:.2f} x{LEVERAGE}")
        slp = round(price * (1 - sl / 100), 2)
        api_post("/fapi/v1/order", {
            "symbol": SYMBOL, "side": "SELL",
            "type": "STOP_MARKET", "stopPrice": slp,
            "closePosition": "true"
        })
        msg(f"  SL: {slp} (-{sl}%)")
        tpp = round(price * (1 + tp / 100), 2)
        api_post("/fapi/v1/order", {
            "symbol": SYMBOL, "side": "SELL",
            "type": "TAKE_PROFIT_MARKET", "stopPrice": tpp,
            "closePosition": "true"
        })
        msg(f"  TP: {tpp} (+{tp}%)")
        return order
    msg(f"LONG FAIL: {order}")
    return None

def open_short(usdt, sl, tp):
    price = get_price()
    qty = round((usdt * LEVERAGE) / price, prec)
    if qty <= 0: msg("수량부족"); return None
    order = api_post("/fapi/v1/order", {
        "symbol": SYMBOL, "side": "SELL",
        "type": "MARKET", "quantity": qty
    })
    if order and "orderId" in order:
        msg(f"🔴 SHORT {qty} @ {price:.2f} x{LEVERAGE}")
        slp = round(price * (1 + sl / 100), 2)
        api_post("/fapi/v1/order", {
            "symbol": SYMBOL, "side": "BUY",
            "type": "STOP_MARKET", "stopPrice": slp,
            "closePosition": "true"
        })
        msg(f"  SL: {slp} (-{sl}%)")
        tpp = round(price * (1 - tp / 100), 2)
        api_post("/fapi/v1/order", {
            "symbol": SYMBOL, "side": "BUY",
            "type": "TAKE_PROFIT_MARKET", "stopPrice": tpp,
            "closePosition": "true"
        })
        msg(f"  TP: {tpp} (+{tp}%)")
        return order
    msg(f"SHORT FAIL: {order}")
    return None

def close_pos():
    pt, amt, entry, pnl = get_position()
    if pt == "N": return
    side = "SELL" if pt == "L" else "BUY"
    api_delete("/fapi/v1/allOpenOrders", {"symbol": SYMBOL})
    api_post("/fapi/v1/order", {
        "symbol": SYMBOL, "side": side,
        "type": "MARKET", "quantity": amt
    })
    price = get_price()
    if entry > 0:
        if pt == "L": pct = ((price - entry) / entry) * 100 * LEVERAGE
        else: pct = ((entry - price) / entry) * 100 * LEVERAGE
    else: pct = 0
    msg(f"⬜ CLOSE {pt} x{LEVERAGE} pnl:{pnl:.2f} ({pct:.2f}%)")

# ============================================
# 메인 루프
# ============================================
def main():
    global tt, tw, tl, tp2, cl, dt, dp, cdu, hp
    global pending, last_trade_time, prec, cur_sl, cur_tp, ltd

    print("=" * 50)
    print("🤖 EMA CROSS BOT v7.0")
    print("  백테스트 결과: +24% / 360일")
    print("  PF:1.22 RR:1.70 승률:42%")
    print("=" * 50)

    api_post("/fapi/v1/leverage", {"symbol": SYMBOL, "leverage": LEVERAGE})
    try:
        api_post("/fapi/v1/marginType", {"symbol": SYMBOL, "marginType": "ISOLATED"})
    except: pass

    prec = get_precision()
    bal = get_bal()
    msg(f"START! {bal:.2f} USDT | {SYMBOL} x{LEVERAGE} | {TRADE_USDT} USDT")

    cnt = 0
    reset_pending()

    while True:
        try:
            now = datetime.datetime.now()
            today = now.date()
            cnt += 1

            # 일일 리셋
            if today != ltd:
                if dt > 0: msg(f"📊 DAILY: {dt}t PnL:{dp:.2f}")
                dt = 0; dp = 0; ltd = today

            # 일일 제한
            if dt >= MAX_DAILY_TRADES:
                if cnt % 10 == 1: msg("⚠️ 일일한도")
                time.sleep(CHECK_INTERVAL); continue

            # 일일 손실 제한
            sb = get_bal()
            if sb > 0 and dp < 0 and abs(dp) / sb * 100 >= MAX_DAILY_LOSS_PCT:
                if cnt % 10 == 1: msg("⚠️ 손실한도")
                time.sleep(CHECK_INTERVAL); continue

            # 쿨다운
            if cdu and now < cdu:
                rm = (cdu - now).seconds // 60
                if cnt % 5 == 1: msg(f"⏸️ 쿨다운 {rm}분")
                time.sleep(CHECK_INTERVAL); continue
            elif cdu:
                cdu = None; cl = 0; msg("▶️ 재시작")

            pt, pa, pe, pp = get_position()
            cp = get_price()

            # ★ 포지션 있을 때: 트레일링 관리
            if pt != "N" and pe > 0:
                if pt == "L": pfp = ((cp - pe) / pe) * 100
                else: pfp = ((pe - cp) / pe) * 100
                if pfp > hp: hp = pfp

                # 트레일링: 3% 이상 수익 후 1.2% 하락
                if hp >= 3.0 and hp - pfp >= 1.2:
                    msg(f"🔔 TRAIL peak:{hp:.2f}% now:{pfp:.2f}%")
                    close_pos()
                    tt += 1; dt += 1
                    if pp > 0: tw += 1; cl = 0; dp += pp
                    else: tl += 1; cl += 1; dp += pp
                    tp2 += pp; hp = 0
                    time.sleep(5); continue

            # 데이터 가져오기
            c15 = get_klines(SYMBOL, "15m", 200)
            c1h = get_klines(SYMBOL, "1h", 200)
            c4h = get_klines(SYMBOL, "4h", 200)

            if not c15 or not c1h or not c4h:
                time.sleep(CHECK_INTERVAL); continue

            # ★ 포지션 없을 때: 신호 탐색 & 진입
            if pt == "N":
                # 대기 신호 확인
                if pending["type"] != "N":
                    pending["cnt"] += 1
                    if pending["cnt"] > 4:
                        msg("  ❌ 신호만료")
                        reset_pending()
                    else:
                        confirmed = confirm_signal(c15, pending["type"], pending["price"])
                        if confirmed:
                            dec = pending["type"]
                            cur_sl, cur_tp = calc_sl_tp(c15)
                            b = get_bal()
                            inv = min(TRADE_USDT, b * 0.9)
                            if inv >= 5:
                                msg(f"  ✅ 확인완료! SL:{cur_sl}% TP:{cur_tp}%")
                                if dec == "L": open_long(inv, cur_sl, cur_tp)
                                else: open_short(inv, cur_sl, cur_tp)
                                hp = 0; last_trade_time = time.time()
                            reset_pending()
                        else:
                            msg(f"  ⏳ 확인대기 {pending['cnt']}/4 {pending['type']}")

                # 새 신호 탐색
                elif time.time() - last_trade_time > 90 * 60:  # 최소 1.5시간 간격
                    dec, score, reasons = check_ema_signal(c15, c1h, c4h)
                    if dec != "W":
                        pending = {"type": dec, "score": score, "cnt": 0, "price": cp}
                        msg(f"  🔍 신호감지! {dec} score:{score}")
                        for r in reasons:
                            msg(f"    {r}")

            # 로그 출력
            if cnt % 5 == 0:
                wr = (tw / tt * 100) if tt > 0 else 0
                status = f"[{cnt}] {cp:.2f}"
                if pt != "N":
                    if pt == "L": pct = ((cp - pe) / pe) * 100 * LEVERAGE
                    else: pct = ((pe - cp) / pe) * 100 * LEVERAGE
                    status += f" | {pt} x{LEVERAGE} {pct:+.2f}% hp:{hp:.2f}%"
                else:
                    status += " | WAIT"
                if pending["type"] != "N":
                    status += f" | 신호:{pending['type']}"
                status += f" | {tt}t {tw}w({wr:.0f}%)"
                msg(status)

            gc.collect()
            time.sleep(CHECK_INTERVAL)

        except KeyboardInterrupt:
            msg("👋 종료!")
            pt, _, _, _ = get_position()
            if pt != "N": close_pos()
            break
        except Exception as e:
            msg(f"❌ ERR: {e}")
            time.sleep(30)

if __name__ == "__main__":
    main()
