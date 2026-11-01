"""Bounded pull consumption; only a durable Gateway outcome authorizes ACK."""
from __future__ import annotations

import asyncio
import hashlib
import json
import logging
from contextlib import suppress
from typing import Any

import httpx
import nats

logger = logging.getLogger(__name__)
TERMINAL = {"Completed", "Failed", "Superseded", "Quarantined"}


class GatewayClient:
    def __init__(self, client: httpx.AsyncClient) -> None:
        self.client = client

    async def post(self, route: str, body: Any) -> dict[str, Any]:
        response = await self.client.post(route, json=body)
        response.raise_for_status()
        result: dict[str, Any] = response.json()
        return result

    async def claim(self, data: bytes) -> dict[str, Any]:
        if len(data) > 4096:
            raise ValueError("oversized-notice")
        return await self.post("claim", {"notice": json.loads(data)})


async def process_message(message: Any, gateway: Any, classifier: Any) -> None:
    """A lost completion reply is retried through claim, never inferred to be success."""
    try:
        try:
            claim = await gateway.claim(message.data)
        except (ValueError, httpx.HTTPStatusError) as error:
            if isinstance(error, httpx.HTTPStatusError) and error.response.status_code != 400:
                raise
            quarantine = await gateway.post("quarantine", {
                "streamSequence": message.metadata.sequence.stream,
                "payloadSha256": hashlib.sha256(message.data).hexdigest(), "reason": "invalid-notice"})
            if quarantine["state"] == "Quarantined":
                await message.ack_sync()
            return
        if claim["state"] in TERMINAL:
            await message.ack_sync()
            return
        if claim["state"] != "Leased":
            await message.nak(delay=5)
            return
        state = await execute_claim(claim, gateway, classifier, message)
        if state in TERMINAL:
            await message.ack_sync()
        else:
            await message.nak(delay=5)
    except asyncio.CancelledError:
        raise
    except (httpx.HTTPError, ConnectionError, ValueError, KeyError, OSError, nats.errors.Error) as error:
        logger.warning("Classification delivery deferred: %s", type(error).__name__)
        # No ACK if the control plane or durable commit is unavailable.
        await message.nak(delay=10)


async def execute_claim(claim: dict[str, Any], gateway: Any, classifier: Any, message: Any = None) -> str:
    """Shared model lease execution for live delivery and historical backfill."""
    if claim["state"] != "Leased":
        return str(claim["state"])
    lease = claim["lease"]

    async def heartbeat() -> None:
        while True:
            await asyncio.sleep(20)
            response = await gateway.post("renew", {"resultId": lease["resultId"], "leaseId": lease["leaseId"]})
            if response["state"] != "Leased":
                return
            if message is not None:
                await message.in_progress()

    renewal = asyncio.create_task(heartbeat())
    try:
        try:
            result = await classifier.classify(lease)
            outcome = await gateway.post("complete", result)
        except (httpx.HTTPError, ConnectionError, asyncio.CancelledError):
            raise
        except (ValueError, RuntimeError, OSError) as error:
            # Exceptions may include protected input; log only bounded failure codes.
            code = "model-configuration" if isinstance(error, ValueError) else "model-unavailable"
            outcome = await gateway.post("fail", {"resultId": lease["resultId"], "leaseId": lease["leaseId"], "code": code})
        return str(outcome["state"])
    finally:
        renewal.cancel()
        with suppress(asyncio.CancelledError, httpx.HTTPError):
            await renewal


async def run_consumer(settings: Any, classifier: Any) -> None:
    connection = await nats.connect(settings.nats_url, user=settings.nats_user, password=settings.nats_password,
                                    max_reconnect_attempts=-1, reconnect_time_wait=2)
    js = connection.jetstream()
    # One durable shared by replicas. Database leases and fingerprints fence duplicate delivery.
    info = await js.consumer_info("SWIFTAID_INGESTION", settings.durable)
    if (info.config.filter_subject != "swiftaid.ingestion.v1.case" or
            info.config.ack_policy != "explicit" or info.config.max_ack_pending != 32 or info.config.max_deliver != -1):
        raise ValueError("classification-consumer-configuration-mismatch")
    subscription = await js.pull_subscribe_bind(durable=settings.durable, stream="SWIFTAID_INGESTION",
                                               pending_msgs_limit=32, pending_bytes_limit=131072)
    # Broker redelivery remains unlimited so a Gateway outage cannot strand an unrecorded failure.
    # Actual inference attempts are bounded and terminally recorded by the Gateway.
    async with httpx.AsyncClient(base_url=settings.gateway_url.rstrip("/") + "/api/internal/classification/",
        headers={"Authorization": f"Bearer {settings.worker_token}"}, timeout=30, follow_redirects=False) as client:
        gateway = GatewayClient(client)

        async def lane() -> None:
            while True:
                try:
                    messages = await subscription.fetch(batch=1, timeout=5)
                except nats.errors.TimeoutError:
                    continue
                for message in messages:
                    await process_message(message, gateway, classifier)

        try:
            async with asyncio.TaskGroup() as group:
                for _ in range(settings.concurrency):
                    group.create_task(lane())
        finally:
            await connection.drain()
