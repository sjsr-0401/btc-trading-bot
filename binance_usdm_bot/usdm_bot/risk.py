from __future__ import annotations

from dataclasses import dataclass, asdict
from datetime import datetime, timezone
from typing import Dict, Optional, Tuple

from .config import RiskConfig


@dataclass
class RiskState:
    day: str = ""  # YYYY-MM-DD in UTC
    day_start_equity: float = 0.0
    peak_equity: float = 0.0
    daily_halted: bool = False
    kill_halted: bool = False

    def to_dict(self) -> Dict:
        return asdict(self)

    @classmethod
    def from_dict(cls, d: Dict) -> "RiskState":
        return cls(**{k: d.get(k) for k in cls.__dataclass_fields__.keys()})


def _utc_day(ts: datetime) -> str:
    return ts.astimezone(timezone.utc).strftime("%Y-%m-%d")


def update_risk_state(state: RiskState, cfg: RiskConfig, equity: float, ts: datetime) -> Tuple[RiskState, Dict[str, float]]:
    """
    Update daily start / peak and compute triggers.
    Returns (state, metrics)
    """
    day = _utc_day(ts)
    metrics = {}

    if state.day != day:
        state.day = day
        state.day_start_equity = equity
        state.daily_halted = False

    if state.peak_equity <= 0:
        state.peak_equity = equity
    else:
        state.peak_equity = max(state.peak_equity, equity)

    daily_ret = (equity - state.day_start_equity) / (state.day_start_equity + 1e-12)
    dd = (equity - state.peak_equity) / (state.peak_equity + 1e-12)
    metrics["daily_return"] = daily_ret
    metrics["drawdown_from_peak"] = dd

    if daily_ret <= -abs(cfg.daily_loss_limit):
        state.daily_halted = True
    if dd <= -abs(cfg.kill_switch_max_drawdown):
        state.kill_halted = True

    return state, metrics


def can_open_new_trades(state: RiskState) -> bool:
    return not (state.daily_halted or state.kill_halted)


def position_size_from_risk(
    cfg: RiskConfig,
    equity: float,
    entry_price: float,
    stop_price: float,
    *,
    side: str,
    used_notional: float = 0.0,
) -> float:
    """
    1회 리스크(per_trade_risk) 기반 포지션 수량 산정.

    - 손절까지 손실이 equity * per_trade_risk 가 되도록 수량 결정
    - per-position 마진 한도: equity * max_margin_fraction
    - total 마진 한도: equity * max_total_margin_fraction (이미 사용 중인 notional 고려)
    """
    entry = float(entry_price)
    stop = float(stop_price)
    if entry <= 0:
        return 0.0

    if side == "long":
        stop_dist = entry - stop
    else:
        stop_dist = stop - entry
    if stop_dist <= 0:
        return 0.0

    equity = float(equity)
    risk_usdt = equity * float(cfg.per_trade_risk)
    qty_by_risk = risk_usdt / stop_dist

    # per-position margin cap
    max_notional_pos = equity * float(cfg.max_margin_fraction) * float(cfg.leverage)
    qty_by_pos_cap = max_notional_pos / entry

    # total margin cap across positions
    used_notional = abs(float(used_notional))
    max_notional_total = equity * float(getattr(cfg, "max_total_margin_fraction", 0.85)) * float(cfg.leverage)
    remaining_notional = max(0.0, max_notional_total - used_notional)
    qty_by_total_cap = remaining_notional / entry

    qty = min(qty_by_risk, qty_by_pos_cap, qty_by_total_cap)

    notional = qty * entry
    if notional < cfg.min_notional_usdt:
        return 0.0
    return float(qty)
