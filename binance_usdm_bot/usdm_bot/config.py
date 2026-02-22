from __future__ import annotations

import os
from dataclasses import dataclass, field, asdict
from typing import Any, Dict, Optional

import yaml


def _env(name: str, default: Optional[str] = None) -> Optional[str]:
    v = os.getenv(name)
    return v if v is not None and v != "" else default


def _is_dataclass_instance(obj: Any) -> bool:
    return hasattr(obj, "__dataclass_fields__")


@dataclass
class ExchangeConfig:
    exchange_id: str = "binanceusdm"  # ccxt exchange id
    api_key: str = ""
    api_secret: str = ""
    testnet: bool = False
    enable_rate_limit: bool = True
    timeout_ms: int = 20000
    recv_window_ms: int = 5000


@dataclass
class DiscordConfig:
    enabled: bool = False
    webhook_url: str = ""
    username: str = "USDM-Bot"
    mention: str = ""  # e.g. "<@USER_ID>" or "<@&ROLE_ID>"


@dataclass
class CostConfig:
    """
    현실적인 비용 모델(보수적):
    - VIP0 가정: maker≈0.02%, taker≈0.04% (거래소/티어/프로모션에 따라 달라질 수 있음)
    - 슬리피지: 1~3bp/side 수준을 기본값으로 두고, 시장 상황에 따라 조정
    - 펀딩: 기본 8시간마다 정산(계약별/상황별로 1시간으로 바뀔 수 있음). '순 지불' 가정치를 multiplier로 반영.
    """
    maker_fee: float = 0.0002
    taker_fee: float = 0.0004
    slippage: float = 0.00015  # per side, fraction
    funding_rate_per_8h: float = 0.0001  # 0.01% per 8h (reference baseline)
    funding_cost_multiplier: float = 0.5  # 0..1 (0.5 = 절반 정도는 펀딩 지불했다고 가정)


@dataclass
class RiskConfig:
    leverage: int = 3
    per_trade_risk: float = 0.0125  # 1.25% of equity
    max_positions: int = 4
    max_margin_fraction: float = 0.35  # per position margin cap (equity * this)
    max_total_margin_fraction: float = 0.85  # total margin cap across positions (equity * this)
    daily_loss_limit: float = 0.04  # 4% of day-start equity
    close_on_daily_stop: bool = True
    kill_switch_max_drawdown: float = 0.30  # 30% from peak => halt (live)
    min_notional_usdt: float = 10.0  # set conservative; exchange minimum varies


@dataclass
class SelectorConfig:
    top_n: int = 10
    candidates: int = 40
    max_abs_funding: float = 0.002  # 0.2% per 8h (very high)
    refresh_interval_sec: int = 60 * 60 * 6  # 6 hours
    w_volume: float = 0.6
    w_volatility: float = 0.4
    volatility_lookback_bars: int = 288  # 24h on 5m


@dataclass
class StrategyConfig:
    # Regime thresholds (ADX)
    adx_len: int = 14
    adx_trend_min: float = 22.0
    adx_range_max: float = 18.0

    # Trend breakout strategy
    donchian_window: int = 20
    ema_fast: int = 50
    ema_slow: int = 200
    atr_len: int = 14
    stop_atr_mult_trend: float = 2.8
    tp_r_trend: float = 2.2
    trail_atr_mult_trend: float = 2.2
    trail_update_min_gap: float = 0.001  # 0.1%

    # Mean reversion strategy
    rsi_len: int = 14
    rsi_low: float = 30.0
    rsi_high: float = 70.0
    bb_len: int = 20
    bb_std: float = 2.0
    stop_atr_mult_mr: float = 2.2
    tp_r_mr: float = 1.4


@dataclass
class BacktestConfig:
    timeframe: str = "5m"
    in_sample_days: int = 360
    walk_forward_days: int = 180
    wf_train_days: int = 90
    wf_test_days: int = 30
    ohlcv_limit: int = 1500
    cache_dir: str = "data_cache"


@dataclass
class LiveConfig:
    poll_interval_sec: int = 20
    working_type: str = "MARK_PRICE"  # triggers for stop/tp
    margin_mode: str = "isolated"  # isolated / cross
    cancel_orphan_orders: bool = True
    heartbeat_sec: int = 3600


@dataclass
class BotConfig:
    exchange: ExchangeConfig = field(default_factory=ExchangeConfig)
    discord: DiscordConfig = field(default_factory=DiscordConfig)
    cost: CostConfig = field(default_factory=CostConfig)
    risk: RiskConfig = field(default_factory=RiskConfig)
    selector: SelectorConfig = field(default_factory=SelectorConfig)
    strategy: StrategyConfig = field(default_factory=StrategyConfig)
    backtest: BacktestConfig = field(default_factory=BacktestConfig)
    live: LiveConfig = field(default_factory=LiveConfig)

    quote_asset: str = "USDT"
    only_perpetual: bool = True


def load_config(path: str = "config.yaml") -> BotConfig:
    """
    YAML 로드 후 env 시크릿 오버레이:
      BINANCE_API_KEY, BINANCE_API_SECRET, DISCORD_WEBHOOK_URL
    """
    with open(path, "r", encoding="utf-8") as f:
        raw = yaml.safe_load(f) or {}

    cfg = BotConfig()

    def _update(dc, d: Dict[str, Any]):
        for k, v in d.items():
            if not hasattr(dc, k):
                continue
            current = getattr(dc, k)
            if _is_dataclass_instance(current) and isinstance(v, dict):
                _update(current, v)
            else:
                setattr(dc, k, v)

    _update(cfg, raw)

    # secrets from env
    cfg.exchange.api_key = _env("BINANCE_API_KEY", cfg.exchange.api_key) or ""
    cfg.exchange.api_secret = _env("BINANCE_API_SECRET", cfg.exchange.api_secret) or ""

    cfg.discord.webhook_url = _env("DISCORD_WEBHOOK_URL", cfg.discord.webhook_url) or ""
    if cfg.discord.webhook_url:
        cfg.discord.enabled = True

    # basic sanity
    cfg.risk.leverage = int(cfg.risk.leverage)
    if cfg.risk.leverage < 1:
        cfg.risk.leverage = 1
    if not (0.001 <= cfg.risk.per_trade_risk <= 0.05):
        cfg.risk.per_trade_risk = 0.0125
    if cfg.risk.max_total_margin_fraction <= 0 or cfg.risk.max_total_margin_fraction > 1.5:
        cfg.risk.max_total_margin_fraction = 0.85
    if cfg.risk.max_total_margin_fraction < cfg.risk.max_margin_fraction:
        cfg.risk.max_total_margin_fraction = cfg.risk.max_margin_fraction
    if cfg.risk.daily_loss_limit <= 0 or cfg.risk.daily_loss_limit > 0.2:
        cfg.risk.daily_loss_limit = 0.04
    if cfg.selector.top_n < 1:
        cfg.selector.top_n = 5
    if cfg.selector.candidates < cfg.selector.top_n:
        cfg.selector.candidates = cfg.selector.top_n

    return cfg


def to_dict(cfg: BotConfig) -> Dict[str, Any]:
    return asdict(cfg)
