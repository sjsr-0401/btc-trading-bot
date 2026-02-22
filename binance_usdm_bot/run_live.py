#!/usr/bin/env python3
from __future__ import annotations

import argparse

from usdm_bot.config import load_config
from usdm_bot.live import LiveBot
from usdm_bot.utils import setup_logging


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--config", default="config.yaml")
    ap.add_argument("--state", default="state/state.json")
    args = ap.parse_args()

    cfg = load_config(args.config)
    setup_logging()

    bot = LiveBot(cfg, state_path=args.state)
    bot.start()


if __name__ == "__main__":
    main()
