from __future__ import annotations

from dataclasses import dataclass
from typing import Optional, Tuple

import pandas as pd

from .config import StrategyConfig
from .indicators import adx, atr, bollinger, donchian, ema, rsi


@dataclass
class Signal:
    side: str  # "long" or "short"
    stop_price: float
    take_profit: float
    tag: str  # strategy name / regime


def add_indicators(df: pd.DataFrame, cfg: StrategyConfig) -> pd.DataFrame:
    out = df.copy()
    out["ema_fast"] = ema(out["close"], cfg.ema_fast)
    out["ema_slow"] = ema(out["close"], cfg.ema_slow)
    out["atr"] = atr(out, cfg.atr_len)
    out["adx"] = adx(out, cfg.adx_len)

    hi, lo = donchian(out, cfg.donchian_window)
    out["don_hi"] = hi
    out["don_lo"] = lo

    out["rsi"] = rsi(out["close"], cfg.rsi_len)
    mid, ub, lb = bollinger(out["close"], cfg.bb_len, cfg.bb_std)
    out["bb_mid"] = mid
    out["bb_ub"] = ub
    out["bb_lb"] = lb
    return out


def _trend_signal(row, prev, cfg: StrategyConfig) -> Optional[Signal]:
    # Trend filter
    if pd.isna(row["adx"]) or pd.isna(row["ema_fast"]) or pd.isna(row["ema_slow"]) or pd.isna(prev["don_hi"]) or pd.isna(prev["don_lo"]):
        return None
    if row["adx"] < cfg.adx_trend_min:
        return None

    c = float(row["close"])
    atr_ = float(row["atr"])

    up_trend = row["ema_fast"] > row["ema_slow"]
    down_trend = row["ema_fast"] < row["ema_slow"]

    # Breakout on close above previous channel
    if up_trend and c > float(prev["don_hi"]):
        stop = c - cfg.stop_atr_mult_trend * atr_
        tp = c + cfg.tp_r_trend * (c - stop)
        return Signal("long", stop, tp, tag="trend_breakout")
    if down_trend and c < float(prev["don_lo"]):
        stop = c + cfg.stop_atr_mult_trend * atr_
        tp = c - cfg.tp_r_trend * (stop - c)
        return Signal("short", stop, tp, tag="trend_breakout")
    return None


def _mr_signal(row, cfg: StrategyConfig) -> Optional[Signal]:
    if pd.isna(row["rsi"]) or pd.isna(row["bb_ub"]) or pd.isna(row["bb_lb"]) or pd.isna(row["adx"]):
        return None
    # Range regime preference
    if row["adx"] > cfg.adx_range_max:
        return None

    c = float(row["close"])
    atr_ = float(row["atr"])
    r = float(row["rsi"])

    if c < float(row["bb_lb"]) and r < cfg.rsi_low:
        stop = c - cfg.stop_atr_mult_mr * atr_
        tp = c + cfg.tp_r_mr * (c - stop)
        return Signal("long", stop, tp, tag="mean_reversion")
    if c > float(row["bb_ub"]) and r > cfg.rsi_high:
        stop = c + cfg.stop_atr_mult_mr * atr_
        tp = c - cfg.tp_r_mr * (stop - c)
        return Signal("short", stop, tp, tag="mean_reversion")
    return None


def signal_at(df_feat: pd.DataFrame, i: int, cfg: StrategyConfig) -> Optional[Signal]:
    """
    i 시점(캔들 close 기준)에서의 진입 신호.
    실제 체결은 i+1 open에서 발생하도록 백테스트에서 처리.
    """
    if i <= 0 or i >= len(df_feat):
        return None
    row = df_feat.iloc[i]
    prev = df_feat.iloc[i-1]
    # Prefer trend when ADX high, otherwise mean reversion
    sig = _trend_signal(row, prev, cfg)
    if sig is not None:
        return sig
    sig = _mr_signal(row, cfg)
    return sig
