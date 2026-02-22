from __future__ import annotations

from dataclasses import dataclass
from typing import Dict, Tuple

import numpy as np
import pandas as pd


def max_drawdown(equity: pd.Series) -> Tuple[float, pd.Timestamp, pd.Timestamp]:
    if equity.empty:
        return 0.0, None, None  # type: ignore
    peak = equity.cummax()
    dd = equity / (peak + 1e-12) - 1.0
    mdd = float(dd.min())
    end = dd.idxmin()
    start = equity.loc[:end].idxmax()
    return mdd, start, end


def cagr(equity: pd.Series) -> float:
    if len(equity) < 2:
        return 0.0
    start = float(equity.iloc[0])
    end = float(equity.iloc[-1])
    days = (equity.index[-1] - equity.index[0]).total_seconds() / 86400.0
    if days <= 0:
        return 0.0
    years = days / 365.0
    return (end / (start + 1e-12)) ** (1.0 / years) - 1.0


def sharpe_daily(equity: pd.Series) -> float:
    if len(equity) < 10:
        return 0.0
    daily = equity.resample("1D").last().pct_change().dropna()
    if daily.std(ddof=0) == 0:
        return 0.0
    return float((daily.mean() / daily.std(ddof=0)) * np.sqrt(365))


def trade_stats(trades: pd.DataFrame) -> Dict:
    if trades is None or trades.empty:
        return {"win_rate": 0.0, "profit_factor": 0.0, "avg_pnl": 0.0, "num_trades": 0}
    pnl = trades["pnl_usdt"].astype(float)
    wins = pnl[pnl > 0]
    losses = pnl[pnl < 0]
    win_rate = float((pnl > 0).mean())
    profit_factor = float(wins.sum() / (-losses.sum() + 1e-12)) if len(losses) else float("inf")
    return {
        "num_trades": int(len(trades)),
        "win_rate": win_rate,
        "profit_factor": profit_factor,
        "avg_pnl": float(pnl.mean()),
        "median_pnl": float(pnl.median()),
        "avg_fee": float(trades["fees_usdt"].astype(float).mean()) if "fees_usdt" in trades.columns else 0.0,
        "avg_funding": float(trades["funding_usdt"].astype(float).mean()) if "funding_usdt" in trades.columns else 0.0,
    }


def summarize(equity: pd.Series, trades: pd.DataFrame) -> Dict:
    mdd, dd_start, dd_end = max_drawdown(equity)
    return {
        "total_return": float(equity.iloc[-1] / equity.iloc[0] - 1.0) if len(equity) else 0.0,
        "cagr": float(cagr(equity)),
        "max_drawdown": float(mdd),
        "dd_start": str(dd_start) if dd_start is not None else "",
        "dd_end": str(dd_end) if dd_end is not None else "",
        "sharpe_daily": float(sharpe_daily(equity)),
        **trade_stats(trades),
    }
