"""Explicit bootstrap command. The worker identity cannot create or alter consumers."""
import asyncio
import os

import nats
from nats.js.api import AckPolicy, ConsumerConfig, DeliverPolicy


async def provision() -> None:
    connection = await nats.connect(os.environ["NATS_URL"], user=os.environ["NATS_USER"], password=os.environ["NATS_PASSWORD"])
    try:
        js = connection.jetstream()
        name = "swiftaid_classification_v1"
        desired = ConsumerConfig(durable_name=name, ack_policy=AckPolicy.EXPLICIT,
            deliver_policy=DeliverPolicy.ALL, filter_subject="swiftaid.ingestion.v1.case",
            ack_wait=210, max_ack_pending=32, max_deliver=-1, max_waiting=32)
        try:
            actual = (await js.consumer_info("SWIFTAID_INGESTION", name)).config
        except nats.js.errors.NotFoundError:
            actual = (await js.add_consumer("SWIFTAID_INGESTION", config=desired)).config
        for field in ("ack_policy", "deliver_policy", "filter_subject", "ack_wait", "max_ack_pending", "max_deliver", "max_waiting"):
            if getattr(actual, field) != getattr(desired, field):
                raise ValueError(f"Consumer differs: {field}; explicit operator migration required")
        print("Classification durable verified")
    finally:
        await connection.close()


if __name__ == "__main__":
    asyncio.run(provision())
