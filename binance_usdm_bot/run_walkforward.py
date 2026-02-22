#!/usr/bin/env python3
from __future__ import annotations

import argparse
from datetime import datetime, timedelta, timezone
from pathlib import Path

import pandas as pd

from usdm_bot.config import load_config
from usdm_bot.data import get_ohlcv
from usdm_bot.exchange import make_exchange
from usdm_bot.metrics import summarize
from usdm_bot.selector import list_usdt_perps
from usdm_bot.walkforward import walk_forward
from usdm_bot.utils import setup_logging


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--config", default="config.yaml")
    ap.add_argument("--candidates", type=int, default=None, help="universe size (default: config.selector.candidates)")
    ap.add_argument("--out", default="wf_out")
    args = ap.parse_args()

    cfg = load_config(args.config)
    setup_logging()

    if args.candidates is not None:
        cfg.selector.candidates = args.candidates

    ex = make_exchange(cfg.exchange)
    ex.load_markets()

    end = datetime.now(timezone.utc).replace(second=0, microsecond=0)

    # Need data for: walk_forward_days + wf_train_days + warmup buffer
    warmup_days = 10
    total_days = cfg.backtest.walk_forward_days + cfg.backtest.wf_train_days + warmup_days
    start = end - timedelta(days=total_days)

    symbols = list_usdt_perps(ex, quote_asset=cfg.quote_asset, only_perpetual=cfg.only_perpetual)
    tickers = ex.fetch_tickers(symbols)
    vols = []
    for s in symbols:
        t = tickers.get(s) or {}
        qv = float(t.get("quoteVolume") or 0.0)
        if qv <= 0:
            bv = float(t.get("baseVolume") or 0.0)
            last = float(t.get("last") or t.get("close") or 0.0)
            qv = bv * last
        vols.append((s, qv))
    vols.sort(key=lambda x: x[1], reverse=True)
    universe = [s for s, _ in vols[: cfg.selector.candidates]]

    print(f"Universe size={len(universe)}. Fetching OHLCV from {start} to {end} ...")

    cache_dir = cfg.backtest.cache_dir
    data_map = {}
    start_ms = int(start.timestamp() * 1000)
    end_ms = int(end.timestamp() * 1000)
    for s in universe:
        df = get_ohlcv(ex, s, cfg.backtest.timeframe, start_ms, end_ms, cache_dir=cache_dir, limit=cfg.backtest.ohlcv_limit)
        data_map[s] = df

    out_dir = str(Path(args.out) / end.strftime("%Y%m%d_%H%M%S"))
    res = walk_forward(cfg, data_map, end=end, initial_equity=850.0, out_dir=out_dir)

    # If combined equity exists, print summary
    comb = Path(res["out_dir"]) / "equity_combined.csv"
    if comb.exists():
        eq = pd.read_csv(comb, index_col=0, parse_dates=True)["equity"]
        tr_path = Path(res["out_dir"]) / "trades_combined.csv"
        tr = pd.read_csv(tr_path) if tr_path.exists() else pd.DataFrame()
        summ = summarize(eq, tr)
        print("\n=== WalkForward Combined Summary ===")
        for k, v in summ.items():
            print(f"{k}: {v}")

    print(f"\nOutputs saved to: {res['out_dir']}")


if __name__ == "__main__":
    main()
