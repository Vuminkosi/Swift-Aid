from unittest.mock import AsyncMock

import pytest

from swiftaid_workers.classification.backfill import process_next
from swiftaid_workers.classification.consumer import execute_claim


@pytest.mark.asyncio
@pytest.mark.parametrize("state", ["Paused", "Completed", "ConfigurationChanged"])
async def test_stopped_backfill_does_not_claim_or_infer(state):
    gateway = AsyncMock()
    gateway.post.return_value = {"status": {"state": state}, "notice": None}
    assert (await process_next("job", gateway, None))["state"] == state
    gateway.post.assert_awaited_once_with("backfills/job/next", {})


@pytest.mark.asyncio
async def test_lost_completion_does_not_claim_backfill_progress():
    gateway = AsyncMock()
    gateway.post.side_effect = [
        {"status": {"state": "Running", "pending": 1}, "notice": {"messageId": "event"}},
        {"state": "Leased", "lease": {"resultId": "result", "leaseId": "lease"}},
        ConnectionError("synthetic lost completion response")]
    classifier = AsyncMock()
    classifier.classify.return_value = {"resultId": "result"}
    with pytest.raises(ConnectionError):
        await process_next("job", gateway, classifier)
    assert [c.args[0] for c in gateway.post.await_args_list] == ["backfills/job/next", "claim", "complete"]


@pytest.mark.asyncio
async def test_backfill_model_failure_uses_existing_durable_failure_contract():
    gateway = AsyncMock()
    gateway.post.return_value = {"state": "Failed"}
    classifier = AsyncMock()
    classifier.classify.side_effect = ValueError("synthetic incompatible model")
    assert await execute_claim({"state": "Leased", "lease": {"resultId": "r", "leaseId": "l"}}, gateway, classifier) == "Failed"
    gateway.post.assert_awaited_once_with("fail", {"resultId": "r", "leaseId": "l", "code": "model-configuration"})
