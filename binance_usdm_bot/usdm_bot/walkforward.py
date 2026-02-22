from __future__ import annotations

import copy
import logging
from dataclasses import asdict
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Dict, List, Tuple

import pandas as pd

from .backtest import run_backtest
from .config import BotConfig
from .metrics import summarize
from .utils import ensure_dir, write_json

log = logging.getLogger("walkforward")


def _days(n: int) -> timedelta:
    return timedelta(days=int(n))


def _grid() -> List[Tuple[float, float, float, float]]:
    """
    (stop_trend, tp_trend, stop_mr, tp_mr)
    작은 그리드 (과최적화 방지 + 속도)
    """
    grid = []
    for stop_t in (2.6, 3.0, 3.4):
        for tp_t in (2.0, 2.4):
            for stop_m in (2.0, 2.4):
                for tp_m in (1.2, 1.6):
                    grid.append((stop_t, tp_t, stop_m, tp_m))
    return grid


def walk_forward(
    cfg: BotConfig,
    data_map: Dict[str, pd.DataFrame],
    *,
    end: datetime,
    initial_equity: float = 850.0,
    out_dir: str = "wf_out",
) -> Dict:
    """
    최근 walk_forward_days 동안:
      - train(wf_train_days)에서 파라미터 그리드 선택
      - test(wf_test_days)에 적용
      - 다음 윈도우로 이동
    """
    out = ensure_dir(out_dir)

    wf_days = cfg.backtest.walk_forward_days
    train_days = cfg.backtest.wf_train_days
    test_days = cfg.backtest.wf_test_days

    wf_start = end - _days(wf_days)
    step = _days(test_days)
    train_len = _days(train_days)

    t0 = wf_start
    equity = float(initial_equity)

    all_steps = []
    combined_equity = []
    combined_trades = []

    grid = _grid()

    k = 0
    while t0 + train_len + step <= end:
        train_start = t0
        train_end = t0 + train_len
        test_start = train_end
        test_end = train_end + step

        k += 1
        step_dir = out / f"step_{k:02d}"
        ensure_dir(step_dir)

        best_score = -1e18
        best_params = None
        best_train_summary = None

        # Train: parameter selection
        for (stop_t, tp_t, stop_m, tp_m) in grid:
            c = copy.deepcopy(cfg)
            c.strategy.stop_atr_mult_trend = stop_t
            c.strategy.tp_r_trend = tp_t
            c.strategy.stop_atr_mult_mr = stop_m
            c.strategy.tp_r_mr = tp_m

            r = run_backtest(c, data_map, start=train_start, end=train_end, initial_equity=equity, out_dir=str(step_dir / "train_tmp"))
            eq = pd.read_csv(Path(r["out_dir"]) / "equity.csv", index_col=0, parse_dates=True)["equity"]
            tr = pd.read_csv(Path(r["out_dir"]) / "trades.csv")
            summ = summarize(eq, tr)

            # scoring: return - penalty
            score = summ["total_return"] - 0.7 * abs(summ["max_drawdown"]) + 0.05 * summ.get("sharpe_daily", 0.0)
            if score > best_score:
                best_score = score
                best_params = (stop_t, tp_t, stop_m, tp_m)
                best_train_summary = summ

        assert best_params is not None

        # Test: run with best params and carry equity
        c = copy.deepcopy(cfg)
        c.strategy.stop_atr_mult_trend, c.strategy.tp_r_trend, c.strategy.stop_atr_mult_mr, c.strategy.tp_r_mr = best_params

        r_test = run_backtest(c, data_map, start=test_start, end=test_end, initial_equity=equity, out_dir=str(step_dir / "test"))
        eq_path = Path(r_test["out_dir"]) / "equity.csv"
        tr_path = Path(r_test["out_dir"]) / "trades.csv"
        eq = pd.read_csv(eq_path, index_col=0, parse_dates=True)["equity"]
        tr = pd.read_csv(tr_path)
        summ_test = summarize(eq, tr)

        equity = float(eq.iloc[-1]) if len(eq) else equity

        all_steps.append({
            "k": k,
            "train_start": str(train_start),
            "train_end": str(train_end),
            "test_start": str(test_start),
            "test_end": str(test_end),
            "best_params": {
                "stop_atr_mult_trend": best_params[0],
                "tp_r_trend": best_params[1],
                "stop_atr_mult_mr": best_params[2],
                "tp_r_mr": best_params[3],
            },
            "train_summary": best_train_summary,
            "test_summary": summ_test,
            "test_final_equity": equity,
        })

        combined_equity.append(eq)
        combined_trades.append(tr)

        t0 = t0 + step

    # Combine outputs
    if combined_equity:
        eq_all = pd.concat(combined_equity).sort_index()
        eq_all = eq_all[~eq_all.index.duplicated(keep="last")]
        eq_all.to_csv(out / "equity_combined.csv", header=True)

    if combined_trades:
        tr_all = pd.concat(combined_trades, ignore_index=True)
        tr_all.to_csv(out / "trades_combined.csv", index=False)

    write_json(out / "walkforward_steps.json", {"steps": all_steps})

    return {
        "wf_start": str(wf_start),
        "wf_end": str(end),
        "steps": len(all_steps),
        "final_equity": equity,
        "out_dir": str(out_dir),
    }
