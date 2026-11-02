import asyncio
import json

from fastapi.testclient import TestClient

from sample_backend.main import StreamState, create_app, sample_events


def test_production_has_health_but_no_demo_or_docs():
    with TestClient(create_app("Production")) as client:
        assert client.get("/health/live").json()["status"] == "live"
        assert client.get("/health/ready").json() == {
            "status": "ready", "service": "sample", "dependencies": []
        }
        for path in ("/stream", "/docs", "/openapi.json"):
            assert client.get(path).status_code == 404


def test_environment_defaults_to_production(monkeypatch):
    monkeypatch.delenv("ASSEMBLEAI_ENVIRONMENT", raising=False)
    with TestClient(create_app()) as client:
        assert client.get("/stream").status_code == 404


def test_lifespan_readiness_resets_on_shutdown():
    app = create_app("Development")
    assert not app.state.ready
    with TestClient(app) as client:
        assert client.get("/health/ready").status_code == 200
    assert not app.state.ready


def test_sse_events_and_request_identity():
    app = create_app("Development")
    with TestClient(app) as client:
        response = client.get("/stream?count=3&delay_ms=10")
    assert response.headers["content-type"].startswith("text/event-stream")
    assert response.headers["x-accel-buffering"] == "no"
    blocks = response.text.strip().split("\n\n")
    assert [block.splitlines()[0] for block in blocks] == ["event: delta"] * 3 + ["event: complete"]
    payloads = [json.loads(block.splitlines()[1].removeprefix("data: ")) for block in blocks]
    assert [payload["index"] for payload in payloads[:-1]] == [0, 1, 2]
    assert all(payload["requestId"] == response.headers["x-request-id"] for payload in payloads)
    assert app.state.streams.active == 0
    assert app.state.streams.completed == 1


def test_stream_rejects_unbounded_work():
    with TestClient(create_app("Development")) as client:
        for query in ("count=0", "count=21", "delay_ms=0", "delay_ms=1001", "count=text"):
            assert client.get(f"/stream?{query}").status_code == 422


class Connection:
    disconnected = False

    async def is_disconnected(self):
        return self.disconnected


def test_first_event_immediate_and_generator_close_releases_state():
    async def scenario():
        state = StreamState()
        events = sample_events(Connection(), 20, 1000, "cancel-test", state)
        first = await asyncio.wait_for(anext(events), timeout=0.1)
        assert "event: delta" in first
        assert state.active == 1
        await events.aclose()
        assert state.active == 0
        assert state.cancelled == 1

    asyncio.run(scenario())


def test_disconnect_does_not_emit_completion():
    async def scenario():
        state = StreamState()
        connection = Connection()
        events = sample_events(connection, 3, 10, "disconnect-test", state)
        await anext(events)
        connection.disconnected = True
        rest = [item async for item in events]
        assert not rest
        assert state.active == 0
        assert state.cancelled == 1

    asyncio.run(scenario())


def test_task_cancellation_propagates_and_releases_state():
    async def scenario():
        state = StreamState()
        events = sample_events(Connection(), 20, 1000, "task-cancel-test", state)
        await anext(events)
        task = asyncio.create_task(anext(events))
        await asyncio.sleep(0)
        task.cancel()
        try:
            await task
        except asyncio.CancelledError:
            pass
        else:
            raise AssertionError("Stream swallowed cancellation")
        assert state.active == 0
        assert state.cancelled == 1

    asyncio.run(scenario())


def test_stream_errors_have_an_error_event_not_success():
    class BrokenConnection:
        async def is_disconnected(self):
            raise RuntimeError("Internal details must not be returned")

    async def scenario():
        state = StreamState()
        result = "".join([item async for item in sample_events(BrokenConnection(), 1, 10, "error-test", state)])
        assert "event: error" in result
        assert "event: complete" not in result
        assert "Internal details" not in result
        assert state.active == 0 and state.failed == 1

    asyncio.run(scenario())
