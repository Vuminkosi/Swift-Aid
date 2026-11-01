import pytest

from swiftaid_workers.classification.consumer import process_message


class Message:
    data = b'{"schemaVersion":1,"messageId":"00000000-0000-0000-0000-000000000001"}'

    def __init__(self):
        self.acked = False

    async def ack_sync(self):
        self.acked = True

    async def nak(self, delay=0):
        pass


class Gateway:
    def __init__(self, fail=False, state="Completed"):
        self.fail = fail
        self.state = state

    async def claim(self, data):
        if self.fail:
            raise ConnectionError("fixture control-plane unavailable")
        return {"state": self.state}


@pytest.mark.asyncio
async def test_no_ack_when_durable_outcome_is_unavailable():
    message = Message()
    await process_message(message, Gateway(fail=True), None)
    assert not message.acked


@pytest.mark.asyncio
async def test_completed_redelivery_acks_without_inference():
    message = Message()
    await process_message(message, Gateway(), None)
    assert message.acked


@pytest.mark.asyncio
async def test_an_active_lease_does_not_authorize_ack():
    message = Message()
    await process_message(message, Gateway(state="Busy"), None)
    assert not message.acked
