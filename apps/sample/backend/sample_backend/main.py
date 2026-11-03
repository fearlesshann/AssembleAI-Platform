"""Independent sample backend with platform-verified probe and bounded SSE."""

from __future__ import annotations

import asyncio
from collections.abc import AsyncIterator
from contextlib import asynccontextmanager
from dataclasses import dataclass
import json
import logging
import os
from typing import Annotated
from uuid import uuid4

from fastapi import FastAPI, HTTPException, Query, Request
from fastapi.responses import JSONResponse, StreamingResponse
import httpx

from sample_backend.authentication import (
    InternalCredentials, RemoteValidator, SLUG, VERSION, read_credentials,
    read_message, validate_configuration,
)

logger = logging.getLogger(__name__)


@dataclass
class StreamState:
    active: int = 0
    completed: int = 0
    cancelled: int = 0
    failed: int = 0


def sse_event(event: str, payload: dict) -> str:
    return f"event: {event}\ndata: {json.dumps(payload, ensure_ascii=False)}\n\n"


async def sample_events(
    request: Request,
    count: int,
    delay_ms: int,
    request_id: str,
    state: StreamState,
) -> AsyncIterator[str]:
    """Send the first delta immediately and release per-stream state on every exit."""
    state.active += 1
    outcome = "cancelled"
    try:
        for index in range(count):
            if await request.is_disconnected():
                return
            yield sse_event(
                "delta",
                {"index": index, "text": f"连接验证 {index + 1}/{count}", "requestId": request_id},
            )
            if index + 1 < count:
                await asyncio.sleep(delay_ms / 1000)
        yield sse_event("complete", {"count": count, "requestId": request_id})
        outcome = "completed"
    except asyncio.CancelledError:
        raise
    except Exception:
        outcome = "failed"
        logger.exception("Sample stream failed: request_id=%s", request_id)
        yield sse_event("error", {"code": "sample_stream_failed", "requestId": request_id})
    finally:
        state.active -= 1
        setattr(state, outcome, getattr(state, outcome) + 1)


async def protected_events(
    request: Request,
    count: int,
    delay_ms: int,
    credentials: InternalCredentials,
    context: dict,
    state: StreamState,
    validator: RemoteValidator,
) -> AsyncIterator[str]:
    """Bounded (<20s) demonstration: recheck the original 30-second credential.

    The stream does not extend a JWT or cache an authorization decision. Awaited
    HTTP checks are cancelled with the stream when its client disconnects.
    """
    state.active += 1
    outcome = "cancelled"
    loop = asyncio.get_running_loop()
    last_check = loop.time()
    try:
        for index in range(count):
            if await request.is_disconnected():
                return
            if loop.time() - last_check >= 2:
                fresh = await validator.validate(credentials, request.method, request.url.path)
                if fresh != context:
                    raise HTTPException(403, "internal_credentials_rejected")
                last_check = loop.time()
            yield sse_event("delta", {
                "index": index, "text": f"受保护调用 {index + 1}/{count}", "requestId": credentials.request_id,
            })
            if index + 1 < count:
                await asyncio.sleep(delay_ms / 1000)
        yield sse_event("complete", {"count": count, "requestId": credentials.request_id})
        outcome = "completed"
    except asyncio.CancelledError:
        raise
    except HTTPException as error:
        outcome = "failed"
        code = "authorization_revoked" if error.status_code in (401, 403) else "validation_unavailable"
        yield sse_event("error", {"code": code, "requestId": credentials.request_id})
    except Exception:
        outcome = "failed"
        logger.error("Protected stream failed: request_id=%s", credentials.request_id)
        yield sse_event("error", {"code": "sample_stream_failed", "requestId": credentials.request_id})
    finally:
        state.active -= 1
        setattr(state, outcome, getattr(state, outcome) + 1)


def create_app(environment: str | None = None) -> FastAPI:
    environment = environment or os.getenv("ASSEMBLEAI_ENVIRONMENT", "Production")
    development = environment == "Development"
    validation_url = validate_configuration(os.getenv("ASSEMBLEAI_VALIDATION_URL"), development)
    service = os.getenv("ASSEMBLEAI_SERVICE", "sample")
    version = os.getenv("ASSEMBLEAI_SERVICE_VERSION", "0.1.0")
    if SLUG.fullmatch(service) is None or len(version) > 64 or VERSION.fullmatch(version) is None:
        raise ValueError("Invalid configured backend service or version")

    @asynccontextmanager
    async def lifespan(app: FastAPI):
        async with httpx.AsyncClient(
            timeout=httpx.Timeout(3), trust_env=False, follow_redirects=False,
            limits=httpx.Limits(max_connections=50, max_keepalive_connections=10),
        ) as client:
            app.state.validator = RemoteValidator(client, validation_url, service, version)
            app.state.ready = True
            try:
                yield
            finally:
                app.state.ready = False

    app = FastAPI(
        title="AssembleAI sample probe",
        version=version,
        lifespan=lifespan,
        docs_url="/docs" if development else None,
        redoc_url=None,
        openapi_url="/openapi.json" if development else None,
    )
    app.state.ready = False
    app.state.streams = StreamState()

    @app.get("/health/live")
    async def live() -> dict:
        return {"status": "live", "service": service, "version": version}

    @app.get("/health/ready")
    async def ready() -> JSONResponse:
        # Process readiness only: protected operations still fail closed when
        # the separately configured platform verifier is absent/unavailable.
        is_ready = app.state.ready
        return JSONResponse(
            {"status": "healthy" if is_ready else "not_ready", "service": service,
             "version": version, "protocolVersion": 1},
            status_code=200 if is_ready else 503,
        )

    @app.post("/probe")
    async def probe(request: Request) -> JSONResponse:
        credentials = read_credentials(request)
        context = await app.state.validator.validate(credentials, request.method, request.url.path)
        message = await read_message(request)
        return JSONResponse({"status": "verified", "message": message, **context}, headers={
            "Cache-Control": "no-store", "X-Request-ID": credentials.request_id,
        })

    @app.get("/events")
    async def events(
        request: Request,
        count: Annotated[int, Query(ge=1, le=20)] = 5,
        delay_ms: Annotated[int, Query(ge=10, le=1000)] = 150,
    ) -> StreamingResponse:
        credentials = read_credentials(request)
        context = await app.state.validator.validate(credentials, request.method, request.url.path)
        return StreamingResponse(
            protected_events(request, count, delay_ms, credentials, context, app.state.streams, app.state.validator),
            media_type="text/event-stream",
            headers={"Cache-Control": "no-store", "X-Accel-Buffering": "no", "X-Request-ID": credentials.request_id},
        )

    if development:

        @app.get("/stream")
        async def stream(
            request: Request,
            count: Annotated[int, Query(ge=1, le=20)] = 5,
            delay_ms: Annotated[int, Query(ge=10, le=1000)] = 150,
        ) -> StreamingResponse:
            request_id = str(uuid4())
            return StreamingResponse(
                sample_events(request, count, delay_ms, request_id, app.state.streams),
                media_type="text/event-stream",
                headers={
                    "Cache-Control": "no-cache, no-transform",
                    "X-Accel-Buffering": "no",
                    "X-Request-Id": request_id,
                },
            )

    return app


app = create_app()
