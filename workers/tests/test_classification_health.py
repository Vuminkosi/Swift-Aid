from pathlib import Path

import httpx
import pytest

from swiftaid_workers.classification.health import healthy, refresh


class Connection:
    is_connected = True

    async def flush(self, timeout):
        pass


@pytest.mark.asyncio
async def test_readiness_requires_authenticated_gateway_and_broker(tmp_path):
    path = tmp_path / "ready"
    assert not healthy(path)
    async with httpx.AsyncClient(base_url="http://localhost/", transport=httpx.MockTransport(
        lambda request: httpx.Response(401))) as client:
        with pytest.raises(httpx.HTTPStatusError):
            await refresh(Connection(), client, path)
        assert not path.exists()
    async with httpx.AsyncClient(base_url="http://localhost/", transport=httpx.MockTransport(
        lambda request: httpx.Response(200, json={"status": "ready"}))) as client:
        await refresh(Connection(), client, path)
        assert healthy(path)
        connection = Connection()
        connection.is_connected = False
        with pytest.raises(ConnectionError):
            await refresh(connection, client, path)


def test_stale_or_invalid_health_lease_is_not_healthy(tmp_path: Path):
    path = tmp_path / "ready"
    path.write_text("100")
    assert healthy(path, now=159)
    assert not healthy(path, now=160)
    assert not healthy(path, now=99)
    path.write_text("invalid")
    assert not healthy(path)
