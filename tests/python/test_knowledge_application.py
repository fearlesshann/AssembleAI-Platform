"""Public knowledge protocol exercises real clients against a platform transport."""
import asyncio
from contextlib import contextmanager
import json

from fastapi.testclient import TestClient
import httpx
import pytest

REQUEST = "a" * 32
MODEL_REQUEST = "b" * 32
SEARCH_REQUEST = "c" * 32
MODEL_CAP = "d" * 64
KNOWLEDGE_CAP = "e" * 64
PROFILE = "e5-small-dd476dd0-384-v1"
DOCUMENT = "10000000-0000-0000-0000-000000000001"
OTHER_DOCUMENT = "10000000-0000-0000-0000-000000000002"
CHUNK = "10000000000000000000000000000001-2-0-0"
HIT = {"chunkId": CHUNK, "documentId": DOCUMENT, "documentRevision": 2, "fileName": "制造维护.pdf",
       "locator": "page:2", "text": "轴承温度过高，应立即停机检查润滑油和冷却系统。", "score": 0.85}
HEADERS = {"Authorization": "Bearer original.business.jwt", "X-Request-ID": REQUEST,
           "X-Model-Delegation": MODEL_CAP, "X-Knowledge-Delegation": KNOWLEDGE_CAP}
CONTEXT = {"userId": DOCUMENT, "tenantId": OTHER_DOCUMENT, "authorizationVersion": 7,
           "requestId": REQUEST, "appId": "knowledge", "releaseVersion": "0.1.0"}
ANSWER = {"answer": "应立即停机并检查润滑油与冷却系统。", "citations": [{"chunkId": CHUNK, "quote": "应立即停机检查润滑油和冷却系统"}]}


class Platform:
    def __init__(self):
        self.calls = []
        self.hits = [dict(HIT)]
        self.content = json.dumps(ANSWER, ensure_ascii=False)
        self.finish = "stop"
        self.check_statuses = [200, 200]
        self.checks = 0
        self.search_status = 200
        self.version = 7

    async def handler(self, request):
        self.calls.append(request)
        path = request.url.path
        status = 200
        if path == "/internal/calls/validate":
            body = CONTEXT
        elif path == "/internal/knowledge/search":
            status = self.search_status
            body = {"requestId": SEARCH_REQUEST, "authorizationVersion": self.version, "profile": PROFILE, "hits": self.hits}
        elif path == "/internal/knowledge/check":
            status = self.check_statuses[min(self.checks, len(self.check_statuses)-1)]
            self.checks += 1
            body = {"valid": True}
        elif path == "/internal/models/assistant/chat":
            body = {"content": self.content, "finishReason": self.finish, "usage": None, "requestId": MODEL_REQUEST}
        else:
            raise AssertionError(path)
        raw = json.dumps(body, ensure_ascii=False).encode()
        return httpx.Response(status, headers={"content-type": "application/json"}, stream=httpx.ByteStream(raw))


@contextmanager
def running(monkeypatch, platform=None):
    from knowledge_backend.application import create_app
    from knowledge_backend.authentication import RemoteValidator
    from knowledge_backend.knowledge import KnowledgeClient
    from knowledge_backend.models import ModelClient, validate_model_configuration
    for name in ("ASSEMBLEAI_SERVICE", "ASSEMBLEAI_SERVICE_VERSION", "ASSEMBLEAI_KNOWLEDGE_GATEWAY_ORIGIN"):
        monkeypatch.delenv(name, raising=False)
    monkeypatch.setenv("ASSEMBLEAI_VALIDATION_URL", "https://platform.test/internal/calls/validate")
    monkeypatch.setenv("ASSEMBLEAI_MODEL_GATEWAY_ORIGIN", "https://platform.test")
    monkeypatch.setenv("ASSEMBLEAI_MODEL_ID", "assistant")
    platform = platform or Platform()
    app = create_app("Production")
    with TestClient(app) as browser:
        transport = httpx.AsyncClient(transport=httpx.MockTransport(platform.handler), trust_env=False,
                                    headers={"X-Tenant-ID": "untrusted"}, cookies={"session": "untrusted"})
        app.state.validator = RemoteValidator(transport, "https://platform.test/internal/calls/validate", "knowledge", "0.1.0")
        app.state.models = ModelClient(transport, validate_model_configuration("https://platform.test", "assistant", "2048", False))
        app.state.knowledge = KnowledgeClient(transport, "https://platform.test")
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
        result.append((lines[0].removeprefix("event: "), json.loads(lines[1].removeprefix("data: "))))
    return result


def ask(browser):
    return browser.post("/ask", headers=HEADERS, json={"question": "轴承过热怎么处理？"})


def test_answer_trusted_citations_distinct_capabilities_and_all_context_guards(monkeypatch):
    platform = Platform()
    platform.hits.append({**HIT, "documentId": OTHER_DOCUMENT, "chunkId": "10000000000000000000000000000002-2-0-0", "text": "无关员工制度。"})
    platform.version = 9223372036854775807
    with running(monkeypatch, platform) as (browser, app, _):
        response = ask(browser)
        assert app.state.streams.active == 0
    sequence = events(response)
    assert [v["stage"] for k,v in sequence if k == "delta"] == ["retrieving", "answering", "verifying"]
    kind, complete = sequence[-1]
    assert kind == "complete"
    assert complete == {"requestId": REQUEST, "modelRequestId": MODEL_REQUEST, "searchRequestId": SEARCH_REQUEST,
        "answer": ANSWER["answer"], "refusal": False, "profile": PROFILE, "authorizationVersion": "9223372036854775807",
        "documents": [{"documentId": DOCUMENT, "revision": 2}, {"documentId": OTHER_DOCUMENT, "revision": 2}],
        "citations": [{key: HIT[key] for key in ("documentId", "documentRevision", "chunkId", "fileName", "locator")} | {"quote": ANSWER["citations"][0]["quote"]}]}
    assert [r.url.path for r in platform.calls] == ["/internal/calls/validate", "/internal/knowledge/search", "/internal/knowledge/check", "/internal/models/assistant/chat", "/internal/knowledge/check"]
    for request in platform.calls[1:]:
        assert request.headers["authorization"] == "Bearer " + (MODEL_CAP if "/models/" in request.url.path else KNOWLEDGE_CAP)
        assert not {"cookie", "x-tenant-id", "x-knowledge-delegation", "x-model-delegation"} & request.headers.keys()
    checks = [json.loads(r.content) for r in platform.calls if r.url.path.endswith("/check")]
    assert checks[0] == checks[1] == {"authorizationVersion": 9223372036854775807, "profile": PROFILE, "documents": complete["documents"]}
    prompt = json.loads(platform.calls[3].content)["messages"][0]["content"]
    assert HIT["text"] in prompt and "无关员工制度。" in prompt and "不可信" in prompt


@pytest.mark.parametrize("body", [b"{}", b'{"question":"x","tenantId":"evil"}', b'{"question":"x","question":"y"}',
    b'{"question":true}', b'{"question":"\\ud800"}', b'{"question":"\\u0000"}', json.dumps({"question":"x"*1001}).encode(), b"x"*8193],
    ids=["missing", "extra", "duplicate", "type", "surrogate", "nul", "question-limit", "body-limit"])
def test_strict_request_prevents_search_and_model(monkeypatch, body):
    with running(monkeypatch) as (browser, _, platform):
        response = browser.post("/ask", headers={**HEADERS,"Content-Type":"application/json"}, content=body)
    assert response.status_code in (400, 413)
    assert len(platform.calls) == 1


@pytest.mark.parametrize("headers", [
    {k:v for k,v in HEADERS.items() if k != "X-Knowledge-Delegation"},
    {**HEADERS,"X-Knowledge-Delegation":"original.business.jwt"},
    list(HEADERS.items())+[("X-Knowledge-Delegation",KNOWLEDGE_CAP)],
    {**HEADERS,"X-Model-Delegation":"D"*64}])
def test_independent_capability_required(monkeypatch, headers):
    with running(monkeypatch) as (browser, _, platform):
        response = browser.post("/ask", headers=headers, json={"question":"x"})
    assert response.status_code == 403
    assert all(r.url.path == "/internal/calls/validate" for r in platform.calls)


@pytest.mark.parametrize("value", [
    {"answer":"forged", "citations":[{"chunkId":"secret", "quote":"应立即停机"}]},
    {"answer":"forged", "citations":[{"chunkId":CHUNK, "quote":"制造虚假原文"}]},
    {"answer":"x","citations":[]}, {"answer":"x","citations":ANSWER["citations"]*2},
    {**ANSWER,"documentId":"secret"}, {"answer":"x"*4001,"citations":ANSWER["citations"]},
    {"answer":"\ud800","citations":ANSWER["citations"]},
    {"refusal":"anything"}, {"refusal":"insufficient_evidence","answer":"secret"}])
def test_invalid_answers_never_release_complete(monkeypatch, value):
    platform = Platform()
    platform.content = json.dumps(value)
    with running(monkeypatch, platform) as (browser, _, _):
        response = ask(browser)
    sequence = events(response)
    assert sequence[-1] == ("error", {"code":"invalid_knowledge_answer","requestId":REQUEST})
    assert all(k != "complete" for k,v in sequence)
    assert "forged" not in response.text and "secret" not in response.text


@pytest.mark.parametrize("content", ['```json\n{}\n```', '{"answer":"x","answer":"y","citations":[]}', 'null', 'NaN'])
def test_noncanonical_model_json_rejected(monkeypatch, content):
    platform = Platform(); platform.content = content
    with running(monkeypatch, platform) as (browser, _, _):
        assert events(ask(browser))[-1][1]["code"] == "invalid_knowledge_answer"


@pytest.mark.parametrize("finish", [None, "length", "tool_calls"])
def test_truncated_or_nontext_model_plan_rejected(monkeypatch, finish):
    platform = Platform(); platform.finish = finish
    with running(monkeypatch, platform) as (browser, _, _):
        assert events(ask(browser))[-1][1]["code"] == "invalid_knowledge_answer"


@pytest.mark.parametrize("empty", [True, False])
def test_empty_retrieval_or_model_refusal_has_fixed_answer(monkeypatch, empty):
    platform = Platform()
    if empty:
        platform.hits = []
    else:
        platform.content = '{"refusal":"insufficient_evidence"}'
    with running(monkeypatch, platform) as (browser, _, _):
        sequence = events(ask(browser))
        complete = sequence[-1]
    assert [v["stage"] for k,v in sequence if k == "delta"] == (["retrieving","verifying"] if empty else ["retrieving","answering","verifying"])
    assert complete[0] == "complete" and complete[1]["refusal"] is True
    assert complete[1]["answer"] == "当前授权资料中没有足够依据回答这个问题。"
    assert complete[1]["citations"] == []
    assert complete[1]["modelRequestId"] == (None if empty else MODEL_REQUEST)
    assert sum("/models/" in r.url.path for r in platform.calls) == (0 if empty else 1)


@pytest.mark.parametrize("statuses", [[403], [200,403]])
def test_revocation_before_model_or_complete_releases_no_answer(monkeypatch, statuses):
    platform = Platform(); platform.check_statuses = statuses
    with running(monkeypatch, platform) as (browser, app, _):
        response = ask(browser)
        assert app.state.streams.active == 0 and app.state.streams.failed == 1
    assert events(response)[-1] == ("error", {"code":"authorization_revoked", "requestId":REQUEST})
    assert all(k != "complete" for k,v in events(response))
    assert sum("/models/" in r.url.path for r in platform.calls) == len(statuses)-1


def test_references_requires_new_knowledge_cap_without_model_cap(monkeypatch):
    body = {"profile": PROFILE, "authorizationVersion": "9223372036854775807", "documents": [{"documentId":DOCUMENT,"revision":2}]}
    with running(monkeypatch) as (browser, _, platform):
        headers = {k:v for k,v in HEADERS.items() if k != "X-Model-Delegation"}
        response = browser.post("/references", headers=headers, json=body)
    assert response.status_code == 200 and response.json() == {"valid":True}
    assert len(platform.calls) == 2 and platform.calls[-1].url.path.endswith("/check")
    assert json.loads(platform.calls[-1].content) == {**body,"authorizationVersion":9223372036854775807}


@pytest.mark.parametrize("change", [{"authorizationVersion":7},{"authorizationVersion":"01"},{"authorizationVersion":"9223372036854775808"},
    {"profile":"other"},{"documents":[{"documentId":DOCUMENT,"revision":True}]},
    {"documents":[{"documentId":DOCUMENT,"revision":2}]*2},{"extra":1}])
def test_invalid_reference_guards_do_not_reach_platform(monkeypatch, change):
    body = {"profile":PROFILE,"authorizationVersion":"7","documents":[{"documentId":DOCUMENT,"revision":2}],**change}
    with running(monkeypatch) as (browser, _, platform):
        response = browser.post("/references", headers=HEADERS, json=body)
    assert response.status_code == 400 and len(platform.calls) == 1


@pytest.mark.parametrize("change", [{"chunkId":"secret"},{"documentId":OTHER_DOCUMENT},{"documentRevision":True},
    {"text":"\ud800"},{"score":float("nan")},{"score":True},{"extra":1},{"locator":"x"*201}])
def test_malformed_search_hits_never_reach_model(monkeypatch, change):
    platform = Platform(); platform.hits = [{**HIT,**change}]
    # JSON's surrogate escape remains a valid byte sequence to test the consumer.
    original = platform.handler
    async def handler(request):
        if request.url.path.endswith("/search"):
            platform.calls.append(request)
            body = {"requestId":SEARCH_REQUEST,"authorizationVersion":7,"profile":PROFILE,"hits":platform.hits}
            return httpx.Response(200,headers={"content-type":"application/json"},stream=httpx.ByteStream(json.dumps(body).encode()))
        return await original(request)
    platform.handler = handler
    with running(monkeypatch, platform) as (browser, _, _):
        response = ask(browser)
    assert events(response)[-1] == ("error",{"code":"knowledge_unavailable","requestId":REQUEST})
    assert not any("/models/" in r.url.path for r in platform.calls)


def test_full_platform_locator_bound_is_preserved_in_citation(monkeypatch):
    platform = Platform(); platform.hits = [{**HIT, "locator":"x"*200}]
    with running(monkeypatch,platform) as (browser,_,_):
        complete = events(ask(browser))[-1]
    assert complete[0] == "complete" and complete[1]["citations"][0]["locator"] == "x"*200


@pytest.mark.parametrize("status,headers,raw", [
    (302,{"Location":"https://untrusted.invalid"},b""),
    (500,{},b"secret-provider-key"),
    (200,{"content-type":"text/plain"},b"private body"),
    (200,{"content-encoding":"gzip"},b"private body"),
    (200,{},b"x"*131073)], ids=["redirect", "error", "media", "encoding", "size"])
def test_bad_upstream_protocol_is_safe_and_not_followed(monkeypatch,status,headers,raw):
    platform = Platform(); original = platform.handler
    async def handler(request):
        if request.url.path.endswith("/search"):
            platform.calls.append(request)
            return httpx.Response(status, headers={"content-type":"application/json",**headers},stream=httpx.ByteStream(raw))
        return await original(request)
    platform.handler = handler
    with running(monkeypatch,platform) as (browser,_,_):
        response = ask(browser)
    assert events(response)[-1][1]["code"] == "knowledge_unavailable"
    assert len(platform.calls) == 2 and "private" not in response.text and "secret" not in response.text
