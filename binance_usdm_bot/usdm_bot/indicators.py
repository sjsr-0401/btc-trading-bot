from __future__ import annotations

import numpy as np
import pandas as pd


def ema(s: pd.Series, length: int) -> pd.Series:
    return s.ewm(span=length, adjust=False).mean()


def sma(s: pd.Series, length: int) -> pd.Series:
    return s.rolling(length).mean()


def true_range(df: pd.DataFrame) -> pd.Series:
    prev_close = df["close"].shift(1)
    tr = pd.concat([
        (df["high"] - df["low"]).abs(),
        (df["high"] - prev_close).abs(),
        (df["low"] - prev_close).abs(),
    ], axis=1).max(axis=1)
    return tr


def atr(df: pd.DataFrame, length: int = 14) -> pd.Series:
    tr = true_range(df)
    # Wilder's smoothing
    return tr.ewm(alpha=1/length, adjust=False).mean()


def rsi(close: pd.Series, length: int = 14) -> pd.Series:
    delta = close.diff()
    up = delta.clip(lower=0.0)
    down = (-delta).clip(lower=0.0)
    roll_up = up.ewm(alpha=1/length, adjust=False).mean()
    roll_down = down.ewm(alpha=1/length, adjust=False).mean()
    rs = roll_up / (roll_down + 1e-12)
    return 100.0 - (100.0 / (1.0 + rs))


def bollinger(close: pd.Series, length: int = 20, std: float = 2.0):
    mid = sma(close, length)
    dev = close.rolling(length).std(ddof=0)
    upper = mid + std * dev
    lower = mid - std * dev
    return mid, upper, lower


def donchian(df: pd.DataFrame, window: int = 20):
    hi = df["high"].rolling(window).max()
    lo = df["low"].rolling(window).min()
    return hi, lo


def adx(df: pd.DataFrame, length: int = 14) -> pd.Series:
    """
    Simplified ADX (Wilder).
    Returns ADX series.
    """
    high = df["high"]
    low = df["low"]
    close = df["close"]

    up_move = high.diff()
    down_move = -low.diff()

    plus_dm = np.where((up_move > down_move) & (up_move > 0), up_move, 0.0)
    minus_dm = np.where((down_move > up_move) & (down_move > 0), down_move, 0.0)

    tr = true_range(df)
    atr_ = tr.ewm(alpha=1/length, adjust=False).mean()

    plus_di = 100.0 * pd.Series(plus_dm, index=df.index).ewm(alpha=1/length, adjust=False).mean() / (atr_ + 1e-12)
    minus_di = 100.0 * pd.Series(minus_dm, index=df.index).ewm(alpha=1/length, adjust=False).mean() / (atr_ + 1e-12)

    dx = (100.0 * (plus_di - minus_di).abs() / ((plus_di + minus_di) + 1e-12))
    adx_ = dx.ewm(alpha=1/length, adjust=False).mean()
    return adx_
