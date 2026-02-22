from __future__ import annotations

import logging
from pathlib import Path
from typing import Optional

import pandas as pd

from .utils import ensure_dir

log = logging.getLogger("data")


def cache_path(cache_dir: str, symbol: str, timeframe: str) -> Path:
    safe = symbol.replace("/", "_").replace(":", "_")
    return Path(cache_dir) / f"{safe}_{timeframe}.csv"


def _to_df(rows) -> pd.DataFrame:
    df = pd.DataFrame(rows, columns=["ts", "open", "high", "low", "close", "volume"])
    df["datetime"] = pd.to_datetime(df["ts"], unit="ms", utc=True)
    df = df.drop(columns=["ts"])
    df = df.set_index("datetime")
    df = df.sort_index()
    return df


def load_cache(path: Path) -> Optional[pd.DataFrame]:
    if not path.exists():
        return None
    try:
        df = pd.read_csv(path)
        df["datetime"] = pd.to_datetime(df["datetime"], utc=True)
        df = df.set_index("datetime").sort_index()
        for c in ["open", "high", "low", "close", "volume"]:
            df[c] = pd.to_numeric(df[c], errors="coerce")
        df = df.dropna()
        return df
    except Exception as e:
        log.warning("cache read failed %s: %s", path, e)
        return None


def save_cache(df: pd.DataFrame, path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    out = df.copy()
    out = out.reset_index()
    if "datetime" not in out.columns:
        out = out.rename(columns={"index": "datetime"})
    out["datetime"] = out["datetime"].astype(str)
    out.to_csv(path, index=False)


def fetch_ohlcv_history(
    ex,
    symbol: str,
    timeframe: str,
    start_ms: int,
    end_ms: Optional[int],
    *,
    limit: int = 1500,
    max_batches: int = 10_000,
) -> pd.DataFrame:
    """
    CCXT fetch_ohlcv 페이징. end_ms가 None이면 가능한 만큼.
    """
    all_rows = []
    since = int(start_ms)
    batches = 0

    while True:
        rows = ex.fetch_ohlcv(symbol, timeframe=timeframe, since=since, limit=limit)
        batches += 1
        if not rows:
            break

        all_rows.extend(rows)
        last_ts = rows[-1][0]
        since = int(last_ts) + 1

        if end_ms is not None and since >= int(end_ms):
            break
        if len(rows) < limit:
            break
        if batches >= max_batches:
            log.warning("max_batches reached for %s", symbol)
            break

    df = _to_df(all_rows)
    df = df[~df.index.duplicated(keep="last")]
    if end_ms is not None:
        end_dt = pd.to_datetime(end_ms, unit="ms", utc=True)
        df = df[df.index <= end_dt]
    return df


def get_ohlcv(
    ex,
    symbol: str,
    timeframe: str,
    start_ms: int,
    end_ms: Optional[int],
    *,
    cache_dir: str,
    limit: int = 1500,
    force_refresh: bool = False,
) -> pd.DataFrame:
    """
    캐시를 우선 사용하고, 부족하면 추가로 fetch 후 병합.
    """
    ensure_dir(cache_dir)
    path = cache_path(cache_dir, symbol, timeframe)

    cached = None if force_refresh else load_cache(path)
    need_fetch = True

    if cached is not None and not cached.empty:
        cached_start = int(cached.index[0].timestamp() * 1000)
        cached_end = int(cached.index[-1].timestamp() * 1000)
        # 캐시가 원하는 구간을 완전히 포함하면 그대로 사용
        if cached_start <= start_ms and (end_ms is None or cached_end >= end_ms):
            need_fetch = False
        else:
            need_fetch = True

    if need_fetch:
        log.info("fetching OHLCV %s %s", symbol, timeframe)
        fresh = fetch_ohlcv_history(ex, symbol, timeframe, start_ms, end_ms, limit=limit)
        if cached is not None and not cached.empty:
            df = pd.concat([cached, fresh]).sort_index()
            df = df[~df.index.duplicated(keep="last")]
        else:
            df = fresh
        save_cache(df, path)
    else:
        df = cached

    if df is None:
        raise RuntimeError("OHLCV not available")

    start_dt = pd.to_datetime(start_ms, unit="ms", utc=True)
    df = df[df.index >= start_dt]
    if end_ms is not None:
        end_dt = pd.to_datetime(end_ms, unit="ms", utc=True)
        df = df[df.index <= end_dt]
    return df.copy()
