"""Local readiness lease, refreshed only by a loaded, connected consumer."""
from __future__ import annotations

import asyncio
import logging
import os
import time
from pathlib import Path
from typing import Any

import httpx
import nats.errors

logger = logging.getLogger(__name__)


async def refresh(connection: Any, client: httpx.AsyncClient, path: Path) -> None:
    if not connection.is_connected:
        raise ConnectionError("broker-disconnected")
    await connection.flush(timeout=5)
    response = await client.get("ready")
    response.raise_for_status()
    if response.json().get("status") != "ready":
        raise ValueError("gateway-not-ready")
    path.write_text(str(time.time()), encoding="ascii")


async def monitor(connection: Any, client: httpx.AsyncClient, path: Path) -> None:
    while True:
        try:
            await refresh(connection, client, path)
        except (httpx.HTTPError, nats.errors.Error, ConnectionError, OSError, ValueError) as error:
            # Do not log credentials, provider responses, or protected source data.
            path.unlink(missing_ok=True)
            logger.warning("Classification dependency unavailable: %s", type(error).__name__)
        await asyncio.sleep(15)


def healthy(path: Path, now: float | None = None) -> bool:
    try:
        age = (time.time() if now is None else now) - float(path.read_text(encoding="ascii"))
        return 0 <= age < 60
    except (OSError, ValueError):
        return False


if __name__ == "__main__":
    raise SystemExit(0 if healthy(Path(os.environ["CLASSIFICATION_HEALTH_FILE"])) else 1)
