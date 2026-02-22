from __future__ import annotations

import logging
import time
from datetime import datetime, timezone
from typing import Dict, Optional

import pandas as pd

from .config import BotConfig
from .discord import DiscordNotifier
from .exchange import (
    cancel_all_orders,
    create_market,
    create_stop_market,
    create_take_profit_market,
    fetch_balance_usdt,
    fetch_open_orders,
    fetch_positions,
    fetch_ticker_last,
    load_markets,
    make_exchange,
    market_min_amount,
    safe_symbol,
    set_leverage,
    set_margin_mode,
)
from .risk import RiskState, can_open_new_trades, position_size_from_risk, update_risk_state
from .selector import rank_symbols
from .state import BotState, PositionState, load_state, save_state
from .strategies import add_indicators, signal_at
from .utils import setup_logging

log = logging.getLogger("live")


def _now_utc() -> datetime:
    return datetime.now(timezone.utc)


def _positions_map(raw_positions) -> Dict[str, float]:
    """
    returns symbol -> positionAmt (abs qty)
    """
    out = {}
    for p in raw_positions:
        try:
            sym = p.get("symbol") or p.get("info", {}).get("symbol")
            if not sym:
                continue
            amt = float(p.get("contracts") or p.get("positionAmt") or p.get("info", {}).get("positionAmt") or 0.0)
            out[sym] = amt
        except Exception:
            continue
    return out


class LiveBot:
    def __init__(self, cfg: BotConfig, *, state_path: str = "state/state.json"):
        self.cfg = cfg
        self.state_path = state_path
        self.state: BotState = load_state(state_path)
        self.ex = make_exchange(cfg.exchange)
        self.notifier = DiscordNotifier(cfg.discord)
        setup_logging()

    def start(self) -> None:
        self.notifier.send("🚀 Live bot starting")
        load_markets(self.ex)

        # initial selector refresh
        self.refresh_symbols(force=True)

        while True:
            try:
                self.loop_once()
            except Exception as e:
                log.exception("loop error: %s", e)
                self.notifier.send(f"⚠️ loop error: {e}")

            # heartbeat (hourly)
            if self.cfg.live.heartbeat_sec:
                self.notifier.heartbeat("usdm-bot", every_sec=self.cfg.live.heartbeat_sec)

            save_state(self.state_path, self.state)
            time.sleep(self.cfg.live.poll_interval_sec)

    def get_equity(self) -> float:
        total, _ = fetch_balance_usdt(self.ex)
        return float(total)

    def refresh_symbols(self, force: bool = False) -> None:
        now = time.time()
        if (not force) and (now - float(self.state.last_selector_ts) < self.cfg.selector.refresh_interval_sec):
            return
        ranked = rank_symbols(self.ex, self.cfg.selector, self.cfg.backtest, cache_dir=self.cfg.backtest.cache_dir)
        syms = [s.symbol for s in ranked]
        self.state.selected_symbols = syms
        self.state.last_selector_ts = now
        if syms:
            msg = "🔁 Symbols refreshed: " + ", ".join(syms[: min(10, len(syms))])
            self.notifier.send(msg)

    def loop_once(self) -> None:
        self.refresh_symbols(force=False)

        equity = self.get_equity()
        ts = _now_utc()
        self.state.risk, metrics = update_risk_state(self.state.risk, self.cfg.risk, equity, ts)

        # notify if halted
        if self.state.risk.daily_halted:
            self.notifier.send(f"🛑 Daily loss limit hit ({metrics.get('daily_return',0):.2%}). New entries blocked.")
            if self.cfg.risk.close_on_daily_stop:
                self.close_all_positions("daily_stop")

        if self.state.risk.kill_halted:
            self.notifier.send(f"🛑 Kill switch drawdown hit ({metrics.get('drawdown_from_peak',0):.2%}). Halting.")
            self.close_all_positions("kill_switch")

        # reconcile exchange positions
        raw_pos = fetch_positions(self.ex)
        pos_map = _positions_map(raw_pos)

        # drop positions that no longer exist
        for p in list(self.state.positions):
            if p.symbol not in pos_map or abs(pos_map.get(p.symbol, 0.0)) < 1e-12:
                # position closed externally
                cancel_all_orders(self.ex, p.symbol)
                self.state.positions.remove(p)
                self.notifier.send(f"ℹ️ Position closed externally: {p.symbol}")

        # manage existing positions
        for p in list(self.state.positions):
            self.manage_position(p)

        # entries
        if can_open_new_trades(self.state.risk) and len(self.state.positions) < self.cfg.risk.max_positions:
            self.try_entries(equity)

    def close_all_positions(self, reason: str) -> None:
        for p in list(self.state.positions):
            try:
                side = "sell" if p.side == "long" else "buy"
                create_market(self.ex, p.symbol, side, p.qty, params={"reduceOnly": True})
            except Exception as e:
                log.warning("close position failed %s: %s", p.symbol, e)
            try:
                cancel_all_orders(self.ex, p.symbol)
            except Exception:
                pass
            self.notifier.send(f"🧹 Closed {p.symbol} ({p.side}) due to {reason}")
            self.state.positions.remove(p)

    def manage_position(self, p: PositionState) -> None:
        """
        Ensure stop/TP exist and update trailing for trend positions.
        """
        # refresh trailing stop based on latest candles
        try:
            df = self.fetch_recent(p.symbol, bars=300)
            if len(df) < 50:
                return
            feat = add_indicators(df, self.cfg.strategy)
            # use last closed candle for trailing computation
            row = feat.iloc[-2]
            atr_ = float(row["atr"] or 0.0)
            high = float(row["high"])
            low = float(row["low"])

            changed = False
            if "trend_breakout" in (p.tag or "") and atr_ > 0:
                if p.side == "long":
                    p.highest = max(p.highest, high)
                    trail = p.highest - self.cfg.strategy.trail_atr_mult_trend * atr_
                    if trail > p.stop_price * (1 + self.cfg.strategy.trail_update_min_gap):
                        p.stop_price = max(p.stop_price, trail)
                        changed = True
                else:
                    p.lowest = min(p.lowest, low)
                    trail = p.lowest + self.cfg.strategy.trail_atr_mult_trend * atr_
                    if trail < p.stop_price * (1 - self.cfg.strategy.trail_update_min_gap):
                        p.stop_price = min(p.stop_price, trail)
                        changed = True

            # Ensure orders
            self.ensure_brackets(p, changed_stop=changed)

        except Exception as e:
            log.warning("manage_position error %s: %s", p.symbol, e)

    def ensure_brackets(self, p: PositionState, *, changed_stop: bool = False) -> None:
        orders = fetch_open_orders(self.ex, p.symbol)

        # Identify stop / tp orders (best-effort)
        stop_orders = []
        tp_orders = []
        for o in orders:
            t = (o.get("type") or "").upper()
            info_type = str(o.get("info", {}).get("type") or "").upper()
            k = t + "|" + info_type
            if "STOP" in k and "TAKE" not in k:
                stop_orders.append(o)
            if "TAKE_PROFIT" in k or ("TAKE" in k and "PROFIT" in k):
                tp_orders.append(o)

        # If missing, create
        if not stop_orders or changed_stop:
            # cancel existing stop orders
            for o in stop_orders:
                try:
                    self.ex.cancel_order(o["id"], p.symbol)
                except Exception:
                    pass
            side = "sell" if p.side == "long" else "buy"
            try:
                create_stop_market(
                    self.ex,
                    p.symbol,
                    side,
                    p.qty,
                    p.stop_price,
                    reduce_only=True,
                    working_type=self.cfg.live.working_type,
                )
                self.notifier.send(f"🛡️ Stop updated: {p.symbol} {p.side} @ {p.stop_price:.6g}")
            except Exception as e:
                log.warning("create stop failed %s: %s", p.symbol, e)

        if not tp_orders:
            side = "sell" if p.side == "long" else "buy"
            try:
                create_take_profit_market(
                    self.ex,
                    p.symbol,
                    side,
                    p.qty,
                    p.take_profit,
                    reduce_only=True,
                    working_type=self.cfg.live.working_type,
                )
            except Exception as e:
                log.warning("create tp failed %s: %s", p.symbol, e)

    def fetch_recent(self, symbol: str, bars: int = 300) -> pd.DataFrame:
        tf = self.cfg.backtest.timeframe
        # fetch since now - bars
        now_ms = self.ex.milliseconds()
        tf_ms = self.ex.parse_timeframe(tf) * 1000
        start_ms = now_ms - bars * tf_ms
        rows = self.ex.fetch_ohlcv(symbol, timeframe=tf, since=start_ms, limit=min(bars, self.cfg.backtest.ohlcv_limit))
        df = pd.DataFrame(rows, columns=["ts", "open", "high", "low", "close", "volume"])
        df["datetime"] = pd.to_datetime(df["ts"], unit="ms", utc=True)
        df = df.drop(columns=["ts"]).set_index("datetime").sort_index()
        return df

    def try_entries(self, equity: float) -> None:
        selected = list(self.state.selected_symbols)
        if not selected:
            return

        # avoid over-allocating: simple cap by max_positions
        open_syms = {p.symbol for p in self.state.positions}

        for sym in selected:
            if sym in open_syms:
                continue
            if len(self.state.positions) >= self.cfg.risk.max_positions:
                break
            try:
                df = self.fetch_recent(sym, bars=600)
                if len(df) < 250:
                    continue
                feat = add_indicators(df, self.cfg.strategy)
                # use last closed candle as signal bar
                i = len(feat) - 2
                sig = signal_at(feat, i, self.cfg.strategy)
                if sig is None:
                    continue

                last = fetch_ticker_last(self.ex, sym)
                if last <= 0:
                    continue

                atr_ = float(feat["atr"].iloc[i] or 0.0)
                if atr_ <= 0:
                    continue

                entry_price = last * (1 + self.cfg.cost.slippage if sig.side == "long" else 1 - self.cfg.cost.slippage)

                # Recompute SL/TP from entry_price to keep risk sizing consistent
                if sig.tag == "trend_breakout":
                    stop_mult = self.cfg.strategy.stop_atr_mult_trend
                    tp_r = self.cfg.strategy.tp_r_trend
                else:
                    stop_mult = self.cfg.strategy.stop_atr_mult_mr
                    tp_r = self.cfg.strategy.tp_r_mr

                if sig.side == "long":
                    stop_price = entry_price - stop_mult * atr_
                    tp_price = entry_price + tp_r * (entry_price - stop_price)
                else:
                    stop_price = entry_price + stop_mult * atr_
                    tp_price = entry_price - tp_r * (stop_price - entry_price)

                used_notional = sum(abs(pp.qty * pp.entry_price) for pp in self.state.positions)
                qty = position_size_from_risk(self.cfg.risk, equity, entry_price, stop_price, side=sig.side, used_notional=used_notional)
                if qty <= 0:
                    continue

                # exchange min amount
                min_amt = market_min_amount(self.ex, sym)
                if min_amt and qty < min_amt:
                    continue

                # set leverage / margin
                set_margin_mode(self.ex, sym, self.cfg.live.margin_mode)
                set_leverage(self.ex, sym, int(self.cfg.risk.leverage))

                # entry
                side = "buy" if sig.side == "long" else "sell"
                create_market(self.ex, sym, side, qty, params={})

                # bracket orders
                exit_side = "sell" if sig.side == "long" else "buy"
                create_stop_market(
                    self.ex, sym, exit_side, qty, stop_price,
                    reduce_only=True, working_type=self.cfg.live.working_type
                )
                create_take_profit_market(
                    self.ex, sym, exit_side, qty, float(tp_price),
                    reduce_only=True, working_type=self.cfg.live.working_type
                )

                ps = PositionState(
                    symbol=sym,
                    side=sig.side,
                    qty=float(qty),
                    entry_price=float(entry_price),
                    stop_price=float(stop_price),
                    take_profit=float(tp_price),
                    tag=sig.tag,
                    opened_ts=ts_iso(),
                    highest=float(entry_price),
                    lowest=float(entry_price),
                )
                self.state.positions.append(ps)

                self.notifier.send(
                    f"📌 Enter {sym} {sig.side} qty={qty:.6g} entry≈{entry_price:.6g} "
                    f"SL={stop_price:.6g} TP={float(tp_price):.6g} ({sig.tag})"
                )

            except Exception as e:
                log.warning("entry failed %s: %s", sym, e)


def ts_iso() -> str:
    return _now_utc().isoformat()
