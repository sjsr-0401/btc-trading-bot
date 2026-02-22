"""트레이딩 엔진 — 메인 루프를 QThread로 실행"""
import time
import datetime
import gc
from PyQt6.QtCore import QThread, pyqtSignal

from .api import BinanceFuturesAPI
from .strategy import check_ema_signal, confirm_signal, calc_sl_tp


class TradingEngine(QThread):
    # GUI로 보내는 시그널
    log_signal = pyqtSignal(str)           # 로그 메시지
    status_signal = pyqtSignal(dict)       # 상태 업데이트
    trade_signal = pyqtSignal(dict)        # 거래 발생
    position_signal = pyqtSignal(dict)     # 포지션 변경

    def __init__(self, config: dict):
        super().__init__()
        self.config = config
        self.running = False
        self.api = None
        self.prec = 3

        # 상태
        self.total_trades = 0
        self.total_wins = 0
        self.total_losses = 0
        self.total_pnl = 0.0
        self.consec_losses = 0
        self.daily_trades = 0
        self.daily_pnl = 0.0
        self.cooldown_until = None
        self.highest_profit = 0.0
        self.pending = {"type": "N", "score": 0, "cnt": 0, "price": 0}
        self.last_trade_time = 0
        self.last_trade_date = datetime.date.today()

    def log(self, text: str):
        now = datetime.datetime.now().strftime("%m/%d %H:%M:%S")
        m = f"[{now}] {text}"
        self.log_signal.emit(m)

    def stop(self):
        self.running = False

    def run(self):
        cfg = self.config
        self.api = BinanceFuturesAPI(cfg["api_key"], cfg["api_secret"])
        symbol = cfg["symbol"]
        leverage = cfg["leverage"]
        trade_usdt = cfg["trade_usdt"]
        check_interval = cfg.get("check_interval", 60)
        max_daily_trades = cfg.get("max_daily_trades", 4)
        max_daily_loss_pct = cfg.get("max_daily_loss_pct", 3.0)
        max_consec_losses = cfg.get("max_consec_losses", 3)
        cooldown_minutes = cfg.get("cooldown_minutes", 75)

        self.api.set_leverage(symbol, leverage)
        try:
            self.api.set_margin_type(symbol, "ISOLATED")
        except Exception:
            pass

        self.prec = self.api.get_precision(symbol)
        bal = self.api.get_balance()
        self.log(f"START! {bal:.2f} USDT | {symbol} x{leverage} | {trade_usdt} USDT")

        self.running = True
        cnt = 0

        while self.running:
            try:
                now = datetime.datetime.now()
                today = now.date()
                cnt += 1

                # 일일 리셋
                if today != self.last_trade_date:
                    if self.daily_trades > 0:
                        self.log(f"DAILY: {self.daily_trades}t PnL:{self.daily_pnl:.2f}")
                    self.daily_trades = 0
                    self.daily_pnl = 0.0
                    self.last_trade_date = today

                # 일일 제한
                if self.daily_trades >= max_daily_trades:
                    if cnt % 10 == 1:
                        self.log("일일한도 도달")
                    time.sleep(check_interval)
                    continue

                # 일일 손실 제한
                sb = self.api.get_balance()
                if sb > 0 and self.daily_pnl < 0 and abs(self.daily_pnl) / sb * 100 >= max_daily_loss_pct:
                    if cnt % 10 == 1:
                        self.log("손실한도 도달")
                    time.sleep(check_interval)
                    continue

                # 쿨다운
                if self.cooldown_until and now < self.cooldown_until:
                    rm = (self.cooldown_until - now).seconds // 60
                    if cnt % 5 == 1:
                        self.log(f"쿨다운 {rm}분 남음")
                    time.sleep(check_interval)
                    continue
                elif self.cooldown_until:
                    self.cooldown_until = None
                    self.consec_losses = 0
                    self.log("쿨다운 해제, 재시작")

                pt, pa, pe, pp = self.api.get_position(symbol)
                cp = self.api.get_price(symbol)

                # 포지션 상태 전송
                self.position_signal.emit({
                    "type": pt, "amount": pa,
                    "entry": pe, "pnl": pp, "price": cp
                })

                # 포지션 있을 때: 트레일링 관리
                if pt != "N" and pe > 0:
                    if pt == "L":
                        pfp = ((cp - pe) / pe) * 100
                    else:
                        pfp = ((pe - cp) / pe) * 100
                    if pfp > self.highest_profit:
                        self.highest_profit = pfp

                    if self.highest_profit >= 3.0 and self.highest_profit - pfp >= 1.2:
                        self.log(f"TRAIL peak:{self.highest_profit:.2f}% now:{pfp:.2f}%")
                        self._close_position(symbol, pt, pa, pe, cp)
                        time.sleep(5)
                        continue

                # 데이터 수집
                c15 = self.api.get_klines(symbol, "15m", 200)
                c1h = self.api.get_klines(symbol, "1h", 200)
                c4h = self.api.get_klines(symbol, "4h", 200)

                if not c15 or not c1h or not c4h:
                    time.sleep(check_interval)
                    continue

                # 포지션 없을 때: 신호 탐색 & 진입
                if pt == "N":
                    if self.pending["type"] != "N":
                        self.pending["cnt"] += 1
                        if self.pending["cnt"] > 4:
                            self.log("신호 만료")
                            self._reset_pending()
                        else:
                            confirmed = confirm_signal(c15, self.pending["type"], self.pending["price"])
                            if confirmed:
                                dec = self.pending["type"]
                                sl, tp = calc_sl_tp(c15)
                                b = self.api.get_balance()
                                inv = min(trade_usdt, b * 0.9)
                                if inv >= 5:
                                    self.log(f"확인완료! {dec} SL:{sl}% TP:{tp}%")
                                    self._open_position(symbol, dec, inv, sl, tp, leverage, cp)
                                self._reset_pending()
                            else:
                                self.log(f"확인대기 {self.pending['cnt']}/4 {self.pending['type']}")

                    elif time.time() - self.last_trade_time > 90 * 60:
                        dec, score, reasons = check_ema_signal(c15, c1h, c4h)
                        if dec != "W":
                            self.pending = {"type": dec, "score": score, "cnt": 0, "price": cp}
                            reason_str = " | ".join(reasons)
                            self.log(f"신호감지! {dec} score:{score} [{reason_str}]")

                # 상태 업데이트
                if cnt % 5 == 0:
                    wr = (self.total_wins / self.total_trades * 100) if self.total_trades > 0 else 0
                    self.status_signal.emit({
                        "cycle": cnt, "price": cp,
                        "position": pt, "entry": pe,
                        "highest": self.highest_profit,
                        "total": self.total_trades,
                        "wins": self.total_wins,
                        "winrate": wr,
                        "pnl": self.total_pnl,
                        "daily_trades": self.daily_trades,
                        "daily_pnl": self.daily_pnl,
                        "pending": self.pending["type"],
                        "leverage": leverage,
                        "balance": sb if sb else 0,
                    })

                gc.collect()
                time.sleep(check_interval)

            except Exception as e:
                self.log(f"ERR: {e}")
                time.sleep(30)

        # 종료 시 포지션 정리
        pt, pa, pe, pp = self.api.get_position(symbol)
        if pt != "N":
            cp = self.api.get_price(symbol)
            self._close_position(symbol, pt, pa, pe, cp)
        self.log("엔진 종료")

    def _open_position(self, symbol, direction, usdt, sl, tp, leverage, price):
        qty = round((usdt * leverage) / price, self.prec)
        if qty <= 0:
            self.log("수량 부족")
            return

        if direction == "L":
            side = "BUY"
            close_side = "SELL"
            sl_price = round(price * (1 - sl / 100), 2)
            tp_price = round(price * (1 + tp / 100), 2)
        else:
            side = "SELL"
            close_side = "BUY"
            sl_price = round(price * (1 + sl / 100), 2)
            tp_price = round(price * (1 - tp / 100), 2)

        order = self.api.market_order(symbol, side, qty)
        if order and "orderId" in order:
            label = "LONG" if direction == "L" else "SHORT"
            self.log(f"{label} {qty} @ {price:.2f} x{leverage}")
            self.api.stop_market(symbol, close_side, sl_price)
            self.log(f"  SL: {sl_price} (-{sl}%)")
            self.api.take_profit_market(symbol, close_side, tp_price)
            self.log(f"  TP: {tp_price} (+{tp}%)")
            self.highest_profit = 0.0
            self.last_trade_time = time.time()
            self.trade_signal.emit({
                "action": "OPEN", "direction": direction,
                "qty": qty, "price": price, "sl": sl_price, "tp": tp_price
            })
        else:
            self.log(f"{direction} 주문 실패: {order}")

    def _close_position(self, symbol, pt, pa, pe, cp):
        close_side = "SELL" if pt == "L" else "BUY"
        self.api.cancel_all_orders(symbol)
        self.api.market_order(symbol, close_side, pa)

        if pe > 0:
            if pt == "L":
                pnl = (cp - pe) * pa
            else:
                pnl = (pe - cp) * pa
        else:
            pnl = 0

        self.total_trades += 1
        self.daily_trades += 1
        if pnl > 0:
            self.total_wins += 1
            self.consec_losses = 0
        else:
            self.total_losses += 1
            self.consec_losses += 1
            # 원본 코드에 빠져있던 쿨다운 트리거
            max_cl = self.config.get("max_consec_losses", 3)
            cd_min = self.config.get("cooldown_minutes", 75)
            if self.consec_losses >= max_cl:
                self.cooldown_until = datetime.datetime.now() + datetime.timedelta(minutes=cd_min)
                self.log(f"연속 {self.consec_losses}패! {cd_min}분 쿨다운")

        self.total_pnl += pnl
        self.daily_pnl += pnl
        self.highest_profit = 0.0
        self.log(f"CLOSE {pt} pnl:{pnl:.2f}")
        self.trade_signal.emit({
            "action": "CLOSE", "direction": pt,
            "pnl": pnl, "price": cp
        })

    def _reset_pending(self):
        self.pending = {"type": "N", "score": 0, "cnt": 0, "price": 0}
