"""Streaming lifetime, final guard, bounded protocol and real socket acceptance."""

import asyncio
from contextlib import asynccontextmanager, contextmanager
import json
import socket
import threading
import time
from types import SimpleNamespace

import httpx
import pytest
import uvicorn

from chatbi_backend.authentication import RemoteValidator
from chatbi_backend.connectors import ConnectorClient, validate_connector_origin
from chatbi_backend.main import StreamState, chatbi_events, create_app
from chatbi_backend.models import ModelClient, validate_model_configuration
from test_chatbi import DATASET, DATA_CAP, HEADERS, MODEL_CAP, QUERY_REQUEST, REQUEST, Platform, ask, events, running


def test_final_guard_failure_discards_provisional_rows(monkeypatch):
    class Changed(Platform):
        async def handler(self, request):
            if request.url.path.endswith("/check") and any(call.url.path.endswith("/check") for call in self.calls):
                self.check_status = 403
            return await super().handler(request)

    with running(monkeypatch, Changed()) as (browser, _, platform):
        response = ask(browser)
    sequence = events(response)
    assert any(value.get("kind") == "rows" for _, value in sequence)
    assert sequence[-1] == ("error", {"code": "authorization_revoked", "requestId": REQUEST})
    assert not any(kind == "complete" for kind, _ in sequence)
    assert len([call for call in platform.calls if call.url.path.endswith("/check")]) == 2


def test_guard_rechecks_before_delivery_resumes_after_rows():
    async def scenario():
        platform = Platform()
        async with httpx.AsyncClient(transport=httpx.MockTransport(platform.handler)) as client:
            app = SimpleNamespace(state=SimpleNamespace(streams=StreamState(),
                models=ModelClient(client, validate_model_configuration("https://platform.test", "assistant", "2048", False)),
                connectors=ConnectorClient(client, "https://platform.test")))
            stream = chatbi_events(app, {"datasetId": "production", "question": "x"}, MODEL_CAP, DATA_CAP, REQUEST, 10)
            async with _closing(stream):
                while '"kind":"rows"' not in await anext(stream):
                    pass
                platform.check_status = 403
                await asyncio.sleep(1)
                last = await anext(stream)
                assert "event: error" in last and "authorization_revoked" in last
                assert len([call for call in platform.calls if call.url.path.endswith("/check")]) >= 2
            assert app.state.streams.failed == 1 and app.state.streams.active == 0
    asyncio.run(scenario())


@asynccontextmanager
async def _closing(stream):
    try:
        yield stream
    finally:
        await stream.aclose()


def test_rows_are_utf8_bounded_and_never_truncated(monkeypatch):
    platform = Platform()
    platform.plan = '{"columns":["line"],"filters":[],"metrics":[],"limit":100}'
    platform.result = json.dumps({"requestId": QUERY_REQUEST, "columns": ["line"], "rows": [["厂" * 1365]] * 100,
                                 "authorizationVersion": 7, "configurationRevision": 2}, ensure_ascii=False)
    with running(monkeypatch, platform) as (browser, _, _):
        response = ask(browser)
    sequence = events(response)
    assert sequence[-1][0] == "complete" and sequence[-1][1]["rowCount"] == 100
    chunks = [value["rows"] for _, value in sequence if value.get("kind") == "rows"]
    assert len(chunks) > 1 and sum(map(len, chunks)) == 100
    assert all(cell == "厂" * 1365 for chunk in chunks for row in chunk for cell in row)


def test_single_row_larger_than_frame_is_explicit_error(monkeypatch):
    platform = Platform()
    columns = ["text" + str(index) for index in range(8)]
    platform.metadata = {"items": [{**DATASET, "columns": [{"name": name, "type": "text"} for name in columns]}]}
    platform.plan = json.dumps({"columns": columns, "filters": [], "metrics": [], "limit": 1})
    platform.result = json.dumps({"requestId": QUERY_REQUEST, "columns": columns, "rows": [["x" * 4096] * 8],
                                 "authorizationVersion": 7, "configurationRevision": 2})
    with running(monkeypatch, platform) as (browser, _, _):
        response = ask(browser)
    sequence = events(response)
    assert sequence[-1] == ("error", {"code": "result_too_large", "requestId": REQUEST})
    assert not any(kind == "complete" for kind, _ in sequence)


@pytest.mark.parametrize("change", [
    {"rows": [[True]]}, {"rows": [["secret-source-value"]]}, {"rows": [[{}]]}, {"rows": [[]]},
    {"columns": ["other"]}, {"authorizationVersion": True}, {"configurationRevision": 0},
    {"requestId": "not-a-request-id"}, {"extra": "secret-source-value"}, {"rows": [[1]] * 101},
], ids=["bool-cell", "wrong-cell-type", "object-cell", "row-width", "columns", "bool-version", "zero-version", "request-id", "extra-field", "too-many-rows"])
def test_bad_source_response_never_releases_rows(monkeypatch, change):
    platform = Platform()
    platform.result = json.dumps({**json.loads(platform.result), **change})
    with running(monkeypatch, platform) as (browser, _, _):
        response = ask(browser)
    sequence = events(response)
    assert sequence[-1] == ("error", {"code": "connector_unavailable", "requestId": REQUEST})
    assert not any(kind == "complete" or value.get("kind") == "rows" for kind, value in sequence)
    assert "secret-source-value" not in response.text


@pytest.mark.parametrize("body", ["not-json", '{"items":[],"secret":"private"}', '{"items":null}', '{"items":[{}]}'])
def test_bad_metadata_is_safe(monkeypatch, body):
    class BadMetadata(Platform):
        async def handler(self, request):
            if request.url.path == "/internal/connectors/datasets":
                self.calls.append(request)
                return httpx.Response(200, headers={"content-type": "application/json"}, stream=httpx.ByteStream(body.encode()))
            return await super().handler(request)
    with running(monkeypatch, BadMetadata()) as (browser, _, _):
        response = browser.get("/datasets", headers=HEADERS)
    assert response.status_code == 502 and "private" not in response.text


@pytest.mark.parametrize("status,headers", [(302, {"location": "https://secret.invalid", "content-type": "application/json"}),
    (500, {"content-type": "application/json"}), (200, {"content-type": "text/plain"}),
    (200, {"content-type": "application/json; charset=latin1"}), (200, {"content-type": "application/json", "content-encoding": "gzip"})],
    ids=["redirect", "upstream-error", "wrong-media", "wrong-charset", "compressed"])
def test_connector_transport_never_echoes_unsafe_response_or_follows_redirect(monkeypatch, status, headers):
    class BadTransport(Platform):
        async def handler(self, request):
            if request.url.path.endswith("/query"):
                self.calls.append(request)
                return httpx.Response(status, headers=headers, stream=httpx.ByteStream(b"private-source-data"))
            return await super().handler(request)
    with running(monkeypatch, BadTransport()) as (browser, _, platform):
        response = ask(browser)
    assert events(response)[-1][1]["code"] == "connector_unavailable"
    assert "private-source-data" not in response.text
    assert len([call for call in platform.calls if call.url.path.endswith("/query")]) == 1


@pytest.mark.parametrize("origin", ["http://remote.test", "https://user:pass@host.test", "https://host.test/path", "https://host.test?secret=x", "https://host.test#x", "https://host.test:0"])
def test_connector_fixed_origin_rejects_unsafe_address(origin):
    with pytest.raises(ValueError):
        validate_connector_origin(origin, True)


class HeldStream(httpx.AsyncByteStream):
    def __init__(self):
        self.entered = threading.Event()
        self.closed = threading.Event()

    async def __aiter__(self):
        self.entered.set()
        await asyncio.sleep(60)
        yield b"{}"

    async def aclose(self):
        self.closed.set()


@contextmanager
def network_app(monkeypatch, platform):
    monkeypatch.setenv("ASSEMBLEAI_VALIDATION_URL", "https://platform.test/internal/calls/validate")
    monkeypatch.setenv("ASSEMBLEAI_MODEL_GATEWAY_ORIGIN", "https://platform.test")
    monkeypatch.setenv("ASSEMBLEAI_MODEL_ID", "assistant")
    monkeypatch.delenv("ASSEMBLEAI_CONNECTOR_GATEWAY_ORIGIN", raising=False)
    app = create_app("Production")
    original = app.router.lifespan_context

    @asynccontextmanager
    async def lifespan(app):
        async with original(app), httpx.AsyncClient(transport=httpx.MockTransport(platform.handler), trust_env=False) as client:
            app.state.validator = RemoteValidator(client, "https://platform.test/internal/calls/validate", "chatbi", "0.1.0")
            app.state.models = ModelClient(client, validate_model_configuration("https://platform.test", "assistant", "2048", False))
            app.state.connectors = ConnectorClient(client, "https://platform.test")
            yield
    app.router.lifespan_context = lifespan
    listener = socket.socket()
    listener.bind(("127.0.0.1", 0))
    origin = f"http://127.0.0.1:{listener.getsockname()[1]}"
    server = uvicorn.Server(uvicorn.Config(app, log_level="error", lifespan="on"))
    thread = threading.Thread(target=server.run, kwargs={"sockets": [listener]}, daemon=True)
    thread.start()
    try:
        deadline = time.monotonic() + 5
        while not server.started:
            assert time.monotonic() < deadline
            time.sleep(0.01)
        yield origin, app
    finally:
        server.should_exit = True
        thread.join(timeout=5)
        listener.close()


def test_real_socket_first_frame_heartbeat_and_disconnect_close_model(monkeypatch):
    held = HeldStream()
    class HangingModel(Platform):
        async def handler(self, request):
            if "/models/" in request.url.path:
                self.calls.append(request)
                return httpx.Response(200, headers={"content-type": "application/json"}, stream=held)
            return await super().handler(request)

    platform = HangingModel()
    with network_app(monkeypatch, platform) as (origin, app), httpx.Client(timeout=15, trust_env=False) as browser:
        started = time.monotonic()
        with browser.stream("POST", origin + "/ask", headers=HEADERS, json={"datasetId": "production", "question": "x"}) as response:
            assert response.status_code == 200
            lines = response.iter_lines()
            assert next(lines) == "event: delta"
            assert json.loads(next(lines).removeprefix("data: ")) == {"kind": "stage", "stage": "planning"}
            assert time.monotonic() - started < 2
            assert held.entered.wait(2)
            assert next(lines) == ""
            assert next(lines) == ": heartbeat"
            assert 9 <= time.monotonic() - started < 14
        assert held.closed.wait(3)
        deadline = time.monotonic() + 3
        while app.state.streams.active:
            assert time.monotonic() < deadline
            time.sleep(0.01)
        assert app.state.streams.cancelled == 1
    assert sum(call.url.path == "/internal/calls/validate" for call in platform.calls) == 1
    assert not any(call.url.path.endswith("/query") for call in platform.calls)


def test_total_budget_cancels_pending_http_and_emits_no_complete():
    async def scenario():
        held = HeldStream()
        class HangingModel(Platform):
            async def handler(self, request):
                if "/models/" in request.url.path:
                    return httpx.Response(200, headers={"content-type": "application/json"}, stream=held)
                return await super().handler(request)
        platform = HangingModel()
        async with httpx.AsyncClient(transport=httpx.MockTransport(platform.handler)) as client:
            app = SimpleNamespace(state=SimpleNamespace(streams=StreamState(),
                models=ModelClient(client, validate_model_configuration("https://platform.test", "assistant", "2048", False)),
                connectors=ConnectorClient(client, "https://platform.test")))
            frames = [frame async for frame in chatbi_events(app, {"datasetId": "production", "question": "x"}, MODEL_CAP, DATA_CAP, REQUEST, 0.05)]
            assert len(frames) == 2 and "chatbi_timeout" in frames[-1]
            assert not any("event: complete" in frame for frame in frames)
            assert held.closed.is_set() and app.state.streams.failed == 1
    asyncio.run(scenario())


@pytest.mark.parametrize("phase", ["query", "final-check"])
def test_real_socket_disconnect_cancels_pending_connector(monkeypatch, phase):
    held = HeldStream()
    class HangingQuery(Platform):
        async def handler(self, request):
            if (phase == "query" and request.url.path.endswith("/query") or phase == "final-check"
                    and request.url.path.endswith("/check") and any(call.url.path.endswith("/check") for call in self.calls)):
                self.calls.append(request)
                return httpx.Response(200, headers={"content-type": "application/json"}, stream=held)
            return await super().handler(request)
    platform = HangingQuery()
    with network_app(monkeypatch, platform) as (origin, app), httpx.Client(timeout=5, trust_env=False) as browser:
        with browser.stream("POST", origin + "/ask", headers=HEADERS, json={"datasetId": "production", "question": "x"}) as response:
            lines = response.iter_lines()
            while '"stage":"querying"' not in next(lines):
                pass
            assert held.entered.wait(2)
        assert held.closed.wait(3)
        deadline = time.monotonic() + 3
        while app.state.streams.active:
            assert time.monotonic() < deadline
            time.sleep(0.01)
        assert app.state.streams.cancelled == 1
    checks = sum(call.url.path.endswith("/check") for call in platform.calls)
    assert checks == (0 if phase == "query" else 2)


@pytest.mark.parametrize("suffix,headers,body", [
    ("?factory=forged", {}, b'{"datasetId":"production","question":"x"}'),
    ("", {"Content-Type": "text/plain"}, b'{}'),
    ("", {"Content-Type": "application/json; charset=latin1"}, b'{}'),
    ("", {"Content-Encoding": "gzip"}, b'{}'),
], ids=["query-override", "wrong-media", "wrong-charset", "compressed"])
def test_ask_rejects_non_json_protocol_before_any_capability_call(monkeypatch, suffix, headers, body):
    with running(monkeypatch) as (browser, _, platform):
        response = browser.post("/ask" + suffix, headers={**HEADERS, "Content-Type": "application/json", **headers}, content=body)
    assert response.status_code == 400
    assert len(platform.calls) == 1


def test_healthy_process_is_independent_chatbi_service(monkeypatch):
    with running(monkeypatch) as (browser, _, platform):
        live = browser.get("/health/live")
        ready = browser.get("/health/ready")
    assert live.json() == {"status": "live", "service": "chatbi", "version": "0.1.0"}
    assert ready.json() == {"status": "healthy", "service": "chatbi", "version": "0.1.0", "protocolVersion": 1}
    assert not platform.calls


def test_concurrent_answers_never_overlap_checks_within_an_answer():
    async def scenario():
        class SlowChecks(Platform):
            def __init__(self):
                super().__init__()
                self.active = {}
                self.maximum = {}
                self.counts = {}
                self.total_maximum = 0

            async def handler(self, request):
                if not request.url.path.endswith("/check"):
                    return await super().handler(request)
                capability = request.headers["authorization"]
                self.active[capability] = self.active.get(capability, 0) + 1
                self.counts[capability] = self.counts.get(capability, 0) + 1
                self.maximum[capability] = max(self.maximum.get(capability, 0), self.active[capability])
                self.total_maximum = max(self.total_maximum, sum(self.active.values()))
                try:
                    await asyncio.sleep(0.85)
                    return await super().handler(request)
                finally:
                    self.active[capability] -= 1

        platform = SlowChecks()
        async with httpx.AsyncClient(transport=httpx.MockTransport(platform.handler)) as client:
            app = SimpleNamespace(state=SimpleNamespace(streams=StreamState(),
                models=ModelClient(client, validate_model_configuration("https://platform.test", "assistant", "2048", False)),
                connectors=ConnectorClient(client, "https://platform.test")))
            async def answer(capability):
                return [frame async for frame in chatbi_events(app, {"datasetId": "production", "question": "x"},
                                                               MODEL_CAP, capability, REQUEST, 10)]
            answers = await asyncio.gather(answer(DATA_CAP), answer("f" * 64))
            assert all("event: complete" in frames[-1] for frames in answers)
            assert list(platform.maximum.values()) == [1, 1]
            assert platform.total_maximum <= 2
            assert list(platform.counts.values()) == [2, 2]
            assert not any(platform.active.values())
            assert app.state.streams.active == 0 and app.state.streams.completed == 2
    asyncio.run(scenario())


@pytest.mark.parametrize("operation", ["metadata", "check"])
def test_small_connector_callbacks_allow_server_budget_plus_transport(operation):
    async def scenario():
        class NearServerBudget(Platform):
            async def handler(self, request):
                await asyncio.sleep(3.05)
                return await super().handler(request)
        platform = NearServerBudget()
        async with httpx.AsyncClient(transport=httpx.MockTransport(platform.handler)) as client:
            connector = ConnectorClient(client, "https://platform.test")
            if operation == "metadata":
                assert await connector.datasets(DATA_CAP) == {"items": [DATASET]}
            else:
                assert await connector.check(DATA_CAP, DATASET, {"authorizationVersion": 7, "configurationRevision": 2}) is None
            assert len(platform.calls) == 1
    asyncio.run(scenario())


def test_disconnect_during_final_check_cancels_http_without_asgi_task_cancellation():
    async def scenario():
        held = HeldStream()
        class FinalCheckHeld(Platform):
            async def handler(self, request):
                if request.url.path.endswith("/check") and any(call.url.path.endswith("/check") for call in self.calls):
                    self.calls.append(request)
                    return httpx.Response(200, headers={"content-type": "application/json"}, stream=held)
                return await super().handler(request)
        class Request:
            disconnected = False
            async def is_disconnected(self):
                return self.disconnected

        platform = FinalCheckHeld()
        request = Request()
        async with httpx.AsyncClient(transport=httpx.MockTransport(platform.handler)) as client:
            app = SimpleNamespace(state=SimpleNamespace(streams=StreamState(),
                models=ModelClient(client, validate_model_configuration("https://platform.test", "assistant", "2048", False)),
                connectors=ConnectorClient(client, "https://platform.test")))
            frames = []
            async def consume():
                async for frame in chatbi_events(app, {"datasetId": "production", "question": "x"}, MODEL_CAP, DATA_CAP, REQUEST, 10, request):
                    frames.append(frame)
            task = asyncio.create_task(consume())
            async with asyncio.timeout(3):
                while not held.entered.is_set():
                    await asyncio.sleep(0.01)
            request.disconnected = True
            await asyncio.wait_for(task, 0.5)
            assert held.closed.is_set()
            assert not any("event: complete" in frame for frame in frames)
            assert app.state.streams.active == 0 and app.state.streams.cancelled == 1
    asyncio.run(scenario())


def test_delivery_checks_permission_before_next_row_batch_after_one_second():
    async def scenario():
        platform = Platform()
        platform.plan = '{"columns":["line"],"filters":[],"metrics":[],"limit":100}'
        platform.result = json.dumps({"requestId": QUERY_REQUEST, "columns": ["line"], "rows": [["x" * 4096]] * 100,
                                     "authorizationVersion": 7, "configurationRevision": 2})
        async with httpx.AsyncClient(transport=httpx.MockTransport(platform.handler)) as client:
            app = SimpleNamespace(state=SimpleNamespace(streams=StreamState(),
                models=ModelClient(client, validate_model_configuration("https://platform.test", "assistant", "2048", False)),
                connectors=ConnectorClient(client, "https://platform.test")))
            stream = chatbi_events(app, {"datasetId": "production", "question": "x"}, MODEL_CAP, DATA_CAP, REQUEST, 10)
            async with _closing(stream):
                while '"kind":"rows"' not in await anext(stream):
                    pass
                platform.check_status = 403
                await asyncio.sleep(1.05)
                assert sum(call.url.path.endswith("/check") for call in platform.calls) == 1
                frame = await anext(stream)
                assert "event: error" in frame and "authorization_revoked" in frame
                assert sum(call.url.path.endswith("/check") for call in platform.calls) == 2
            assert app.state.streams.failed == 1 and app.state.streams.active == 0
    asyncio.run(scenario())
