from __future__ import annotations

import logging
from dataclasses import dataclass
from typing import Dict, List, Optional, Tuple

import numpy as np
import pandas as pd

from .config import SelectorConfig, BacktestConfig
from .data import get_ohlcv
from .indicators import atr

log = logging.getLogger("selector")


STABLE_BASES = {"USDT", "USDC", "BUSD", "TUSD", "FDUSD", "DAI", "EUR", "GBP", "TRY", "BRL"}


@dataclass
class SymbolScore:
    symbol: str
    score: float
    volume_usdt: float
    vol_pct: float
    funding: float


def _minmax(x: np.ndarray) -> np.ndarray:
    if len(x) == 0:
        return x
    mn, mx = np.nanmin(x), np.nanmax(x)
    if not np.isfinite(mn) or not np.isfinite(mx) or mx - mn < 1e-12:
        return np.zeros_like(x)
    return (x - mn) / (mx - mn)


def list_usdt_perps(ex, quote_asset: str = "USDT", only_perpetual: bool = True) -> List[str]:
    markets = ex.load_markets()
    syms = []
    for s, m in markets.items():
        try:
            if not m.get("active", True):
                continue
            if m.get("quote") != quote_asset:
                continue
            if m.get("linear") is not True:
                continue
            if only_perpetual and m.get("swap") is not True:
                continue
            base = m.get("base")
            if base in STABLE_BASES:
                continue
            # ccxt futures symbol has format "BTC/USDT:USDT"
            syms.append(s)
        except Exception:
            continue
    return sorted(list(set(syms)))


def fetch_funding(ex, symbol: str) -> float:
    """
    Returns current funding rate (per funding interval).
    If unavailable, returns 0.0.
    """
    try:
        fr = ex.fetch_funding_rate(symbol)
        # ccxt unified: {'fundingRate': 0.0001, ...}
        v = fr.get("fundingRate")
        if v is None:
            v = fr.get("fundingRatePercentage")
        return float(v or 0.0)
    except Exception:
        return 0.0


def rank_symbols(
    ex,
    cfg: SelectorConfig,
    bt_cfg: BacktestConfig,
    *,
    as_of_ms: Optional[int] = None,
    cache_dir: str,
) -> List[SymbolScore]:
    """
    1) 24h 거래대금(유동성) 큰 종목 위주
    2) 최근 변동성(ATR%) 충분한 종목 선호
    3) 펀딩이 과하게 불리한 종목 제외
    """
    symbols = list_usdt_perps(ex)
    if not symbols:
        return []

    # candidates by volume
    try:
        tickers = ex.fetch_tickers(symbols)
    except Exception as e:
        log.warning("fetch_tickers failed: %s", e)
        tickers = {}

    rows: List[Tuple[str, float]] = []
    for s in symbols:
        t = tickers.get(s) or {}
        qv = t.get("quoteVolume")
        vol = float(qv or 0.0)
        if vol <= 0:
            # fallback: baseVolume * last
            bv = float(t.get("baseVolume") or 0.0)
            last = float(t.get("last") or t.get("close") or 0.0)
            vol = bv * last
        rows.append((s, vol))

    rows.sort(key=lambda x: x[1], reverse=True)
    candidates = [s for s, v in rows[: cfg.candidates]]

    # compute volatility score from OHLCV
    vol_usdts = []
    vol_pcts = []
    fundings = []
    for s, v in rows[: cfg.candidates]:
        vol_usdts.append(v)
        funding = fetch_funding(ex, s)
        fundings.append(funding)

        # funding filter (absolute)
        if cfg.max_abs_funding is not None and abs(funding) > cfg.max_abs_funding:
            continue

    # Evaluate only candidates (after initial filter)
    eval_syms = candidates
    metrics = []
    for s in eval_syms:
        try:
            # lookback volatility_lookback_bars
            # We request slightly more bars to ensure indicator warmup
            bars = cfg.volatility_lookback_bars + 50
            # derive start_ms from as_of_ms or now using last candle timestamps from exchange
            # For simplicity, just fetch last N bars via since=None is not supported uniformly,
            # so we approximate using now - bars*timeframe
            now_ms = as_of_ms if as_of_ms is not None else ex.milliseconds()
            tf_ms = ex.parse_timeframe(bt_cfg.timeframe) * 1000
            start_ms = now_ms - bars * tf_ms
            df = get_ohlcv(ex, s, bt_cfg.timeframe, start_ms, as_of_ms, cache_dir=cache_dir, limit=bt_cfg.ohlcv_limit)
            if len(df) < cfg.volatility_lookback_bars // 2:
                continue
            a = atr(df, 14).iloc[-1]
            c = float(df["close"].iloc[-1])
            vol_pct = float(a / (c + 1e-12)) * 100.0
            funding = fetch_funding(ex, s)
            if cfg.max_abs_funding is not None and abs(funding) > cfg.max_abs_funding:
                continue
            metrics.append((s, vol_pct, funding))
        except Exception:
            continue

    if not metrics:
        return []

    # Map volume for metrics
    vol_map = dict(rows)
    vols = np.array([vol_map.get(s, 0.0) for s, _, _ in metrics], dtype=float)
    vps = np.array([vp for _, vp, _ in metrics], dtype=float)

    vols_n = _minmax(np.log1p(vols))
    vps_n = _minmax(vps)

    scores = cfg.w_volume * vols_n + cfg.w_volatility * vps_n

    out: List[SymbolScore] = []
    for (s, vol_pct, funding), sc, volu in zip(metrics, scores, vols):
        out.append(SymbolScore(symbol=s, score=float(sc), volume_usdt=float(volu), vol_pct=float(vol_pct), funding=float(funding)))

    out.sort(key=lambda x: x.score, reverse=True)
    return out[: cfg.top_n]
