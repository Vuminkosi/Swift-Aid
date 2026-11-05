"""Aggregate readiness, not merely a running Gateway process."""
import json
from pathlib import Path
import time


def healthy(path: Path, now: float | None = None) -> bool:
    try:
        state = json.loads(path.read_text())
        age = (time.time() if now is None else now) - state["checkedAt"]
        services = state["services"]
        return (0 <= age < 60 and state["ready"] is True and isinstance(services, dict)
                and bool(services) and all(value is True for value in services.values()))
    except (OSError, ValueError, KeyError, TypeError):
        return False


if __name__ == "__main__":
    raise SystemExit(0 if healthy(Path("/run/swiftaid/health.json")) else 1)
