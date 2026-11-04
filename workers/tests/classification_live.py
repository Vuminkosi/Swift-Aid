"""Opt-in subprocess harness for real Gateway/PostgreSQL/JetStream/pinned BART tests."""
import asyncio
import json
import os
import time
from pathlib import Path
from uuid import uuid4

import httpx
import nats
from nats.js.api import AckPolicy, ConsumerConfig

from swiftaid_workers.classification.backfill import process_next
from swiftaid_workers.classification.bart import BartClassifier
from swiftaid_workers.classification.consumer import GatewayClient, process_message


async def main():
    notice_json = os.environ.get("CLASSIFICATION_TEST_NOTICE_JSON")
    notice = json.loads(notice_json if notice_json is not None else
                        Path(os.environ["CLASSIFICATION_TEST_NOTICE"]).read_text(encoding="utf-8-sig"))
    connection = await nats.connect(os.environ.get("CLASSIFICATION_TEST_NATS", "nats://127.0.0.1:44229"))
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
    headers = {"Authorization": "Bearer " + os.environ["CLASSIFICATION_TEST_TOKEN"]}
    if host := os.environ.get("CLASSIFICATION_TEST_HOST_HEADER"):
        headers["Host"] = host
    async with httpx.AsyncClient(base_url=os.environ["CLASSIFICATION_TEST_GATEWAY"] + "/api/internal/classification/",
        headers=headers, timeout=30) as client:
        gateway = GatewayClient(client)
        await js.publish("swiftaid.ingestion.v1.case", json.dumps(notice).encode())
        message = (await subscription.fetch(1, timeout=5))[0]
        started = time.perf_counter()
        try:
            claim = await gateway.claim(message.data)
        except httpx.HTTPStatusError as error:
            raise RuntimeError(f"fixture-claim-http-{error.response.status_code}: {error.response.text[:256]}") from error
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
        job_id = str(uuid4())
        started = await gateway.post("backfills", {"jobId": job_id,
            "workspaceId": os.environ["CLASSIFICATION_TEST_WORKSPACE"], "organizationId": "org-zw-1"})
        assert started["eligible"] == 4, started
        await gateway.post(f"backfills/{job_id}/control", {"paused": True})
        assert (await process_next(job_id, gateway, None))["state"] == "Paused"
        await gateway.post(f"backfills/{job_id}/control", {"paused": False})
        for _ in range(10):
            status = await process_next(job_id, gateway, classifier)
            if status["state"] == "Completed":
                break
        assert status["state"] == "Completed", status
        assert (status["pending"], status["newlyClassified"], status["reused"], status["abstained"], status["failed"]) == (0, 1, 2, 1, 0), status
        # A new HTTP client can reconcile the same job with no model loaded.
        status = await process_next(job_id, GatewayClient(client), None)
        assert status["state"] == "Completed"
        print(json.dumps({"backfill": status}))
    await js.delete_consumer("SWIFTAID_INGESTION", durable)
    await connection.close()


if __name__ == "__main__":
    asyncio.run(main())
