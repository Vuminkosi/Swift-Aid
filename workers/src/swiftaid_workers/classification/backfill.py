"""Low-priority backfill of an existing Gateway-owned snapshot; never reimports cases."""
from __future__ import annotations

import asyncio
import logging
from typing import Any

import httpx

from swiftaid_workers.classification.consumer import TERMINAL, GatewayClient, execute_claim

logger = logging.getLogger(__name__)


async def process_next(job_id: str, gateway: Any, classifier: Any) -> dict[str, Any]:
    work = await gateway.post(f"backfills/{job_id}/next", {})
    status: dict[str, Any] = work["status"]
    if status["state"] != "Running" or work["notice"] is None:
        return status
    claim = await gateway.post("claim", {"notice": work["notice"], "backfillJobId": job_id})
    outcome = await execute_claim(claim, gateway, classifier)
    if outcome not in TERMINAL:
        await asyncio.sleep(5)
    # Only the next durable reconciliation advances counts, even if the completion reply was lost.
    return status


async def run_discovered_backfills(settings: Any, gateway: Any, classifier: Any) -> None:
    """Run one bounded background lane for jobs admitted by the Gateway."""
    while True:
        try:
            response = await gateway.client.get("backfills/runnable")
            response.raise_for_status()
            job_id = response.json().get("jobId")
            if job_id:
                await process_next(str(job_id), gateway, classifier)
        except asyncio.CancelledError:
            raise
        except (httpx.HTTPError, ConnectionError, OSError, ValueError, KeyError) as error:
            logger.warning("Backfill discovery deferred: %s", type(error).__name__)
            await asyncio.sleep(10)
        await asyncio.sleep(settings.backfill_interval_seconds)


async def run_backfill(settings: Any, classifier: Any, job_id: str) -> None:
    async with httpx.AsyncClient(base_url=settings.gateway_url.rstrip("/") + "/api/internal/classification/",
        headers={"Authorization": f"Bearer {settings.worker_token}"}, timeout=30, follow_redirects=False) as client:
        gateway = GatewayClient(client)
        while True:
            try:
                status = await process_next(job_id, gateway, classifier)
                if status["state"] in {"Completed", "Paused", "ConfigurationChanged"}:
                    logger.info("Backfill stopped: job=%s state=%s pending=%s", job_id, status["state"], status["pending"])
                    return
            except httpx.HTTPStatusError as error:
                if error.response.status_code in {400, 401, 403, 404}:
                    raise RuntimeError("Backfill scope or configuration requires operator review") from None
                logger.warning("Backfill control plane unavailable: %s", type(error).__name__)
                await asyncio.sleep(10)
            except (httpx.RequestError, ConnectionError, OSError) as error:
                logger.warning("Backfill request deferred: %s", type(error).__name__)
                await asyncio.sleep(10)
            # One historical lane, deliberately paced so live work retains capacity.
            await asyncio.sleep(settings.backfill_interval_seconds)
