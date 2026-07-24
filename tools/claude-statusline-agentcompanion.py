#!/usr/bin/env python3
import json
import sys
from datetime import datetime, timezone
from pathlib import Path

if sys.platform == "win32":
    sys.stdout.reconfigure(encoding="utf-8")

GREEN = "\033[32m"
YELLOW = "\033[33m"
RED = "\033[31m"
RESET = "\033[0m"


def color(pct):
    if pct is None:
        return ""
    if pct >= 80:
        return RED
    if pct >= 60:
        return YELLOW
    return GREEN


def fmt(label, info):
    if not info:
        return None
    pct = info.get("used_percentage")
    if pct is None:
        return None
    return f"{color(pct)}●{RESET} {label} {pct:.0f}%"


def write_agentcompanion_cache(rate_limits):
    if not rate_limits:
        return

    wanted = {}
    for key in ("five_hour", "seven_day"):
        info = rate_limits.get(key)
        if isinstance(info, dict) and info.get("used_percentage") is not None:
            wanted[key] = {
                "used_percentage": info.get("used_percentage"),
                "resets_at": info.get("resets_at"),
            }

    if not wanted:
        return

    try:
        cache_path = Path.home() / ".claude" / "agentcompanion-rate-limits.json"
        cache_path.write_text(
            json.dumps(
                {
                    "timestamp": datetime.now(timezone.utc).isoformat(),
                    "rate_limits": wanted,
                },
                ensure_ascii=False,
            ),
            encoding="utf-8",
        )
    except Exception:
        pass


def main():
    raw = sys.stdin.read().strip()
    if not raw:
        return

    try:
        data = json.loads(raw)
    except json.JSONDecodeError:
        return

    ctx_window = data.get("context_window", {}) or {}
    ctx_info = None
    used_pct = ctx_window.get("used_percentage")
    if used_pct is not None:
        ctx_info = {"used_percentage": used_pct}

    rate_limits = data.get("rate_limits", {}) or {}
    five_hour_raw = rate_limits.get("five_hour")
    seven_day_raw = rate_limits.get("seven_day")
    write_agentcompanion_cache(rate_limits)

    parts = [
        fmt("ctx", ctx_info),
        fmt("5h", five_hour_raw),
        fmt("7d", seven_day_raw),
    ]
    parts = [part for part in parts if part]
    if parts:
        print("  ".join(parts))


if __name__ == "__main__":
    main()