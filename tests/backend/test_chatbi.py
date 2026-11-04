"""ChatBI protocol tests use a platform transport fixture, never a supplier."""

import asyncio
from contextlib import contextmanager
import json

from fastapi import HTTPException
from fastapi.testclient import TestClient
import httpx
import pytest

from chatbi_backend.authentication import RemoteValidator
from chatbi_backend.connectors import ConnectorClient
from chatbi_backend.main import create_app
from chatbi_backend.models import ModelClient, validate_model_configuration
from chatbi_backend.planning import parse_plan

REQUEST = "a" * 32
MODEL_REQUEST = "b" * 32
QUERY_REQUEST = "c" * 32
MODEL_CAP = "d" * 64
DATA_CAP = "e" * 64
HEADERS = {"Authorization": "Bearer original.business.jwt", "X-Request-ID": REQUEST,
           "X-Model-Delegation": MODEL_CAP, "X-Connector-Delegation": DATA_CAP}
DATASET = {"sourceId": "manufacturing", "datasetId": "production", "displayName": "制造测试数据",
           "columns": [{"name": "line", "type": "text"}, {"name": "quantity", "type": "integer"},
                       {"name": "cost", "type": "decimal"}, {"name": "day", "type": "date"}], "maxRows": 100}
PLAN = '{"columns":[],"filters":[],"metrics":[{"function":"sum","column":"quantity"}],"limit":100}'
CONTEXT = {"userId": "10000000-0000-0000-0000-000000000001", "tenantId": "20000000-0000-0000-0000-000000000002",
           "authorizationVersion": 7, "requestId": REQUEST, "appId": "chatbi", "releaseVersion": "0.1.0"}


class Platform:
    def __init__(self):
        self.calls = []
        self.plan = PLAN
        self.finish = "stop"
        self.metadata = {"items": [DATASET]}
        self.result = ('{"requestId":"' + QUERY_REQUEST + '","columns":["metric0"],"rows":[[300]],'
                       '"authorizationVersion":7,"configurationRevision":2}')
        self.check_status = 200
        self.query_status = 200
        self.model_status = 200

    async def handler(self, request):
        self.calls.append(request)
        path = request.url.path
        status = 200
        if path == "/internal/calls/validate":
            body = CONTEXT
        elif path == "/internal/connectors/datasets":
            body = self.metadata
        elif path == "/internal/models/assistant/chat":
            status = self.model_status
            body = {"content": self.plan, "finishReason": self.finish, "usage": None, "requestId": MODEL_REQUEST}
        elif path.endswith("/query"):
            status = self.query_status
            body = self.result
        elif path.endswith("/check"):
            status = self.check_status
            body = {"valid": True}
        else:
            raise AssertionError(path)
        raw = body.encode() if isinstance(body, str) else json.dumps(body, ensure_ascii=False).encode()
        return httpx.Response(status, headers={"content-type": "application/json"}, stream=httpx.ByteStream(raw))


@contextmanager
def running(monkeypatch, platform=None):
    for name in ("ASSEMBLEAI_SERVICE", "ASSEMBLEAI_SERVICE_VERSION", "ASSEMBLEAI_CONNECTOR_GATEWAY_ORIGIN"):
        monkeypatch.delenv(name, raising=False)
    monkeypatch.setenv("ASSEMBLEAI_VALIDATION_URL", "https://platform.test/internal/calls/validate")
    monkeypatch.setenv("ASSEMBLEAI_MODEL_GATEWAY_ORIGIN", "https://platform.test")
    monkeypatch.setenv("ASSEMBLEAI_MODEL_ID", "assistant")
    platform = platform or Platform()
    app = create_app("Production")
    with TestClient(app) as browser:
        transport = httpx.AsyncClient(transport=httpx.MockTransport(platform.handler), trust_env=False)
        app.state.validator = RemoteValidator(transport, "https://platform.test/internal/calls/validate", "chatbi", "0.1.0")
        app.state.models = ModelClient(transport, validate_model_configuration("https://platform.test", "assistant", "2048", False))
        app.state.connectors = ConnectorClient(transport, "https://platform.test")
        try:
            yield browser, app, platform
        finally:
            browser.portal.call(transport.aclose)


def events(response):
    result = []
    for frame in response.text.split("\n\n"):
        if not frame or frame.startswith(":"):
            continue
        lines = frame.splitlines()
        assert len(frame.encode()) + 2 <= 32768
        result.append((lines[0].removeprefix("event: "), json.loads(lines[1].removeprefix("data: "))))
    return result


def ask(browser, **kwargs):
    return browser.post("/ask", headers=HEADERS, json={"datasetId": "production", "question": "合计产量是多少？"}, **kwargs)


def test_metadata_and_query_use_distinct_capabilities_validate_original_only_once(monkeypatch):
    with running(monkeypatch) as (browser, _, platform):
        metadata = browser.get("/datasets", headers=HEADERS)
        assert metadata.status_code == 200 and metadata.json() == {"items": [DATASET]}
        platform.calls.clear()
        response = ask(browser)
    assert response.status_code == 200 and response.headers["cache-control"] == "no-store"
    sequence = events(response)
    assert sequence[0] == ("delta", {"kind": "stage", "stage": "planning"})
    assert [value["stage"] for kind, value in sequence if value.get("kind") == "stage"] == ["planning", "querying", "receiving"]
    assert next(value["rows"] for kind, value in sequence if value.get("kind") == "rows") == [["300"]]
    kind, complete = sequence[-1]
    assert kind == "complete" and complete == {"requestId": REQUEST, "modelRequestId": MODEL_REQUEST,
        "queryRequestId": QUERY_REQUEST, "sourceId": "manufacturing", "datasetId": "production", "datasetName": DATASET["displayName"],
        "columns": ["metric0"], "rowCount": 1, "queryText": PLAN, "scope": "当前授权工厂", "summary": "查询完成，共 1 行。"}
    assert sum(request.url.path == "/internal/calls/validate" for request in platform.calls) == 1
    for request in platform.calls[1:]:
        assert request.headers["authorization"] == "Bearer " + (MODEL_CAP if "/models/" in request.url.path else DATA_CAP)
        assert not {"cookie", "x-tenant-id", "x-model-delegation", "x-connector-delegation"} & request.headers.keys()
    model = next(request for request in platform.calls if "/models/" in request.url.path)
    assert json.loads(model.content)["maxOutputTokens"] == 2048
    assert "300" not in json.loads(model.content)["messages"][0]["content"]
    assert sum(request.url.path.endswith("/check") for request in platform.calls) >= 2


@pytest.mark.parametrize("body", [b"{}", b'{"datasetId":"production","question":"x","sql":"SELECT"}',
    b'{"datasetId":"production","question":"x","question":"y"}',
    b'{"datasetId":"production","question":false}', b'{"datasetId":"production","question":"\\ud800"}',
    json.dumps({"datasetId": "production", "question": "x" * 1001}).encode(), b"x" * 8193],
    ids=["missing", "extra", "duplicate", "type", "surrogate", "question-limit", "body-limit"])
def test_strict_body_never_calls_models_or_data(monkeypatch, body):
    with running(monkeypatch) as (browser, _, platform):
        response = browser.post("/ask", headers={**HEADERS, "Content-Type": "application/json"}, content=body)
    assert response.status_code in (400, 413)
    assert len(platform.calls) == 1


@pytest.mark.parametrize("headers", [
    {key: value for key, value in HEADERS.items() if key != "Authorization"},
    {key: value for key, value in HEADERS.items() if key != "X-Connector-Delegation"},
    {**HEADERS, "X-Connector-Delegation": "original.business.jwt"},
    {**HEADERS, "X-Model-Delegation": "D" * 64},
    list(HEADERS.items()) + [("X-Connector-Delegation", DATA_CAP)],
], ids=["missing-jwt", "missing-data-cap", "jwt-is-not-cap", "bad-model-cap", "duplicate-cap"])
def test_bad_capabilities_do_not_reach_model_or_data(monkeypatch, headers):
    with running(monkeypatch) as (browser, _, platform):
        response = browser.post("/ask", headers=headers, json={"datasetId": "production", "question": "x"})
    assert response.status_code in (401, 403)
    assert all(request.url.path == "/internal/calls/validate" for request in platform.calls)


@pytest.mark.parametrize("plan", ["```json\n" + PLAN + "\n```", '{"sql":"SELECT *"}',
    PLAN[:-1] + ',"datasetId":"secret"}', PLAN.replace('"limit":100', '"limit":101'),
    PLAN.replace('"quantity"', '"unknown"'), PLAN.replace('"sum"', '"execute"'),
    PLAN.replace('"limit":100', '"limit":true'), PLAN.replace('"limit":100', '"limit":1.0'),
    '{"columns":["line","line"],"filters":[],"metrics":[],"limit":1}',
    '{"columns":[],"filters":[],"metrics":[],"limit":1}',
    '{"columns":[],"filters":[],"metrics":[{"function":"count","column":null}],"limit":1}',
    '{"refusal":"unsupported_question","columns":[]}', PLAN + " {}"],
    ids=["markdown", "sql", "dataset-override", "rows-limit", "unknown-column", "function", "bool-limit", "float-limit", "duplicate-column", "empty", "count-column", "refusal-extra", "trailing"])
def test_invalid_model_plan_is_never_executed(monkeypatch, plan):
    platform = Platform(); platform.plan = plan
    with running(monkeypatch, platform) as (browser, _, _):
        response = ask(browser)
    assert events(response)[-1][0] == "error"
    assert all(kind != "complete" for kind, _ in events(response))
    assert not any(request.url.path.endswith("/query") for request in platform.calls)


@pytest.mark.parametrize("finish,plan,code", [("length", PLAN, "invalid_query_plan"),
    ("stop", '{"refusal":"unsupported_question"}', "unsupported_question")])
def test_truncation_and_refusal_are_explicit_failures(monkeypatch, finish, plan, code):
    platform = Platform(); platform.finish = finish; platform.plan = plan
    with running(monkeypatch, platform) as (browser, _, _):
        response = ask(browser)
    assert events(response)[-1] == ("error", {"code": code, "requestId": REQUEST})
    assert not any(request.url.path.endswith("/query") for request in platform.calls)


def test_unknown_dataset_fails_before_model(monkeypatch):
    with running(monkeypatch) as (browser, _, platform):
        response = browser.post("/ask", headers=HEADERS, json={"datasetId": "secret", "question": "x"})
    assert events(response)[-1] == ("error", {"code": "dataset_unavailable", "requestId": REQUEST})
    assert not any("/models/" in request.url.path for request in platform.calls)


def test_decimal_literal_forwarded_and_result_precision_retained(monkeypatch):
    platform = Platform()
    platform.plan = '{"columns":["cost","quantity"],"filters":[{"column":"cost","operator":"gte","value":0.1234567890123456789012345678}],"metrics":[],"limit":2}'
    platform.result = ('{"requestId":"' + QUERY_REQUEST + '","columns":["cost","quantity"],'
        '"rows":[[0.1234567890123456789012345678,9223372036854775807]],"authorizationVersion":7,"configurationRevision":2}')
    with running(monkeypatch, platform) as (browser, _, _):
        response = ask(browser)
    query = next(request for request in platform.calls if request.url.path.endswith("/query"))
    assert query.content.decode() == platform.plan
    assert next(value["rows"] for kind, value in events(response) if value.get("kind") == "rows") == [["0.1234567890123456789012345678", "9223372036854775807"]]


@pytest.mark.parametrize("status_field", ["check_status", "query_status", "model_status"])
def test_revoked_delegation_never_completes_or_leaks_upstream(monkeypatch, status_field):
    platform = Platform(); setattr(platform, status_field, 403)
    with running(monkeypatch, platform) as (browser, _, _):
        response = ask(browser)
    assert events(response)[-1] == ("error", {"code": "authorization_revoked", "requestId": REQUEST})
    assert "complete" not in [kind for kind, _ in events(response)]
    assert MODEL_CAP not in response.text and DATA_CAP not in response.text
    assert not any(value.get("kind") == "rows" for _, value in events(response))


@pytest.mark.parametrize("value", ["true", "null", "0.00000000000000000000000000001", "79228162514264337593543950336", "1e1000000", "7.9999999999999999999999999999"])
def test_decimal_filter_rejects_inexact_or_wrong_typed_value(value):
    raw = '{"columns":["cost"],"filters":[{"column":"cost","operator":"eq","value":' + value + '}],"metrics":[],"limit":1}'
    with pytest.raises(HTTPException):
        parse_plan(raw, DATASET)


def test_metadata_allows_catalogs_200_character_unicode_name(monkeypatch):
    platform = Platform()
    platform.metadata = {"items": [{**DATASET, "displayName": "厂" * 200}]}
    with running(monkeypatch, platform) as (browser, _, _):
        response = browser.get("/datasets", headers=HEADERS)
    assert response.status_code == 200
    assert response.json() == platform.metadata


@pytest.mark.parametrize("literal,expected", [("0e-100000", "0"), ("1." + "0" * 10000, "1")], ids=["zero-exponent", "redundant-scale"])
def test_decimal_text_is_bounded_even_with_redundant_source_scale(monkeypatch, literal, expected):
    platform = Platform()
    platform.result = ('{"requestId":"' + QUERY_REQUEST + '","columns":["metric0"],"rows":[[' + literal + ']],'
                       '"authorizationVersion":7,"configurationRevision":2}')
    with running(monkeypatch, platform) as (browser, _, _):
        response = ask(browser)
    sequence = events(response)
    assert sequence[-1][0] == "complete"
    assert next(value["rows"] for _, value in sequence if value.get("kind") == "rows") == [[expected]]
