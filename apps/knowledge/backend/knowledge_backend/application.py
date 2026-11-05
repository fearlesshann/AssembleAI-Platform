"""Public knowledge application: active delegated retrieval and verified citations."""
import asyncio
from contextlib import asynccontextmanager
from dataclasses import dataclass
import json
import os
import time

import anyio
from fastapi import FastAPI, HTTPException, Request
from fastapi.responses import JSONResponse, StreamingResponse
import httpx

from .authentication import JSON_UTF8, SLUG, VERSION, RemoteValidator, read_credentials, strict_json, validate_configuration
from .knowledge import KnowledgeClient, REFUSAL, answer_result, guard_for, make_prompt, read_knowledge_delegation, reference_guard, valid_text, validate_knowledge_origin
from .models import ModelClient, read_delegation, validate_model_configuration, while_connected

HEADERS = {"Cache-Control": "no-store", "X-Content-Type-Options": "nosniff"}


@dataclass
class StreamState:
    active: int = 0
    completed: int = 0
    failed: int = 0
    cancelled: int = 0


def sse_event(kind, value):
    result = f"event: {kind}\ndata: {json.dumps(value, ensure_ascii=False, separators=(',', ':'))}\n\n"
    if len(result.encode("utf-8")) > 65536:
        raise HTTPException(502, "invalid_knowledge_answer")
    return result


async def read_body(request, references=False):
    types = request.headers.getlist("content-type")
    if request.url.query or len(types) != 1 or JSON_UTF8.fullmatch(types[0]) is None or request.headers.getlist("content-encoding"):
        raise HTTPException(400, "invalid_knowledge_request")
    body = bytearray()
    async with asyncio.timeout(3):
        async for chunk in request.stream():
            if len(body) + len(chunk) > 8192:
                raise HTTPException(413, "knowledge_request_too_large")
            body.extend(chunk)
    try:
        value = strict_json(bytes(body))
        if references:
            return reference_guard(value)
        if not isinstance(value, dict) or value.keys() != {"question"} or not valid_text(value["question"], 1000):
            raise ValueError()
        return value
    except (ValueError, TypeError, RecursionError):
        raise HTTPException(400, "invalid_knowledge_request") from None


def error_code(error):
    if isinstance(error, HTTPException):
        if error.status_code in (401, 403):
            return "authorization_revoked"
        if error.detail == "invalid_knowledge_answer":
            return error.detail
        if isinstance(error.detail, str) and error.detail.startswith("model_"):
            return "model_unavailable"
        if isinstance(error.detail, str) and error.detail.startswith("knowledge_"):
            return "knowledge_unavailable"
    return "knowledge_timeout" if isinstance(error, TimeoutError) else "knowledge_failed"


async def knowledge_events(app, question, model_cap, knowledge_cap, request_id, seconds=180, request=None):
    state = app.state.streams
    state.active += 1
    outcome = "cancelled"
    queue = asyncio.Queue(maxsize=1)
    failed = asyncio.get_running_loop().create_future()
    guard = {}

    async def pipeline():
        try:
            result = await app.state.knowledge.search(knowledge_cap, question)
            guard.update(guard_for(result))
            await app.state.knowledge.check(knowledge_cap, guard)
            model_request_id = None
            if result["hits"]:
                await queue.put(("delta", {"kind": "stage", "stage": "answering"}))
                model = await app.state.models.complete(model_cap, make_prompt(question, result["hits"]))
                if model["finishReason"] != "stop":
                    raise HTTPException(422, "invalid_knowledge_answer")
                answer = answer_result(model["content"], result["hits"])
                model_request_id = model["requestId"]
            else:
                answer = {"answer": REFUSAL, "refusal": True, "citations": []}
            await queue.put(("delta", {"kind": "stage", "stage": "verifying"}))
            await queue.put(("complete", {"requestId": request_id, "modelRequestId": model_request_id,
                "searchRequestId": result["requestId"], **answer, "profile": result["profile"],
                "authorizationVersion": str(result["authorizationVersion"]), "documents": guard["documents"]}))
        except asyncio.CancelledError:
            raise
        except Exception as error:
            if not failed.done():
                failed.set_result(error)

    async def watch_disconnect():
        while True:
            if await request.is_disconnected():
                if not failed.done():
                    failed.set_result(None)
                return
            await asyncio.sleep(0.05)

    producer = waiting = disconnected = checking = None
    try:
        async with asyncio.timeout(seconds):
            yield sse_event("delta", {"kind": "stage", "stage": "retrieving"})
            producer = asyncio.create_task(pipeline())
            if request is not None:
                disconnected = asyncio.create_task(watch_disconnect())
            while True:
                waiting = asyncio.create_task(queue.get())
                done, _ = await asyncio.wait((waiting, failed), timeout=10, return_when=asyncio.FIRST_COMPLETED)
                if failed in done:
                    if failed.result() is None:
                        return
                    raise failed.result()
                if waiting not in done:
                    waiting.cancel()
                    await asyncio.gather(waiting, return_exceptions=True)
                    yield ": heartbeat\n\n"
                    continue
                kind, value = waiting.result()
                if kind == "complete":
                    # Serialize authorization checks; there is never a competing
                    # background check occupying a second platform call slot.
                    checking = asyncio.create_task(app.state.knowledge.check(knowledge_cap, guard))
                    done, _ = await asyncio.wait((checking, failed), return_when=asyncio.FIRST_COMPLETED)
                    if failed in done:
                        if failed.result() is None:
                            return
                        raise failed.result()
                    await checking
                yield sse_event(kind, value)
                if kind == "complete":
                    outcome = "completed"
                    return
    except asyncio.CancelledError:
        raise
    except Exception as error:
        outcome = "failed"
        yield sse_event("error", {"code": error_code(error), "requestId": request_id})
    finally:
        tasks = [task for task in (producer, waiting, disconnected, checking) if task is not None]
        for task in tasks:
            task.cancel()
        try:
            with anyio.CancelScope(shield=True):
                async with asyncio.timeout(3):
                    await asyncio.gather(*tasks, return_exceptions=True)
        except TimeoutError:
            pass
        finally:
            failed.cancel()
            state.active -= 1
            setattr(state, outcome, getattr(state, outcome) + 1)


def create_app(environment=None):
    development = (environment or os.getenv("ASSEMBLEAI_ENVIRONMENT", "Production")) == "Development"
    validation_url = validate_configuration(os.getenv("ASSEMBLEAI_VALIDATION_URL"), development)
    model_origin = os.getenv("ASSEMBLEAI_MODEL_GATEWAY_ORIGIN")
    configuration = validate_model_configuration(model_origin, os.getenv("ASSEMBLEAI_MODEL_ID"),
                                                os.getenv("ASSEMBLEAI_MODEL_MAX_OUTPUT_TOKENS", "2048"), development)
    knowledge_origin = validate_knowledge_origin(os.getenv("ASSEMBLEAI_KNOWLEDGE_GATEWAY_ORIGIN", model_origin), development)
    service = os.getenv("ASSEMBLEAI_SERVICE", "knowledge")
    version = os.getenv("ASSEMBLEAI_SERVICE_VERSION", "0.1.0")
    if SLUG.fullmatch(service) is None or len(version) > 64 or VERSION.fullmatch(version) is None:
        raise ValueError("Invalid configured backend service or version")

    @asynccontextmanager
    async def lifespan(app):
        async with httpx.AsyncClient(timeout=httpx.Timeout(3), trust_env=False, follow_redirects=False,
                limits=httpx.Limits(max_connections=50, max_keepalive_connections=10)) as validator_http, \
            httpx.AsyncClient(timeout=httpx.Timeout(120, connect=3), trust_env=False, follow_redirects=False,
                limits=httpx.Limits(max_connections=32, max_keepalive_connections=8)) as model_http, \
            httpx.AsyncClient(timeout=httpx.Timeout(35, connect=3), trust_env=False, follow_redirects=False,
                limits=httpx.Limits(max_connections=32, max_keepalive_connections=8)) as knowledge_http:
            app.state.validator = RemoteValidator(validator_http, validation_url, service, version)
            app.state.models = ModelClient(model_http, configuration)
            app.state.knowledge = KnowledgeClient(knowledge_http, knowledge_origin)
            app.state.ready = True
            try:
                yield
            finally:
                app.state.ready = False

    app = FastAPI(title="AssembleAI Knowledge", version=version, lifespan=lifespan,
                  docs_url=None, redoc_url=None, openapi_url=None)
    app.state.ready = False
    app.state.streams = StreamState()

    @app.exception_handler(HTTPException)
    async def safe_http(_request, error):
        return JSONResponse({"detail": error.detail}, status_code=error.status_code, headers=HEADERS)

    @app.get("/health/live")
    async def live():
        return {"status": "live", "service": service, "version": version}

    @app.get("/health/ready")
    async def ready():
        return JSONResponse({"status": "healthy" if app.state.ready else "not_ready", "service": service,
                             "version": version, "protocolVersion": 1}, status_code=200 if app.state.ready else 503)

    @app.post("/ask")
    async def ask(request: Request):
        started = time.monotonic()
        credentials = read_credentials(request)
        await app.state.validator.validate(credentials, request.method, request.url.path)
        model_cap = read_delegation(request)
        knowledge_cap = read_knowledge_delegation(request)
        try:
            body = await read_body(request)
        except TimeoutError:
            raise HTTPException(408, "knowledge_request_timeout") from None
        return StreamingResponse(knowledge_events(app, body["question"], model_cap, knowledge_cap,
            credentials.request_id, max(0, 180 - (time.monotonic() - started)), request), media_type="text/event-stream",
            headers={**HEADERS, "X-Request-ID": credentials.request_id, "X-Accel-Buffering": "no"})

    @app.post("/references")
    async def references(request: Request):
        credentials = read_credentials(request)
        await app.state.validator.validate(credentials, request.method, request.url.path)
        knowledge_cap = read_knowledge_delegation(request)
        try:
            guard = await read_body(request, True)
            await while_connected(request, app.state.knowledge.check(knowledge_cap, guard))
        except TimeoutError:
            raise HTTPException(408, "knowledge_request_timeout") from None
        return JSONResponse({"valid": True}, headers={**HEADERS, "X-Request-ID": credentials.request_id})

    return app


app = create_app()
