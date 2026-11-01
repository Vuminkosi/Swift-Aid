"""Opt-in subprocess harness for real Gateway/PostgreSQL/JetStream/pinned BART tests."""
import asyncio
import json
import os
import time
from pathlib import Path

import httpx
import nats
from nats.js.api import AckPolicy, ConsumerConfig

from swiftaid_workers.classification.bart import BartClassifier
from swiftaid_workers.classification.consumer import GatewayClient, process_message


async def main():
    notice = json.loads(Path(os.environ["CLASSIFICATION_TEST_NOTICE"]).read_text(encoding="utf-8-sig"))
    connection = await nats.connect("nats://127.0.0.1:44229")
    js = connection.jetstream()
    try:
        await js.stream_info("SWIFTAID_INGESTION")
    except nats.js.errors.NotFoundError:
        await js.add_stream(name="SWIFTAID_INGESTION", subjects=["swiftaid.ingestion.v1.*"],
                            max_msgs=10000, max_bytes=16777216, max_msg_size=4096)
    durable = "fixture_" + notice["messageId"].replace("-", "")
    subscription = await js.pull_subscribe("swiftaid.ingestion.v1.case", durable=durable,
        stream="SWIFTAID_INGESTION", config=ConsumerConfig(ack_policy=AckPolicy.EXPLICIT, deliver_policy="new", ack_wait=2))
    classifier = BartClassifier()
    await asyncio.to_thread(classifier.load)
    async with httpx.AsyncClient(base_url=os.environ["CLASSIFICATION_TEST_GATEWAY"] + "/api/internal/classification/",
        headers={"Authorization": "Bearer " + os.environ["CLASSIFICATION_TEST_TOKEN"]}, timeout=30) as client:
        gateway = GatewayClient(client)
        await js.publish("swiftaid.ingestion.v1.case", json.dumps(notice).encode())
        message = (await subscription.fetch(1, timeout=5))[0]
        started = time.perf_counter()
        claim = await gateway.claim(message.data)
        assert claim["state"] == "Leased", claim["state"]
        prediction = await classifier.classify(claim["lease"])
        assert prediction["predictions"] and prediction["abstentionReason"] is None
        outcome = await gateway.post("complete", prediction)
        assert outcome["state"] == "Completed"
        # Simulate a worker dying after durable commit but before broker acknowledgment.
        await message.nak()
        replay = (await subscription.fetch(1, timeout=5))[0]
        assert replay.metadata.num_delivered >= 2
        await process_message(replay, gateway, None)  # no inference implementation available on replay
        await js.publish("swiftaid.ingestion.v1.case", b"invalid-json-fixture")
        poison = (await subscription.fetch(1, timeout=5))[0]
        await process_message(poison, gateway, None)
        info = await js.consumer_info("SWIFTAID_INGESTION", durable)
        assert info.num_ack_pending == 0, info.num_ack_pending
        print(json.dumps({"resultId": claim["lease"]["resultId"], "labels": len(prediction["predictions"]),
            "inferenceMilliseconds": prediction["inferenceMilliseconds"], "inputTokens": prediction["inputTokens"],
            "elapsedSeconds": time.perf_counter() - started, "redeliveries": replay.metadata.num_delivered - 1,
            "pendingAcknowledgements": info.num_ack_pending}))
    await js.delete_consumer("SWIFTAID_INGESTION", durable)
    await connection.close()


if __name__ == "__main__":
    asyncio.run(main())
