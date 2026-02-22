from __future__ import annotations

import json
import logging
import os
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, Optional


def utc_now() -> datetime:
    return datetime.now(timezone.utc)


def utc_ms() -> int:
    return int(time.time() * 1000)


def ensure_dir(path: str | Path) -> Path:
    p = Path(path)
    p.mkdir(parents=True, exist_ok=True)
    return p


def setup_logging(log_dir: str = "logs", level: int = logging.INFO) -> None:
    ensure_dir(log_dir)
    log_path = Path(log_dir) / "bot.log"

    root = logging.getLogger()
    root.setLevel(level)

    # Avoid duplicate handlers in notebooks/REPL
    if root.handlers:
        return

    fmt = logging.Formatter("%(asctime)s | %(levelname)s | %(name)s | %(message)s")

    sh = logging.StreamHandler()
    sh.setFormatter(fmt)
    root.addHandler(sh)

    fh = logging.FileHandler(log_path, encoding="utf-8")
    fh.setFormatter(fmt)
    root.addHandler(fh)


def read_json(path: str | Path, default: Optional[Dict[str, Any]] = None) -> Dict[str, Any]:
    p = Path(path)
    if not p.exists():
        return dict(default or {})
    with open(p, "r", encoding="utf-8") as f:
        return json.load(f)


def write_json(path: str | Path, obj: Dict[str, Any]) -> None:
    p = Path(path)
    p.parent.mkdir(parents=True, exist_ok=True)
    tmp = p.with_suffix(p.suffix + ".tmp")
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(obj, f, ensure_ascii=False, indent=2)
    os.replace(tmp, p)
