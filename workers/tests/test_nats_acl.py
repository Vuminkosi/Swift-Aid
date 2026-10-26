"""Optional live broker checks for the local gateway/worker ACL boundary."""

import asyncio
import os

import nats
import pytest

BROKER_URL = os.getenv("NATS_ACL_TEST_URL")
GATEWAY_USER = os.getenv("NATS_GATEWAY_USER")
GATEWAY_PASSWORD = os.getenv("NATS_GATEWAY_PASSWORD")
WORKER_USER = os.getenv("NATS_WORKER_USER")
WORKER_PASSWORD = os.getenv("NATS_WORKER_PASSWORD")
BROKER_CONFIGURED = all(
    (BROKER_URL, GATEWAY_USER, GATEWAY_PASSWORD, WORKER_USER, WORKER_PASSWORD)
)


@pytest.mark.skipif(not BROKER_CONFIGURED, reason="Live NATS ACL credentials are not configured")
@pytest.mark.asyncio
async def test_gateway_publishes_jobs_and_worker_cannot_publish_jobs() -> None:
    gateway = await nats.connect(
        servers=[BROKER_URL],
        user=GATEWAY_USER,
        password=GATEWAY_PASSWORD,
        connect_timeout=2,
    )
    worker_errors: asyncio.Queue[Exception] = asyncio.Queue()

    async def on_worker_error(error: Exception) -> None:
        await worker_errors.put(error)

    worker = await nats.connect(
        servers=[BROKER_URL],
        user=WORKER_USER,
        password=WORKER_PASSWORD,
        connect_timeout=2,
        error_cb=on_worker_error,
    )
    received = asyncio.Event()

    async def on_message(_message: nats.aio.msg.Msg) -> None:
        received.set()

    subscription = await worker.subscribe(
        "swiftaid.jobs.report",
        cb=on_message,
    )

    try:
        await worker.flush(timeout=2)
        await gateway.publish("swiftaid.jobs.report", b"opaque-test-job")
        await asyncio.wait_for(received.wait(), timeout=2)

        await worker.publish("swiftaid.jobs.report", b"forbidden-worker-publish")
        await worker.flush(timeout=2)
        error = await asyncio.wait_for(worker_errors.get(), timeout=2)
        assert "permissions violation" in str(error).lower()
    finally:
        await subscription.unsubscribe()
        await gateway.close()
        await worker.close()


@pytest.mark.skipif(not BROKER_CONFIGURED, reason="Live NATS ACL credentials are not configured")
@pytest.mark.asyncio
async def test_gateway_cannot_subscribe_to_private_job_subjects() -> None:
    gateway_errors: asyncio.Queue[Exception] = asyncio.Queue()

    async def on_gateway_error(error: Exception) -> None:
        await gateway_errors.put(error)

    gateway = await nats.connect(
        servers=[BROKER_URL],
        user=GATEWAY_USER,
        password=GATEWAY_PASSWORD,
        connect_timeout=2,
        error_cb=on_gateway_error,
    )

    try:
        await gateway.subscribe("swiftaid.jobs.report")
        await gateway.flush(timeout=2)
        error = await asyncio.wait_for(gateway_errors.get(), timeout=2)
        assert "permissions violation" in str(error).lower()
    finally:
        await gateway.close()
