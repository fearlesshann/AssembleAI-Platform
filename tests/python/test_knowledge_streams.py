"""Real browser sockets and guarded stream cleanup; no external provider calls."""
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

from knowledge_backend.application import StreamState, create_app, knowledge_events
from knowledge_backend.authentication import RemoteValidator
from knowledge_backend.knowledge import KnowledgeClient
from knowledge_backend.models import ModelClient, validate_model_configuration
from test_knowledge_application import HEADERS, KNOWLEDGE_CAP, MODEL_CAP, REQUEST, Platform


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


def clients(app, client):
    app.state.validator = RemoteValidator(client, "https://platform.test/internal/calls/validate", "knowledge", "0.1.0")
    app.state.models = ModelClient(client, validate_model_configuration("https://platform.test", "assistant", "2048", False))
    app.state.knowledge = KnowledgeClient(client, "https://platform.test")


@contextmanager
def network_app(monkeypatch, platform):
    monkeypatch.setenv("ASSEMBLEAI_VALIDATION_URL", "https://platform.test/internal/calls/validate")
    monkeypatch.setenv("ASSEMBLEAI_MODEL_GATEWAY_ORIGIN", "https://platform.test")
    monkeypatch.setenv("ASSEMBLEAI_MODEL_ID", "assistant")
    monkeypatch.delenv("ASSEMBLEAI_KNOWLEDGE_GATEWAY_ORIGIN", raising=False)
    app = create_app("Production")
    original = app.router.lifespan_context
    @asynccontextmanager
    async def lifespan(app):
        async with original(app), httpx.AsyncClient(transport=httpx.MockTransport(platform.handler), trust_env=False) as client:
            clients(app, client)
            yield
    app.router.lifespan_context = lifespan
    listener = socket.socket()
    listener.bind(("127.0.0.1", 0))
    origin = f"http://127.0.0.1:{listener.getsockname()[1]}"
    server = uvicorn.Server(uvicorn.Config(app, log_level="error", lifespan="on"))
    thread = threading.Thread(target=server.run, kwargs={"sockets":[listener]}, daemon=True)
    thread.start()
    try:
        deadline = time.monotonic() + 10
        while not server.started:
            assert time.monotonic() < deadline
            time.sleep(0.01)
        yield origin, app
    finally:
        server.should_exit = True
        thread.join(timeout=5)
        listener.close()
        assert not thread.is_alive()


def test_real_socket_immediate_frame_heartbeat_and_model_disconnect(monkeypatch):
    held = HeldStream()
    class HangingModel(Platform):
        async def handler(self, request):
            if "/models/" in request.url.path:
                self.calls.append(request)
                return httpx.Response(200, headers={"content-type":"application/json"}, stream=held)
            return await super().handler(request)
    platform = HangingModel()
    with network_app(monkeypatch, platform) as (origin, app), httpx.Client(timeout=15, trust_env=False) as browser:
        started = time.monotonic()
        with browser.stream("POST", origin+"/ask", headers=HEADERS, json={"question":"设备怎样维护？"}) as response:
            assert response.status_code == 200
            lines = response.iter_lines()
            assert next(lines) == "event: delta"
            assert json.loads(next(lines).removeprefix("data: ")) == {"kind":"stage","stage":"retrieving"}
            assert time.monotonic()-started < 3
            assert held.entered.wait(3)
            while next(lines) != ": heartbeat":
                pass
            assert 9 <= time.monotonic()-started < 14
        assert held.closed.wait(3)
        deadline = time.monotonic()+3
        while app.state.streams.active:
            assert time.monotonic()<deadline
            time.sleep(0.01)
        assert app.state.streams.cancelled == 1
    assert sum(r.url.path.endswith("/validate") for r in platform.calls) == 1


@pytest.mark.parametrize("phase", ["search", "initial-check", "final-check"])
def test_real_socket_disconnect_closes_knowledge_http_without_complete(monkeypatch, phase):
    held = HeldStream()
    class HangingKnowledge(Platform):
        async def handler(self, request):
            if (phase == "search" and request.url.path.endswith("/search") or
                    phase == "initial-check" and request.url.path.endswith("/check") or
                    phase == "final-check" and request.url.path.endswith("/check") and self.checks == 1):
                self.calls.append(request)
                return httpx.Response(200, headers={"content-type":"application/json"}, stream=held)
            return await super().handler(request)
    platform = HangingKnowledge()
    with network_app(monkeypatch,platform) as (origin,app), httpx.Client(timeout=5,trust_env=False) as browser:
        with browser.stream("POST",origin+"/ask",headers=HEADERS,json={"question":"x"}) as response:
            lines = response.iter_lines()
            assert next(lines) == "event: delta"
            assert held.entered.wait(3)
        assert held.closed.wait(3)
        deadline = time.monotonic()+3
        while app.state.streams.active:
            assert time.monotonic()<deadline
            time.sleep(0.01)
        assert app.state.streams.cancelled == 1 and app.state.streams.completed == 0


def test_total_deadline_cancels_model_and_emits_only_safe_error():
    async def scenario():
        held = HeldStream()
        class Hanging(Platform):
            async def handler(self,request):
                if "/models/" in request.url.path:
                    return httpx.Response(200,headers={"content-type":"application/json"},stream=held)
                return await super().handler(request)
        async with httpx.AsyncClient(transport=httpx.MockTransport(Hanging().handler)) as client:
            app = SimpleNamespace(state=SimpleNamespace(streams=StreamState()))
            clients(app,client)
            frames = [f async for f in knowledge_events(app,"x",MODEL_CAP,KNOWLEDGE_CAP,REQUEST,0.1)]
            assert "knowledge_timeout" in frames[-1] and not any("event: complete" in f for f in frames)
            assert held.closed.is_set() and app.state.streams.active == 0 and app.state.streams.failed == 1
    asyncio.run(scenario())


def test_concurrent_answers_have_single_check_each_and_no_residual_work():
    async def scenario():
        active, maximum = {}, {}
        class SlowChecks(Platform):
            async def handler(self,request):
                if not request.url.path.endswith("/check"):
                    return await super().handler(request)
                cap = request.headers["authorization"]
                active[cap] = active.get(cap,0)+1
                maximum[cap] = max(maximum.get(cap,0),active[cap])
                try:
                    await asyncio.sleep(0.1)
                    return await super().handler(request)
                finally:
                    active[cap] -= 1
        async with httpx.AsyncClient(transport=httpx.MockTransport(SlowChecks().handler)) as client:
            app = SimpleNamespace(state=SimpleNamespace(streams=StreamState()))
            clients(app,client)
            async def consume(cap):
                return [f async for f in knowledge_events(app,"x",MODEL_CAP,cap,REQUEST)]
            results = await asyncio.gather(consume("e"*64),consume("f"*64))
            assert all("event: complete" in frames[-1] for frames in results)
            assert list(maximum.values()) == [1,1] and list(active.values()) == [0,0]
            assert app.state.streams.active == 0 and app.state.streams.completed == 2
    asyncio.run(scenario())


def test_reference_disconnect_closes_guard_http(monkeypatch):
    from test_knowledge_application import DOCUMENT, PROFILE
    held = HeldStream()
    class Hanging(Platform):
        async def handler(self,request):
            if request.url.path.endswith("/check"):
                return httpx.Response(200,headers={"content-type":"application/json"},stream=held)
            return await super().handler(request)
    with network_app(monkeypatch,Hanging()) as (origin,_), httpx.Client(timeout=0.2,trust_env=False) as browser:
        with pytest.raises(httpx.ReadTimeout):
            browser.post(origin+"/references",headers=HEADERS,json={"profile":PROFILE,"authorizationVersion":"7","documents":[{"documentId":DOCUMENT,"revision":2}]})
        assert held.entered.wait(3) and held.closed.wait(3)
