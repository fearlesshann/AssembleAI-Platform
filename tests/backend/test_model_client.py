"""Delegated model client acceptance against real loopback HTTP, never a real model."""

import asyncio
from contextlib import contextmanager
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import socket
import threading
import time

from fastapi.testclient import TestClient
import httpx
import pytest
import uvicorn

from sample_backend.main import create_app
from sample_backend.models import ModelClient, validate_model_configuration

CAPABILITY = "c" * 64
REQUEST_ID = "a" * 32
MODEL_REQUEST_ID = "b" * 32
HEADERS = {"Authorization": "Bearer test.internal.credential", "X-Request-ID": REQUEST_ID, "X-Model-Delegation": CAPABILITY}
CONTEXT = {"userId": "10000000-0000-0000-0000-000000000001", "tenantId": "20000000-0000-0000-0000-000000000002",
           "authorizationVersion": 7, "requestId": REQUEST_ID, "appId": "sample", "releaseVersion": "1.0.0"}
RESULT = {"content": "制造验证", "finishReason": "stop", "usage": {"promptTokens": 5, "completionTokens": 2, "totalTokens": 9}, "requestId": MODEL_REQUEST_ID}


def event(kind, content=None, usage=None, request_id=MODEL_REQUEST_ID):
    body = {"content": content, "finishReason": "stop" if kind == "complete" else None, "usage": usage, "requestId": request_id}
    return (f"event: {kind}\r\ndata: " + json.dumps(body, ensure_ascii=False) + "\r\n\r\n").encode()


@contextmanager
def platform_server(model_response=None):
    calls = []

    class Handler(BaseHTTPRequestHandler):
        def do_POST(self):
            body = self.rfile.read(int(self.headers.get("Content-Length", "0")))
            calls.append((self.path, {k.lower(): v for k, v in self.headers.items()}, json.loads(body)))
            if self.path == "/internal/calls/validate":
                self.reply(200, "application/json", json.dumps(CONTEXT).encode())
            elif model_response:
                model_response(self)
            else:
                self.reply(200, "application/json", json.dumps(RESULT).encode())

        def reply(self, status, content_type, body):
            self.send_response(status)
            self.send_header("Content-Type", content_type)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            try:
                self.wfile.write(body)
                self.wfile.flush()
            except (BrokenPipeError, ConnectionResetError):
                pass

        def log_message(self, *_args):
            pass

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    server.daemon_threads = True
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        yield f"http://127.0.0.1:{server.server_port}", calls
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=2)


@contextmanager
def running_app(app):
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
        yield origin
    finally:
        server.should_exit = True
        thread.join(timeout=5)
        listener.close()


@pytest.fixture(autouse=True)
def isolated_configuration(monkeypatch):
    for name in ("ASSEMBLEAI_VALIDATION_URL", "ASSEMBLEAI_SERVICE", "ASSEMBLEAI_SERVICE_VERSION", "ASSEMBLEAI_MODEL_GATEWAY_ORIGIN", "ASSEMBLEAI_MODEL_ID", "ASSEMBLEAI_MODEL_MAX_OUTPUT_TOKENS"):
        monkeypatch.delenv(name, raising=False)


def configure(monkeypatch, origin):
    monkeypatch.setenv("ASSEMBLEAI_VALIDATION_URL", origin + "/internal/calls/validate")
    monkeypatch.setenv("ASSEMBLEAI_MODEL_GATEWAY_ORIGIN", origin)
    monkeypatch.setenv("ASSEMBLEAI_MODEL_ID", "assistant")


def test_model_route_validates_business_jwt_then_uses_only_scoped_capability(monkeypatch):
    with platform_server() as (origin, calls):
        configure(monkeypatch, origin)
        with TestClient(create_app("Development")) as client:
            response = client.post("/model-chat", headers={**HEADERS, "Cookie": "secret=browser", "X-Tenant-ID": "forged"}, json={"message": "制造验证"})
    assert response.status_code == 200
    assert response.json() == RESULT
    assert response.headers["cache-control"] == "no-store"
    assert calls[0][2]["path"] == "/model-chat"
    path, headers, payload = calls[1]
    assert path == "/internal/models/assistant/chat"
    assert headers["authorization"] == "Bearer " + CAPABILITY
    assert not {"cookie", "origin", "x-tenant-id", "x-model-delegation"} & headers.keys()
    assert payload == {"messages": [{"role": "user", "content": "制造验证"}], "stream": False, "maxOutputTokens": 128}


@pytest.mark.parametrize("origin", ["http://example.com", "https://user:secret@example.com", "https://@example.com", "https://example.com/v1", "https://example.com?token=x", "https://example.com#x", "https://example.com?", "https://example.com#", "https://example.com\\evil", "https://example.com:0"])
def test_model_origin_rejects_untrusted_configuration(origin):
    with pytest.raises(ValueError, match="model configuration"):
        validate_model_configuration(origin, "assistant", "128", True)


@pytest.mark.parametrize("headers", [
    {k: v for k, v in HEADERS.items() if k != "Authorization"},
    {**HEADERS, "X-Model-Delegation": "test.internal.credential"},
    {**HEADERS, "X-Model-Delegation": "c" * 63},
    list(HEADERS.items()) + [("X-Model-Delegation", CAPABILITY)],
], ids=["missing-jwt", "jwt-is-not-capability", "short-capability", "duplicate-capability"])
def test_missing_credentials_and_invalid_capabilities_never_call_models(monkeypatch, headers):
    with platform_server() as (origin, calls):
        configure(monkeypatch, origin)
        with TestClient(create_app("Development")) as client:
            response = client.post("/model-chat", headers=headers, json={"message": "hello"})
    assert response.status_code in (401, 403)
    assert not [call for call in calls if call[0] != "/internal/calls/validate"]


@pytest.mark.parametrize("body", [b'{"message":"hello","tenantId":"forged"}', b'{"message":"one","message":"two"}', b'{"message":"hello","stream":1}', b'{"message":"\\ud800"}', b'{"message":" "}', b'{"message":' + b'"x"' * 2000 + b'}'], ids=["unknown", "duplicate", "stream-type", "surrogate", "empty", "size"])
def test_model_message_rejects_unknown_duplicate_types_unicode_and_size(monkeypatch, body):
    with platform_server() as (origin, calls):
        configure(monkeypatch, origin)
        with TestClient(create_app("Development")) as client:
            response = client.post("/model-chat", headers={**HEADERS, "Content-Type": "application/json"}, content=body)
    assert response.status_code in (400, 413)
    assert len(calls) == 1


def test_unconfigured_model_fails_closed(monkeypatch):
    with platform_server() as (origin, calls):
        monkeypatch.setenv("ASSEMBLEAI_VALIDATION_URL", origin + "/internal/calls/validate")
        with TestClient(create_app("Development")) as client:
            response = client.post("/model-chat", headers=HEADERS, json={"message": "hello"})
    assert response.status_code == 503
    assert len(calls) == 1


@pytest.mark.parametrize("status,media,body", [
    (403, "application/json", b'{"secret":"do-not-echo"}'),
    (302, "text/plain", b'do-not-echo'),
    (500, "application/json", b'do-not-echo'),
    (200, "text/plain", b'do-not-echo'),
    (200, "application/json", b'{"secret":"do-not-echo"}'),
    (200, "application/json", json.dumps({**RESULT, "usage": {"promptTokens": True, "completionTokens": 2, "totalTokens": 3}}).encode()),
    (200, "application/json", json.dumps({**RESULT, "content": "do-not-echo" * 100000}).encode()),
], ids=["denied", "redirect", "upstream-error", "media", "shape", "usage-type", "size"])
def test_model_rejection_and_invalid_responses_are_bounded_and_safe(monkeypatch, status, media, body):
    with platform_server(lambda handler: handler.reply(status, media, body)) as (origin, calls):
        configure(monkeypatch, origin)
        with TestClient(create_app("Development")) as client:
            response = client.post("/model-chat", headers=HEADERS, json={"message": "hello"})
    assert response.status_code == (403 if status == 403 else 502)
    assert "do-not-echo" not in response.text and CAPABILITY not in response.text
    assert len(calls) == 2  # No retry or redirect.


@pytest.mark.parametrize("streaming", [False, True], ids=["json", "sse"])
def test_surrogate_finish_reason_is_rejected_before_response_encoding(monkeypatch, streaming):
    payload = {**RESULT, "content": None if streaming else RESULT["content"], "finishReason": "\ud800"}
    encoded = json.dumps(payload).encode("ascii")
    body = event("delta", "partial") + b"event: complete\ndata: " + encoded + b"\n\n" if streaming else encoded
    media = "text/event-stream" if streaming else "application/json"
    with platform_server(lambda handler: handler.reply(200, media, body)) as (origin, _calls):
        configure(monkeypatch, origin)
        with TestClient(create_app("Development")) as client:
            response = client.post("/model-chat", headers=HEADERS, json={"message": "hello", "stream": streaming})
    assert "model_response_unavailable" in response.text
    if streaming:
        assert "event: error" in response.text and "event: complete" not in response.text
    else:
        assert response.status_code == 502


def test_sse_retains_usage_on_sdk_complete_and_parses_split_utf8(monkeypatch):
    body = event("delta", "制造") + event("usage", usage=RESULT["usage"]) + event("complete")

    def reply(handler):
        handler.send_response(200)
        handler.send_header("Content-Type", "text/event-stream; charset=utf-8")
        handler.send_header("Content-Length", str(len(body)))
        handler.end_headers()
        for byte in body:
            handler.wfile.write(bytes([byte]))
            handler.wfile.flush()

    with platform_server(reply) as (origin, _calls):
        configure(monkeypatch, origin)
        with TestClient(create_app("Development")) as client:
            response = client.post("/model-chat", headers=HEADERS, json={"message": "hello", "stream": True})
    assert response.status_code == 200
    assert "event: delta" in response.text and '"text": "制造"' in response.text
    assert "event: complete" in response.text and '"totalTokens": 9' in response.text
    assert "event: usage" not in response.text and "event: error" not in response.text


def test_large_delta_is_split_within_sdk_frame_bound_and_text_is_preserved(monkeypatch):
    content = "制" * 18000
    body = event("delta", content) + event("complete")
    with platform_server(lambda handler: handler.reply(200, "text/event-stream", body)) as (origin, _calls):
        configure(monkeypatch, origin)
        with TestClient(create_app("Development")) as client:
            response = client.post("/model-chat", headers=HEADERS, json={"message": "hello", "stream": True})
    frames = [frame for frame in response.text.split("\n\n") if frame]
    assert all(len(frame.encode()) + 2 < 65536 for frame in frames)
    fragments = [json.loads(frame.split("data: ", 1)[1])["text"] for frame in frames if frame.startswith("event: delta")]
    assert "".join(fragments) == content
    assert frames[-1].startswith("event: complete")


def test_model_client_total_deadline_closes_pending_connection():
    disconnected = threading.Event()

    def pending(handler):
        handler.connection.settimeout(3)
        try:
            if handler.connection.recv(1) == b"":
                disconnected.set()
        except (ConnectionResetError, BrokenPipeError):
            disconnected.set()

    async def verify(origin):
        async with httpx.AsyncClient(trust_env=False, timeout=None) as http:
            client = ModelClient(http, validate_model_configuration(origin, "assistant", "128", True), total_seconds=0.1)
            from fastapi import HTTPException
            with pytest.raises(HTTPException) as error:
                await client.complete(CAPABILITY, "hello")
            assert error.value.status_code == 502

    with platform_server(pending) as (origin, _calls):
        asyncio.run(verify(origin))
        assert disconnected.wait(1)


@pytest.mark.parametrize("body", [
    event("delta", "partial"),
    event("delta", "partial") + event("complete", request_id="d" * 32),
    event("delta", "partial") + b'event: complete\ndata: {bad}\n\n',
    b'event: delta\ndata: {"content":"\xff"}\n\n',
    b":" + b"x" * 65536 + b"\n\n",
    (b":" + b"x" * 65530 + b"\r\n") * 5 + b"\r\n",
], ids=["incomplete", "changed-request", "malformed", "invalid-utf8", "line-limit", "event-limit"])
def test_incomplete_invalid_changed_identity_and_oversize_stream_never_complete(monkeypatch, body):
    with platform_server(lambda handler: handler.reply(200, "text/event-stream", body)) as (origin, _calls):
        configure(monkeypatch, origin)
        with TestClient(create_app("Development")) as client:
            response = client.post("/model-chat", headers=HEADERS, json={"message": "hello", "stream": True})
    assert "event: error" in response.text and "event: complete" not in response.text
    assert CAPABILITY not in response.text


@pytest.mark.parametrize("streaming", [False, True])
def test_real_client_disconnect_closes_pending_model_http(monkeypatch, streaming):
    started = threading.Event()
    disconnected = threading.Event()

    def pending(handler):
        if streaming:
            handler.send_response(200)
            handler.send_header("Content-Type", "text/event-stream")
            handler.end_headers()
            handler.wfile.write(event("delta", "first"))
            handler.wfile.flush()
        started.set()
        handler.connection.settimeout(4)
        try:
            if handler.connection.recv(1) == b"":
                disconnected.set()
        except (ConnectionResetError, BrokenPipeError):
            disconnected.set()
        except TimeoutError:
            pass

    with platform_server(pending) as (origin, _calls):
        configure(monkeypatch, origin)
        app = create_app("Development")
        with running_app(app) as sample_origin:
            if streaming:
                with httpx.Client(trust_env=False, timeout=3) as client:
                    before = time.monotonic()
                    with client.stream("POST", sample_origin + "/model-chat", headers=HEADERS, json={"message": "hello", "stream": True}) as response:
                        lines = response.iter_lines()
                        assert next(lines) == "event: delta"
                        assert time.monotonic() - before < 1.5
                        assert started.wait(1)
            else:
                parsed = httpx.URL(sample_origin)
                connection = socket.create_connection((parsed.host, parsed.port), timeout=3)
                body = b'{"message":"hello"}'
                headers = {**HEADERS, "Host": parsed.host, "Content-Type": "application/json", "Content-Length": str(len(body))}
                packet = "POST /model-chat HTTP/1.1\r\n" + "\r\n".join(k + ": " + v for k, v in headers.items()) + "\r\n\r\n"
                connection.sendall(packet.encode() + body)
                assert started.wait(2)
                connection.close()
            assert disconnected.wait(2), "The application must cancel its real platform HTTP connection."
            if streaming:
                deadline = time.monotonic() + 1
                while app.state.streams.active and time.monotonic() < deadline:
                    time.sleep(0.01)
                assert app.state.streams.active == 0 and app.state.streams.cancelled == 1
