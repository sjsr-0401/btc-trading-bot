from __future__ import annotations

import logging
import time
from typing import Optional

import requests

from .config import DiscordConfig

log = logging.getLogger("discord")


class DiscordNotifier:
    """
    Discord Webhook 기반 알림.
    - 서버/채널에서 Webhook URL 생성 → DISCORD_WEBHOOK_URL 로 설정
    """
    def __init__(self, cfg: DiscordConfig):
        self.cfg = cfg

    def send(self, text: str, *, mention: Optional[str] = None) -> None:
        if not self.cfg.enabled or not self.cfg.webhook_url:
            return

        prefix = (mention or self.cfg.mention or "").strip()
        content = f"{prefix} {text}".strip() if prefix else text

        payload = {
            "content": content,
            "username": self.cfg.username or "USDM-Bot",
            # allowed_mentions: 멘션 스팸 방지
            "allowed_mentions": {"parse": ["users", "roles"]},
        }
        try:
            resp = requests.post(self.cfg.webhook_url, json=payload, timeout=10)
            if resp.status_code not in (200, 204):
                log.warning("Discord send failed: %s %s", resp.status_code, resp.text[:300])
        except Exception as e:
            log.warning("Discord exception: %s", e)

    def heartbeat(self, name: str = "bot", every_sec: int = 3600) -> None:
        now = time.time()
        if int(now) % every_sec < 3:
            self.send(f"✅ {name} heartbeat: alive @ {time.strftime('%Y-%m-%d %H:%M:%S')}")
