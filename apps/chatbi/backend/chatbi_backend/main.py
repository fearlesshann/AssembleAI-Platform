"""Independent ChatBI application: delegated planning, bounded data and guarded SSE."""

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

from chatbi_backend.authentication import JSON_UTF8, SLUG, VERSION, RemoteValidator, read_credentials, validate_configuration
from chatbi_backend.connectors import ConnectorClient, read_connector_delegation, validate_connector_origin
from chatbi_backend.models import ModelClient, read_delegation, validate_model_configuration, while_connected
from chatbi_backend.planning import IDENTIFIER, exact_json, make_prompt, parse_plan

MAX_EVENT = 32768
HEADERS = {"Cache-Control": "no-store", "X-Content-Type-Options": "nosniff"}


@dataclass
class StreamState:
    active: int = 0
    completed: int = 0
    failed: int = 0
    cancelled: int = 0


def sse_event(kind, value):
    result = f"event: {kind}\ndata: {json.dumps(value, ensure_ascii=False, separators=(',', ':'))}\n\n"
    if len(result.encode("utf-8")) > MAX_EVENT:
        raise HTTPException(502, "result_too_large")
    return result


def row_events(rows):
    batch = []
    for row in rows:
        candidate = batch + [row]
        try:
            sse_event("delta", {"kind": "rows", "rows": candidate})
        except HTTPException:
            if not batch:
                raise
            yield {"kind": "rows", "rows": batch}
            batch = [row]
            sse_event("delta", {"kind": "rows", "rows": batch})
        else:
            batch = candidate
    if batch:
        yield {"kind": "rows", "rows": batch}


async def read_ask(request):
    types = request.headers.getlist("content-type")
    if request.url.query or len(types) != 1 or JSON_UTF8.fullmatch(types[0]) is None or request.headers.getlist("content-encoding"):
        raise HTTPException(400, "invalid_chatbi_request")
    body = bytearray()
    async with asyncio.timeout(3):
        async for chunk in request.stream():
            if len(body) + len(chunk) > 8192:
                raise HTTPException(413, "chatbi_request_too_large")
            body.extend(chunk)
    try:
        value = exact_json(bytes(body))
        if (not isinstance(value, dict) or value.keys() != {"datasetId", "question"}
                or not isinstance(value["datasetId"], str) or IDENTIFIER.fullmatch(value["datasetId"]) is None
                or not isinstance(value["question"], str) or not 1 <= len(value["question"]) <= 1000
                or not value["question"].strip() or "\0" in value["question"]):
            raise ValueError()
        value["question"].encode("utf-8")
        return value
    except (ValueError, TypeError, RecursionError):
        raise HTTPException(400, "invalid_chatbi_request") from None


def error_code(error):
    if isinstance(error, HTTPException):
        if error.status_code in (401, 403):
            return "authorization_revoked"
        if error.detail in {"invalid_query_plan", "unsupported_question", "dataset_unavailable", "result_too_large"}:
            return error.detail
        if isinstance(error.detail, str) and error.detail.startswith("model_"):
            return "model_unavailable"
        if isinstance(error.detail, str) and error.detail.startswith("connector_"):
            return "connector_unavailable"
    return "chatbi_timeout" if isinstance(error, TimeoutError) else "chatbi_failed"


async def chatbi_events(app, body, model_cap, data_cap, request_id, seconds, request=None):
    """First frame is immediate; producer/guard HTTP is cancelled with the browser."""
    state = app.state.streams
    state.active += 1
    outcome = "cancelled"
    queue = asyncio.Queue(maxsize=1)
    failed = asyncio.get_running_loop().create_future()
    guard = {}

    async def pipeline():
        try:
            available = await app.state.connectors.datasets(data_cap)
            dataset = next((item for item in available["items"] if item["datasetId"] == body["datasetId"]), None)
            if dataset is None:
                raise HTTPException(404, "dataset_unavailable")
            model = await app.state.models.complete(model_cap, make_prompt(dataset, body["question"]))
            if model["finishReason"] != "stop":
                raise HTTPException(422, "invalid_query_plan")
            plan = parse_plan(model["content"], dataset)
            await queue.put(("delta", {"kind": "stage", "stage": "querying"}))
            result = await app.state.connectors.query(data_cap, dataset, model["content"], plan)
            await app.state.connectors.check(data_cap, dataset, result)
            guard.update(dataset=dataset, result=result, checked_at=time.monotonic())
            await queue.put(("delta", {"kind": "stage", "stage": "receiving"}))
            for rows in row_events(result["rows"]):
                await queue.put(("delta", rows))
            complete = {"requestId": request_id, "modelRequestId": model["requestId"], "queryRequestId": result["requestId"],
                        "sourceId": dataset["sourceId"], "datasetId": dataset["datasetId"], "datasetName": dataset["displayName"],
                        "columns": result["columns"], "rowCount": len(result["rows"]), "queryText": model["content"],
                        "scope": "当前授权工厂", "summary": f"查询完成，共 {len(result['rows'])} 行。"}
            await queue.put(("complete", complete))
        except asyncio.CancelledError:
            raise
        except Exception as error:
            if not failed.done():
                failed.set_result(error)

    async def watch_disconnect():
        # ASGI implementations differ in whether StreamingResponse detects a
        # close while no body is being sent. The request body is already read.
        while True:
            if await request.is_disconnected():
                if not failed.done():
                    failed.set_result(None)
                return
            await asyncio.sleep(0.05)

    producer = waiting = disconnected = checking = None

    async def check_delivery():
        nonlocal checking
        # A single consumer serializes delivery checks. Race against disconnect
        # explicitly because ASGI 2.4 need not cancel a response awaiting HTTP.
        checking = asyncio.create_task(app.state.connectors.check(data_cap, guard["dataset"], guard["result"]))
        done, _pending = await asyncio.wait((checking, failed), return_when=asyncio.FIRST_COMPLETED)
        if failed in done:
            if failed.result() is None:
                return False
            raise failed.result()
        await checking
        guard["checked_at"] = time.monotonic()
        return True

    try:
        async with asyncio.timeout(seconds):
            yield sse_event("delta", {"kind": "stage", "stage": "planning"})
            producer = asyncio.create_task(pipeline())
            if request is not None:
                disconnected = asyncio.create_task(watch_disconnect())
            while True:
                waiting = asyncio.create_task(queue.get())
                done, _pending = await asyncio.wait((waiting, failed), timeout=10, return_when=asyncio.FIRST_COMPLETED)
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
                if kind == "complete" or (kind == "delta" and value.get("kind") == "rows"
                                          and time.monotonic() - guard["checked_at"] >= 1):
                    if not await check_delivery():
                        return
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
        for task in (producer, waiting, disconnected, checking):
            if task is not None:
                task.cancel()
        try:
            # Starlette uses level cancellation. Shield the finite cleanup so a
            # repeated disconnect cancellation cannot skip closing child HTTP.
            with anyio.CancelScope(shield=True):
                async with asyncio.timeout(3):
                    await asyncio.gather(*(task for task in (producer, waiting, disconnected, checking)
                                           if task is not None), return_exceptions=True)
        except TimeoutError:
            pass
        finally:
            failed.cancel()
            state.active -= 1
            setattr(state, outcome, getattr(state, outcome) + 1)


def create_app(environment=None):
    environment = environment or os.getenv("ASSEMBLEAI_ENVIRONMENT", "Production")
    development = environment == "Development"
    validation_url = validate_configuration(os.getenv("ASSEMBLEAI_VALIDATION_URL"), development)
    model_origin = os.getenv("ASSEMBLEAI_MODEL_GATEWAY_ORIGIN")
    configuration = validate_model_configuration(model_origin, os.getenv("ASSEMBLEAI_MODEL_ID"),
        os.getenv("ASSEMBLEAI_MODEL_MAX_OUTPUT_TOKENS", "2048"), development)
    connector_origin = validate_connector_origin(os.getenv("ASSEMBLEAI_CONNECTOR_GATEWAY_ORIGIN", model_origin), development)
    service = os.getenv("ASSEMBLEAI_SERVICE", "chatbi")
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
                limits=httpx.Limits(max_connections=32, max_keepalive_connections=8)) as connector_http:
            app.state.validator = RemoteValidator(validator_http, validation_url, service, version)
            app.state.models = ModelClient(model_http, configuration)
            app.state.connectors = ConnectorClient(connector_http, connector_origin)
            app.state.ready = True
            try:
                yield
            finally:
                app.state.ready = False

    app = FastAPI(title="AssembleAI ChatBI", version=version, lifespan=lifespan,
                  docs_url="/docs" if development else None, redoc_url=None,
                  openapi_url="/openapi.json" if development else None)
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

    @app.get("/datasets")
    async def datasets(request: Request):
        credentials = read_credentials(request)
        await app.state.validator.validate(credentials, request.method, request.url.path)
        capability = read_connector_delegation(request)
        if request.url.query or request.headers.getlist("content-encoding"):
            raise HTTPException(400, "invalid_chatbi_request")
        try:
            async with asyncio.timeout(3):
                async for chunk in request.stream():
                    if chunk:
                        raise HTTPException(400, "invalid_chatbi_request")
            value = await while_connected(request, app.state.connectors.datasets(capability))
        except TimeoutError:
            raise HTTPException(408, "chatbi_request_timeout") from None
        return JSONResponse(value, headers={**HEADERS, "X-Request-ID": credentials.request_id})

    @app.post("/ask")
    async def ask(request: Request):
        started = time.monotonic()
        credentials = read_credentials(request)
        await app.state.validator.validate(credentials, request.method, request.url.path)
        model_cap = read_delegation(request)
        data_cap = read_connector_delegation(request)
        try:
            body = await read_ask(request)
        except TimeoutError:
            raise HTTPException(408, "chatbi_request_timeout") from None
        return StreamingResponse(chatbi_events(app, body, model_cap, data_cap, credentials.request_id,
                                 max(0, 150 - (time.monotonic() - started)), request), media_type="text/event-stream",
                                 headers={**HEADERS, "X-Request-ID": credentials.request_id, "X-Accel-Buffering": "no"})

    return app


app = create_app()
