"""Create only missing ancillary streams; refuse incompatible retained work."""
import asyncio
import os

import nats
from nats.js.api import DiscardPolicy, RetentionPolicy, StorageType, StreamConfig


async def main() -> None:
    connection = await nats.connect(os.environ["NATS_URL"], user=os.environ["NATS_USER"],
                                    password=os.environ["NATS_PASSWORD"])
    try:
        js = connection.jetstream()
        for name, subjects in [("SWIFTAID_JOBS", ["swiftaid.jobs.>"]),
                               ("SWIFTAID_EVENTS", ["swiftaid.events.case.>"])]:
            desired = StreamConfig(name=name, subjects=subjects, storage=StorageType.FILE,
                                   retention=RetentionPolicy.LIMITS, discard=DiscardPolicy.NEW,
                                   max_bytes=1073741824, max_msgs=1000000, max_msg_size=65536,
                                   num_replicas=1, duplicate_window=120)
            try:
                actual = (await js.stream_info(name)).config
            except nats.js.errors.NotFoundError:
                actual = (await js.add_stream(desired)).config
            for field in ("subjects", "storage", "retention", "discard", "max_bytes", "max_msgs",
                          "max_msg_size", "num_replicas", "duplicate_window"):
                if getattr(actual, field) != getattr(desired, field):
                    raise RuntimeError(f"{name} differs; explicit operator migration required")
            print(f"Verified {name}", flush=True)
    finally:
        await connection.close()


if __name__ == "__main__":
    asyncio.run(main())
