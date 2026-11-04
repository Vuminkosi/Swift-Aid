"""Real repository ACL: bootstrap provisions, Gateway publishes, worker only binds/acks."""
import asyncio
import os

import nats
import pytest
from nats.js.api import AckPolicy, ConsumerConfig


@pytest.mark.skipif(os.getenv("CLASSIFICATION_ACL_TEST") != "1", reason="Requires disposable classification ACL broker")
@pytest.mark.asyncio
async def test_classification_consumer_uses_least_privilege_broker_identity():
    url = os.environ["NATS_ACL_TEST_URL"]
    bootstrap = await nats.connect(url, user=os.environ["NATS_BOOTSTRAP_USER"], password=os.environ["NATS_BOOTSTRAP_PASSWORD"])
    gateway = await nats.connect(url, user=os.environ["NATS_GATEWAY_USER"], password=os.environ["NATS_GATEWAY_PASSWORD"])
    errors = asyncio.Queue()

    async def on_error(error):
        await errors.put(error)

    worker = await nats.connect(url, user=os.environ["NATS_WORKER_USER"], password=os.environ["NATS_WORKER_PASSWORD"], error_cb=on_error)
    try:
        js = bootstrap.jetstream()
        await js.add_stream(name="SWIFTAID_INGESTION", subjects=["swiftaid.ingestion.v1.case", "swiftaid.ingestion.v1.intake", "swiftaid.ingestion.v1.classification"],
                            max_msgs=10000, max_bytes=16777216, max_msg_size=4096)
        durable = "swiftaid_classification_v1"
        await js.add_consumer("SWIFTAID_INGESTION", config=ConsumerConfig(durable_name=durable,
            filter_subject="swiftaid.ingestion.v1.case", ack_policy=AckPolicy.EXPLICIT, ack_wait=210, max_ack_pending=32, max_deliver=-1))
        worker_js = worker.jetstream()
        assert (await worker_js.consumer_info("SWIFTAID_INGESTION", durable)).config.ack_policy == "explicit"
        subscription = await worker_js.pull_subscribe_bind(durable=durable, stream="SWIFTAID_INGESTION")
        await gateway.jetstream().publish("swiftaid.ingestion.v1.case", b"opaque-fixture")
        message = (await subscription.fetch(1, timeout=3))[0]
        await message.ack_sync()
        await gateway.jetstream().publish("swiftaid.ingestion.v1.classification", b"opaque-completion")
        await worker.publish("swiftaid.ingestion.v1.case", b"forbidden")
        await worker.flush()
        assert "permissions violation" in str(await asyncio.wait_for(errors.get(), 3)).lower()
    finally:
        await worker.close()
        await gateway.close()
        await bootstrap.close()
