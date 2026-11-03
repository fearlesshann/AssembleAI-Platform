"""Exercise the consumer against a real loopback HTTP validation service."""

from contextlib import contextmanager
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import threading
import time

from fastapi.testclient import TestClient
import httpx
import pytest
import uvicorn

from sample_backend.main import create_app


REQUEST_ID = "a" * 32
TOKEN = "test.internal.credential"
HEADERS = {"Authorization": f"Bearer {TOKEN}", "X-Request-ID": REQUEST_ID}
CONTEXT = {
    "userId": "10000000-0000-0000-0000-000000000001",
    "tenantId": "20000000-0000-0000-0000-000000000002",
    "authorizationVersion": 7,
    "requestId": REQUEST_ID,
    "appId": "sample",
    "releaseVersion": "2.0.0",
}


@contextmanager
def validation_server(responder=None):
    calls = []

    class Handler(BaseHTTPRequestHandler):
        def do_POST(self):
            data = self.rfile.read(int(self.headers.get("Content-Length", "0")))
            calls.append((self.path, dict(self.headers), json.loads(data)))
            status, headers, body = (responder(len(calls)) if responder else (
                200, {"Content-Type": "application/json"}, json.dumps(CONTEXT).encode()
            ))
            self.send_response(status)
            for key, value in headers.items():
                self.send_header(key, value)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            try:
                self.wfile.write(body)
            except (BrokenPipeError, ConnectionResetError):
                pass

        def log_message(self, *_args):
            pass

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    worker = threading.Thread(target=server.serve_forever, daemon=True)
    worker.start()
    try:
        yield f"http://127.0.0.1:{server.server_port}/internal/calls/validate", calls
    finally:
        server.shutdown()
        server.server_close()
        worker.join(timeout=2)


@contextmanager
def running_app(app):
    import socket

    listener = socket.socket()
    listener.bind(("127.0.0.1", 0))
    address = listener.getsockname()
    server = uvicorn.Server(uvicorn.Config(app, log_level="error", lifespan="on"))
    worker = threading.Thread(target=server.run, kwargs={"sockets": [listener]}, daemon=True)
    worker.start()
    try:
        deadline = time.monotonic() + 5
        while not server.started:
            if time.monotonic() > deadline:
                raise AssertionError("Sample server did not start")
            time.sleep(0.01)
        yield f"http://127.0.0.1:{address[1]}"
    finally:
        server.should_exit = True
        worker.join(timeout=5)
        listener.close()


@pytest.fixture(autouse=True)
def isolated_configuration(monkeypatch):
    for name in ("ASSEMBLEAI_VALIDATION_URL", "ASSEMBLEAI_SERVICE", "ASSEMBLEAI_SERVICE_VERSION"):
        monkeypatch.delenv(name, raising=False)


@pytest.mark.parametrize("path,method", [("/probe", "post"), ("/events", "get")])
def test_business_routes_reject_forged_identity_without_credentials(path, method):
    with TestClient(create_app("Production")) as client:
        response = getattr(client, method)(path, headers={"X-User-ID": CONTEXT["userId"], "X-Tenant-ID": CONTEXT["tenantId"]})
    assert response.status_code == 401


def test_unconfigured_validator_fails_closed():
    with TestClient(create_app("Production")) as client:
        response = client.post("/probe", headers=HEADERS, json={"message": "hello"})
    assert response.status_code == 503


@pytest.mark.parametrize("url", ["http://example.com/internal/calls/validate", "https://user:pass@example.com/validate", "https://@example.com/internal/calls/validate", "https://example.com/validate?token=x", "https://example.com/validate#fragment", "https://example.com\\evil/validate"])
def test_unsafe_validation_configuration_fails_startup(monkeypatch, url):
    monkeypatch.setenv("ASSEMBLEAI_VALIDATION_URL", url)
    with pytest.raises(ValueError):
        create_app("Development")


def test_production_rejects_plain_http_even_for_loopback(monkeypatch):
    monkeypatch.setenv("ASSEMBLEAI_VALIDATION_URL", "http://127.0.0.1:5100/internal/calls/validate")
    with pytest.raises(ValueError):
        create_app("Production")


def test_probe_uses_remote_verified_context_and_actual_backend_target(monkeypatch):
    with validation_server() as (url, calls):
        monkeypatch.setenv("ASSEMBLEAI_VALIDATION_URL", url)
        monkeypatch.setenv("HTTP_PROXY", "http://127.0.0.1:1")
        monkeypatch.setenv("ALL_PROXY", "http://127.0.0.1:1")
        with TestClient(create_app("Development")) as client:
            response = client.post("/probe", headers={**HEADERS, "X-Tenant-ID": "forged", "Cookie": "secret=browser"}, json={"message": "制造验证"})
    assert response.status_code == 200
    assert response.json() == {"status": "verified", "message": "制造验证", **CONTEXT}
    assert response.headers["x-request-id"] == REQUEST_ID
    assert response.headers["cache-control"] == "no-store"
    assert len(calls) == 1
    path, headers, payload = calls[0]
    headers = {key.lower(): value for key, value in headers.items()}
    assert path == "/internal/calls/validate"
    assert payload == {"service": "sample", "version": "0.1.0", "method": "POST", "path": "/probe"}
    assert headers["authorization"] == f"Bearer {TOKEN}"
    assert headers["x-request-id"] == REQUEST_ID
    assert "cookie" not in headers and "x-tenant-id" not in headers


@pytest.mark.parametrize("headers", [
    {"Authorization": "Basic abc", "X-Request-ID": REQUEST_ID},
    {"Authorization": "Bearer one two", "X-Request-ID": REQUEST_ID},
    {"Authorization": "Bearer " + "a" * 16385, "X-Request-ID": REQUEST_ID},
    {"Authorization": f"Bearer {TOKEN}", "X-Request-ID": "bad"},
    [("Authorization", f"Bearer {TOKEN}"), ("Authorization", f"Bearer {TOKEN}"), ("X-Request-ID", REQUEST_ID)],
    [("Authorization", f"Bearer {TOKEN}"), ("X-Request-ID", REQUEST_ID), ("X-Request-ID", REQUEST_ID)],
])
def test_bad_or_ambiguous_identity_headers_never_reach_verifier(monkeypatch, headers):
    with validation_server() as (url, calls):
        monkeypatch.setenv("ASSEMBLEAI_VALIDATION_URL", url)
        with TestClient(create_app("Development")) as client:
            response = client.post("/probe", headers=headers, json={"message": "hello"})
    assert response.status_code in (400, 401)
    assert not calls


@pytest.mark.parametrize("body,headers,status", [
    (b'{"message":"ok","tenantId":"forged"}', {"Content-Type": "application/json"}, 400),
    (b'{"message":"ok","message":"duplicate"}', {"Content-Type": "application/json"}, 400),
    (b'{"message":""}', {"Content-Type": "application/json"}, 400),
    (b'{"message":123}', {"Content-Type": "application/json"}, 400),
    (json.dumps({"message": "x" * 201}).encode(), {"Content-Type": "application/json"}, 400),
    (b'{"message":"\xff"}', {"Content-Type": "application/json"}, 400),
    (b'{}', {"Content-Type": "application/json; charset=iso-8859-1"}, 415),
    (b'{}', {"Content-Type": "application/json", "Content-Encoding": "gzip"}, 415),
    (b'x' * 4097, {"Content-Type": "application/json"}, 413),
])
def test_probe_accepts_only_small_strict_utf8_message(monkeypatch, body, headers, status):
    with validation_server() as (url, calls):
        monkeypatch.setenv("ASSEMBLEAI_VALIDATION_URL", url)
        with TestClient(create_app("Development")) as client:
            response = client.post("/probe", headers={**HEADERS, **headers}, content=body)
    assert response.status_code == status
    assert "forged" not in response.text


@pytest.mark.parametrize("remote_status,body,expected", [
    (401, b'{}', 401), (403, b'{}', 403), (500, b'secret detail', 503),
    (302, b'{}', 503), (200, b'not json', 503), (200, b'{}', 503),
    (200, b'x' * 4097, 503),
    (200, json.dumps({**CONTEXT, "requestId": "b" * 32}).encode(), 503),
    (200, json.dumps({**CONTEXT, "authorizationVersion": True}).encode(), 503),
    (200, json.dumps({**CONTEXT, "tenantId": "forged"}).encode(), 503),
    (200, b'{"userId":"duplicate",' + json.dumps(CONTEXT).encode()[1:], 503),
])
def test_verifier_denials_and_malformed_responses_fail_closed(monkeypatch, remote_status, body, expected):
    with validation_server(lambda _: (remote_status, {"Content-Type": "application/json", "Location": "/other"}, body)) as (url, calls):
        monkeypatch.setenv("ASSEMBLEAI_VALIDATION_URL", url)
        with TestClient(create_app("Development")) as client:
            response = client.post("/probe", headers=HEADERS, json={"message": "hello"})
    assert response.status_code == expected
    assert TOKEN not in response.text and "secret detail" not in response.text
    assert len(calls) == 1


def test_unreachable_verifier_does_not_fall_back_to_claimed_identity(monkeypatch):
    with validation_server() as (url, _calls):
        pass
    monkeypatch.setenv("ASSEMBLEAI_VALIDATION_URL", url)
    with TestClient(create_app("Development")) as client:
        response = client.post("/probe", headers={**HEADERS, "X-Tenant-ID": CONTEXT["tenantId"]}, json={"message": "hello"})
    assert response.status_code == 503
    assert CONTEXT["tenantId"] not in response.text


def test_slow_verifier_has_a_total_deadline(monkeypatch):
    def respond(_number):
        time.sleep(4)
        return 200, {"Content-Type": "application/json"}, json.dumps(CONTEXT).encode()

    with validation_server(respond) as (url, _calls):
        monkeypatch.setenv("ASSEMBLEAI_VALIDATION_URL", url)
        with TestClient(create_app("Development")) as client:
            start = time.monotonic()
            response = client.post("/probe", headers=HEADERS, json={"message": "hello"})
            elapsed = time.monotonic() - start
    assert response.status_code == 503
    assert elapsed < 3.8


def test_events_use_verified_request_id_and_complete(monkeypatch):
    with validation_server() as (url, calls):
        monkeypatch.setenv("ASSEMBLEAI_VALIDATION_URL", url)
        app = create_app("Development")
        with TestClient(app) as client:
            response = client.get("/events?count=3&delay_ms=10", headers=HEADERS)
    assert response.status_code == 200
    assert response.text.count("event: delta") == 3
    assert response.text.count("event: complete") == 1
    assert response.text.count(REQUEST_ID) == 4
    assert calls[0][2] == {"service": "sample", "version": "0.1.0", "method": "GET", "path": "/events"}
    assert app.state.streams.active == 0 and app.state.streams.completed == 1


def test_events_recheck_policy_and_end_with_error_after_revocation(monkeypatch):
    def respond(number):
        return (200, {"Content-Type": "application/json"}, json.dumps(CONTEXT).encode()) if number == 1 else (403, {}, b'{}')

    with validation_server(respond) as (url, calls):
        monkeypatch.setenv("ASSEMBLEAI_VALIDATION_URL", url)
        app = create_app("Development")
        with TestClient(app) as client:
            response = client.get("/events?count=5&delay_ms=1000", headers=HEADERS)
    assert response.text.startswith("event: delta")
    assert "event: error" in response.text and "authorization_revoked" in response.text
    assert "event: complete" not in response.text
    assert len(calls) == 2
    assert app.state.streams.active == 0 and app.state.streams.failed == 1


def test_real_http_stream_delivers_first_event_then_disconnect_cancels_validation(monkeypatch):
    recheck_started = threading.Event()

    def respond(number):
        if number > 1:
            recheck_started.set()
            time.sleep(3)
        return 200, {"Content-Type": "application/json"}, json.dumps(CONTEXT).encode()

    with validation_server(respond) as (url, _calls):
        monkeypatch.setenv("ASSEMBLEAI_VALIDATION_URL", url)
        app = create_app("Development")
        with running_app(app) as origin, httpx.Client(trust_env=False, timeout=5) as client:
            start = time.monotonic()
            with client.stream("GET", f"{origin}/events?count=20&delay_ms=1000", headers=HEADERS) as response:
                assert response.status_code == 200
                lines = response.iter_lines()
                assert next(lines) == "event: delta"
                assert time.monotonic() - start < 1
                assert recheck_started.wait(timeout=4)
            deadline = time.monotonic() + 1
            while app.state.streams.active and time.monotonic() < deadline:
                time.sleep(0.01)
            assert app.state.streams.active == 0
            assert app.state.streams.cancelled == 1
