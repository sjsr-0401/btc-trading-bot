from __future__ import annotations

import logging
from typing import Any, Dict, List, Optional, Tuple

import ccxt  # type: ignore

from .config import ExchangeConfig

log = logging.getLogger("exchange")


def make_exchange(cfg: ExchangeConfig):
    klass = getattr(ccxt, cfg.exchange_id)
    ex = klass({
        "apiKey": cfg.api_key,
        "secret": cfg.api_secret,
        "enableRateLimit": cfg.enable_rate_limit,
        "timeout": cfg.timeout_ms,
        "options": {
            "recvWindow": cfg.recv_window_ms,
        },
    })

    # Binance futures testnet
    if cfg.testnet:
        try:
            ex.set_sandbox_mode(True)
        except Exception:
            # fallback: override URLs (ccxt 버전에 따라 다름)
            if "urls" in ex.__dict__ and "api" in ex.urls:
                # For binance futures testnet, the base domain differs
                ex.urls["api"] = {
                    "public": "https://testnet.binancefuture.com/fapi/v1",
                    "private": "https://testnet.binancefuture.com/fapi/v1",
                }

    return ex


def safe_symbol(ex, symbol: str) -> str:
    # ccxt uses "BTC/USDT:USDT" for USDT-M swaps
    if ":" in symbol:
        return symbol
    if symbol.endswith("/USDT"):
        return symbol + ":USDT"
    return symbol


def load_markets(ex) -> Dict[str, Any]:
    return ex.load_markets()


def set_leverage(ex, symbol: str, leverage: int) -> None:
    try:
        ex.set_leverage(leverage, symbol)
    except Exception as e:
        log.warning("set_leverage failed %s %s", symbol, e)


def set_margin_mode(ex, symbol: str, mode: str = "isolated") -> None:
    try:
        ex.set_margin_mode(mode, symbol)
    except Exception as e:
        log.warning("set_margin_mode failed %s %s", symbol, e)


def fetch_balance_usdt(ex) -> Tuple[float, float]:
    """
    Returns (equity, available) in USDT.
    """
    bal = ex.fetch_balance()
    total = float(bal.get("total", {}).get("USDT", 0.0) or 0.0)
    free = float(bal.get("free", {}).get("USDT", 0.0) or 0.0)
    if total == 0.0 and "info" in bal:
        # try futures wallet fields
        info = bal["info"]
        for k in ("totalWalletBalance", "totalMarginBalance"):
            if k in info:
                total = float(info[k])
                break
        for k in ("availableBalance",):
            if k in info:
                free = float(info[k])
                break
    return total, free


def fetch_positions(ex) -> List[Dict[str, Any]]:
    try:
        return ex.fetch_positions()
    except Exception as e:
        log.warning("fetch_positions failed: %s", e)
        return []


def create_market(ex, symbol: str, side: str, amount: float, params: Optional[Dict[str, Any]] = None) -> Dict[str, Any]:
    params = params or {}
    return ex.create_order(symbol, "market", side, amount, None, params)


def create_stop_market(
    ex,
    symbol: str,
    side: str,
    amount: float,
    stop_price: float,
    *,
    reduce_only: bool = True,
    working_type: str = "MARK_PRICE",
) -> Dict[str, Any]:
    params = {
        "stopPrice": float(stop_price),
        "reduceOnly": reduce_only,
        "workingType": working_type,
    }
    return ex.create_order(symbol, "STOP_MARKET", side, amount, None, params)


def create_take_profit_market(
    ex,
    symbol: str,
    side: str,
    amount: float,
    stop_price: float,
    *,
    reduce_only: bool = True,
    working_type: str = "MARK_PRICE",
) -> Dict[str, Any]:
    params = {
        "stopPrice": float(stop_price),
        "reduceOnly": reduce_only,
        "workingType": working_type,
    }
    return ex.create_order(symbol, "TAKE_PROFIT_MARKET", side, amount, None, params)


def cancel_all_orders(ex, symbol: str) -> None:
    try:
        ex.cancel_all_orders(symbol)
    except Exception:
        try:
            orders = ex.fetch_open_orders(symbol)
            for o in orders:
                try:
                    ex.cancel_order(o["id"], symbol)
                except Exception:
                    pass
        except Exception:
            pass


def fetch_open_orders(ex, symbol: str) -> List[Dict[str, Any]]:
    try:
        return ex.fetch_open_orders(symbol)
    except Exception:
        return []


def fetch_ticker_last(ex, symbol: str) -> float:
    t = ex.fetch_ticker(symbol)
    return float(t.get("last") or t.get("close") or 0.0)


def market_min_amount(ex, symbol: str) -> float:
    m = ex.market(symbol)
    limits = m.get("limits", {}).get("amount", {}) or {}
    return float(limits.get("min") or 0.0)
