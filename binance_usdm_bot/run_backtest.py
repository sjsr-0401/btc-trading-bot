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
from usdm_bot.backtest import run_backtest
from usdm_bot.utils import setup_logging, ensure_dir


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--config", default="config.yaml", help="config yaml path")
    ap.add_argument("--days", type=int, default=None, help="in-sample days (default: config.backtest.in_sample_days)")
    ap.add_argument("--candidates", type=int, default=None, help="universe size (default: config.selector.candidates)")
    ap.add_argument("--out", default="bt_out", help="output directory")
    args = ap.parse_args()

    cfg = load_config(args.config)
    setup_logging()

    days = args.days or cfg.backtest.in_sample_days
    if args.candidates is not None:
        cfg.selector.candidates = args.candidates

    ex = make_exchange(cfg.exchange)
    ex.load_markets()

    end = datetime.now(timezone.utc).replace(second=0, microsecond=0)
    start = end - timedelta(days=days)

    # Universe: current top by volume (live proxy)
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

    print(f"Universe size={len(universe)} (top by volume). Fetching OHLCV...")

    cache_dir = cfg.backtest.cache_dir
    data_map = {}
    start_ms = int(start.timestamp() * 1000)
    end_ms = int(end.timestamp() * 1000)
    for s in universe:
        df = get_ohlcv(ex, s, cfg.backtest.timeframe, start_ms, end_ms, cache_dir=cache_dir, limit=cfg.backtest.ohlcv_limit)
        data_map[s] = df

    out_dir = str(Path(args.out) / end.strftime("%Y%m%d_%H%M%S"))
    res = run_backtest(cfg, data_map, start=start, end=end, initial_equity=850.0, out_dir=out_dir)

    eq = pd.read_csv(Path(res["out_dir"]) / "equity.csv", index_col=0, parse_dates=True)["equity"]
    tr = pd.read_csv(Path(res["out_dir"]) / "trades.csv")
    summ = summarize(eq, tr)

    print("\n=== Backtest Summary ===")
    for k, v in summ.items():
        print(f"{k}: {v}")
    print(f"\nOutputs saved to: {res['out_dir']}")


if __name__ == "__main__":
    main()
