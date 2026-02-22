from __future__ import annotations

from dataclasses import dataclass, asdict
from typing import Any, Dict, List

from .risk import RiskState
from .utils import read_json, write_json


@dataclass
class PositionState:
    symbol: str
    side: str  # long/short
    qty: float
    entry_price: float
    stop_price: float
    take_profit: float
    tag: str = ""
    opened_ts: str = ""  # ISO
    # For trailing logic
    highest: float = 0.0
    lowest: float = 0.0

    def to_dict(self) -> Dict[str, Any]:
        return asdict(self)

    @classmethod
    def from_dict(cls, d: Dict[str, Any]) -> "PositionState":
        return cls(**{k: d.get(k) for k in cls.__dataclass_fields__.keys()})


@dataclass
class BotState:
    risk: RiskState = RiskState()
    positions: List[PositionState] = None  # type: ignore
    selected_symbols: List[str] = None  # type: ignore
    last_selector_ts: float = 0.0

    def __post_init__(self):
        if self.positions is None:
            self.positions = []
        if self.selected_symbols is None:
            self.selected_symbols = []

    def to_dict(self) -> Dict[str, Any]:
        return {
            "risk": self.risk.to_dict(),
            "positions": [p.to_dict() for p in self.positions],
            "selected_symbols": list(self.selected_symbols),
            "last_selector_ts": self.last_selector_ts,
        }

    @classmethod
    def from_dict(cls, d: Dict[str, Any]) -> "BotState":
        risk = RiskState.from_dict(d.get("risk", {}) or {})
        pos = [PositionState.from_dict(x) for x in (d.get("positions") or [])]
        ss = d.get("selected_symbols") or []
        last = float(d.get("last_selector_ts") or 0.0)
        s = cls(risk=risk, positions=pos, selected_symbols=ss, last_selector_ts=last)
        return s


def load_state(path: str) -> BotState:
    d = read_json(path, default={})
    return BotState.from_dict(d)


def save_state(path: str, state: BotState) -> None:
    write_json(path, state.to_dict())
