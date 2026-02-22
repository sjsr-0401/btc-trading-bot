from __future__ import annotations

import logging
from dataclasses import dataclass, asdict
from datetime import datetime, timezone
from typing import Dict, List

import numpy as np
import pandas as pd

from .config import BotConfig
from .risk import RiskState, can_open_new_trades, position_size_from_risk, update_risk_state
from .strategies import add_indicators, signal_at
from .utils import ensure_dir

log = logging.getLogger("backtest")


@dataclass
class Trade:
    symbol: str
    side: str
    qty: float
    entry_time: str
    entry_price: float
    exit_time: str
    exit_price: float
    pnl_usdt: float          # net (after exit fee, incl funding in separate column)
    fees_usdt: float
    funding_usdt: float
    tag: str

    def to_dict(self):
        return asdict(self)


@dataclass
class Position:
    symbol: str
    side: str
    qty: float
    entry_price: float
    stop_price: float
    take_profit: float
    tag: str
    opened_i: int
    highest: float
    lowest: float
    fees_paid: float = 0.0
    funding_paid: float = 0.0


def _tf_to_minutes(tf: str) -> int:
    if tf.endswith("m"):
        return int(tf[:-1])
    if tf.endswith("h"):
        return int(tf[:-1]) * 60
    raise ValueError(f"Unsupported timeframe {tf}")


def _minmax(x: np.ndarray) -> np.ndarray:
    mn = np.nanmin(x)
    mx = np.nanmax(x)
    if not np.isfinite(mn) or not np.isfinite(mx) or mx - mn < 1e-12:
        return np.zeros_like(x)
    return (x - mn) / (mx - mn)


def _unrealised_pnl(pos: Position, price: float) -> float:
    if pos.side == "long":
        return (price - pos.entry_price) * pos.qty
    return (pos.entry_price - price) * pos.qty


def run_backtest(
    cfg: BotConfig,
    data_map: Dict[str, pd.DataFrame],
    *,
    start: datetime,
    end: datetime,
    initial_equity: float = 850.0,
    out_dir: str = "bt_out",
) -> Dict:
    """
    Multi-symbol portfolio backtest (5m) with:
      - 자동 종목 선별(6h 리밸런싱)
      - 레짐 기반 (ADX) 듀얼 전략(트렌드/평균회귀)
      - 1회 리스크 제한, 일일 손실 제한, 킬스위치
      - 비용(수수료/슬리피지/펀딩) 반영

    NOTE: OHLCV만으로는 캔들 내 체결 순서를 정확히 알 수 없어서,
          동시에 TP/SL을 터치한 캔들은 보수적으로 SL 우선 처리합니다.
    """
    ensure_dir(out_dir)

    tf = cfg.backtest.timeframe
    minutes = _tf_to_minutes(tf)
    idx = pd.date_range(start=start, end=end, freq=f"{minutes}min", tz=timezone.utc)

    # Prepare aligned frames with indicators
    frames: Dict[str, pd.DataFrame] = {}
    for sym, df in data_map.items():
        f = add_indicators(df, cfg.strategy)
        f["qv"] = f["volume"] * f["close"]
        f["qv24h"] = f["qv"].rolling(288).sum()  # 24h on 5m
        f["atr_pct"] = (f["atr"] / (f["close"] + 1e-12)) * 100.0
        frames[sym] = f.reindex(idx)

    symbols = list(frames.keys())
    if not symbols:
        raise ValueError("No OHLCV data provided")

    cash = float(initial_equity)  # realised + fees/funding already applied
    equity_curve: List[float] = []
    ts_list: List[pd.Timestamp] = []
    positions: Dict[str, Position] = {}
    trades: List[Trade] = []

    state = RiskState()
    selector_interval = cfg.selector.refresh_interval_sec
    selector_step = max(1, int(selector_interval / (minutes * 60)))
    selected: List[str] = []

    # Funding schedule: every 8h -> 96 bars on 5m
    funding_step = max(1, int((8 * 60) / minutes))

    def mark_to_market(i: int) -> float:
        eq = cash
        for p in positions.values():
            row = frames[p.symbol].iloc[i]
            if not np.isfinite(row["close"]):
                continue
            eq += _unrealised_pnl(p, float(row["close"]))
        return float(eq)

    def close_position(sym: str, i: int, reason: str) -> None:
        nonlocal cash
        pos = positions.pop(sym, None)
        if pos is None:
            return
        row = frames[sym].iloc[i]
        close = float(row["close"]) if np.isfinite(row["close"]) else pos.entry_price
        exit_price = close * (1 - cfg.cost.slippage if pos.side == "long" else 1 + cfg.cost.slippage)
        pnl = _unrealised_pnl(pos, exit_price)
        fee = abs(exit_price * pos.qty) * cfg.cost.taker_fee
        cash += pnl - fee
        trades.append(Trade(
            symbol=sym,
            side=pos.side,
            qty=pos.qty,
            entry_time=str(idx[pos.opened_i]),
            entry_price=pos.entry_price,
            exit_time=str(idx[i]),
            exit_price=exit_price,
            pnl_usdt=pnl - fee - pos.funding_paid,
            fees_usdt=pos.fees_paid + fee,
            funding_usdt=pos.funding_paid,
            tag=pos.tag + f"|{reason}",
        ))

    for i, ts in enumerate(idx):
        equity = mark_to_market(i)

        # Update risk state
        state, _ = update_risk_state(state, cfg.risk, equity, ts.to_pydatetime())

        # Funding cost (worst-case: abs funding * multiplier)
        if i > 0 and (i % funding_step == 0) and positions:
            for p in positions.values():
                row = frames[p.symbol].iloc[i]
                px = float(row["close"]) if np.isfinite(row["close"]) else p.entry_price
                notional = p.qty * px
                funding = abs(notional) * cfg.cost.funding_rate_per_8h * cfg.cost.funding_cost_multiplier
                cash -= funding
                p.funding_paid += funding
            equity = mark_to_market(i)

        # Selection refresh
        if i % selector_step == 0:
            vols = np.array([float(frames[s]["qv24h"].iloc[i] or 0.0) for s in symbols], dtype=float)
            vps = np.array([float(frames[s]["atr_pct"].iloc[i] or 0.0) for s in symbols], dtype=float)
            vols_n = _minmax(np.log1p(vols))
            vps_n = _minmax(vps)
            sc = cfg.selector.w_volume * vols_n + cfg.selector.w_volatility * vps_n
            order = list(np.argsort(-sc))
            selected = [symbols[j] for j in order[: cfg.selector.top_n]]

        # Daily stop
        if state.daily_halted and cfg.risk.close_on_daily_stop and positions:
            for sym in list(positions.keys()):
                close_position(sym, i, "daily_stop")

        # Kill switch (max drawdown)
        if state.kill_halted and positions:
            for sym in list(positions.keys()):
                close_position(sym, i, "kill_switch")

        # Manage open positions
        for sym, pos in list(positions.items()):
            row = frames[sym].iloc[i]
            if not (np.isfinite(row["high"]) and np.isfinite(row["low"]) and np.isfinite(row["close"])):
                continue

            high = float(row["high"])
            low = float(row["low"])
            atr_ = float(row["atr"] or 0.0)

            # Exit checks
            if pos.side == "long":
                if low <= pos.stop_price:
                    # stop first (conservative)
                    exit_px = pos.stop_price * (1 - cfg.cost.slippage)
                    pnl = _unrealised_pnl(pos, exit_px)
                    fee = abs(exit_px * pos.qty) * cfg.cost.taker_fee
                    cash += pnl - fee
                    trades.append(Trade(
                        symbol=sym,
                        side=pos.side,
                        qty=pos.qty,
                        entry_time=str(idx[pos.opened_i]),
                        entry_price=pos.entry_price,
                        exit_time=str(idx[i]),
                        exit_price=exit_px,
                        pnl_usdt=pnl - fee - pos.funding_paid,
                        fees_usdt=pos.fees_paid + fee,
                        funding_usdt=pos.funding_paid,
                        tag=pos.tag + "|stop",
                    ))
                    positions.pop(sym, None)
                    continue
                if high >= pos.take_profit:
                    exit_px = pos.take_profit * (1 - cfg.cost.slippage)
                    pnl = _unrealised_pnl(pos, exit_px)
                    fee = abs(exit_px * pos.qty) * cfg.cost.taker_fee
                    cash += pnl - fee
                    trades.append(Trade(
                        symbol=sym,
                        side=pos.side,
                        qty=pos.qty,
                        entry_time=str(idx[pos.opened_i]),
                        entry_price=pos.entry_price,
                        exit_time=str(idx[i]),
                        exit_price=exit_px,
                        pnl_usdt=pnl - fee - pos.funding_paid,
                        fees_usdt=pos.fees_paid + fee,
                        funding_usdt=pos.funding_paid,
                        tag=pos.tag + "|tp",
                    ))
                    positions.pop(sym, None)
                    continue
            else:
                if high >= pos.stop_price:
                    exit_px = pos.stop_price * (1 + cfg.cost.slippage)
                    pnl = _unrealised_pnl(pos, exit_px)
                    fee = abs(exit_px * pos.qty) * cfg.cost.taker_fee
                    cash += pnl - fee
                    trades.append(Trade(
                        symbol=sym,
                        side=pos.side,
                        qty=pos.qty,
                        entry_time=str(idx[pos.opened_i]),
                        entry_price=pos.entry_price,
                        exit_time=str(idx[i]),
                        exit_price=exit_px,
                        pnl_usdt=pnl - fee - pos.funding_paid,
                        fees_usdt=pos.fees_paid + fee,
                        funding_usdt=pos.funding_paid,
                        tag=pos.tag + "|stop",
                    ))
                    positions.pop(sym, None)
                    continue
                if low <= pos.take_profit:
                    exit_px = pos.take_profit * (1 + cfg.cost.slippage)
                    pnl = _unrealised_pnl(pos, exit_px)
                    fee = abs(exit_px * pos.qty) * cfg.cost.taker_fee
                    cash += pnl - fee
                    trades.append(Trade(
                        symbol=sym,
                        side=pos.side,
                        qty=pos.qty,
                        entry_time=str(idx[pos.opened_i]),
                        entry_price=pos.entry_price,
                        exit_time=str(idx[i]),
                        exit_price=exit_px,
                        pnl_usdt=pnl - fee - pos.funding_paid,
                        fees_usdt=pos.fees_paid + fee,
                        funding_usdt=pos.funding_paid,
                        tag=pos.tag + "|tp",
                    ))
                    positions.pop(sym, None)
                    continue

            # Trailing stop for trend
            if "trend_breakout" in pos.tag and atr_ > 0:
                if pos.side == "long":
                    pos.highest = max(pos.highest, high)
                    trail = pos.highest - cfg.strategy.trail_atr_mult_trend * atr_
                    if trail > pos.stop_price * (1 + cfg.strategy.trail_update_min_gap):
                        pos.stop_price = max(pos.stop_price, trail)
                else:
                    pos.lowest = min(pos.lowest, low)
                    trail = pos.lowest + cfg.strategy.trail_atr_mult_trend * atr_
                    if trail < pos.stop_price * (1 - cfg.strategy.trail_update_min_gap):
                        pos.stop_price = min(pos.stop_price, trail)

        # Entries
        equity = mark_to_market(i)
        if can_open_new_trades(state) and len(positions) < cfg.risk.max_positions:
            for sym in selected:
                if sym in positions:
                    continue
                if len(positions) >= cfg.risk.max_positions:
                    break
                f = frames[sym]
                if i >= len(f) - 2:
                    continue
                sig = signal_at(f, i, cfg.strategy)
                if sig is None:
                    continue
                next_row = f.iloc[i + 1]
                if not np.isfinite(next_row["open"]):
                    continue

                entry = float(next_row["open"])
                side = sig.side
                entry_price = entry * (1 + cfg.cost.slippage if side == "long" else 1 - cfg.cost.slippage)

                atr_ = float(f["atr"].iloc[i] or 0.0)
                if atr_ <= 0:
                    continue

                # Recompute SL/TP from entry_price (live와 동일한 방식으로 리스크 일관성 유지)
                if sig.tag == "trend_breakout":
                    stop_mult = cfg.strategy.stop_atr_mult_trend
                    tp_r = cfg.strategy.tp_r_trend
                else:
                    stop_mult = cfg.strategy.stop_atr_mult_mr
                    tp_r = cfg.strategy.tp_r_mr

                if side == "long":
                    stop_price = entry_price - stop_mult * atr_
                    tp_price = entry_price + tp_r * (entry_price - stop_price)
                else:
                    stop_price = entry_price + stop_mult * atr_
                    tp_price = entry_price - tp_r * (stop_price - entry_price)

                used_notional = 0.0
                for _p in positions.values():
                    _rowp = frames[_p.symbol].iloc[i]
                    _px = float(_rowp["close"]) if np.isfinite(_rowp["close"]) else _p.entry_price
                    used_notional += abs(_p.qty * _px)

                qty = position_size_from_risk(cfg.risk, equity, entry_price, stop_price, side=side, used_notional=used_notional)
                if qty <= 0:
                    continue

                fee = abs(entry_price * qty) * cfg.cost.taker_fee
                cash -= fee

                positions[sym] = Position(
                    symbol=sym,
                    side=side,
                    qty=qty,
                    entry_price=entry_price,
                    stop_price=stop_price,
                    take_profit=tp_price,
                    tag=sig.tag,
                    opened_i=i + 1,
                    highest=entry_price,
                    lowest=entry_price,
                    fees_paid=fee,
                )

        equity_curve.append(mark_to_market(i))
        ts_list.append(ts)

    equity_series = pd.Series(equity_curve, index=ts_list, name="equity")
    trades_df = pd.DataFrame([t.to_dict() for t in trades])

    out = ensure_dir(out_dir)
    equity_series.to_csv(out / "equity.csv", header=True)
    trades_df.to_csv(out / "trades.csv", index=False)

    result = {
        "initial_equity": float(initial_equity),
        "final_equity": float(equity_series.iloc[-1]) if len(equity_series) else float(initial_equity),
        "total_return": float(equity_series.iloc[-1] / initial_equity - 1.0) if len(equity_series) else 0.0,
        "num_trades": int(len(trades)),
        "out_dir": str(out_dir),
    }
    return result
