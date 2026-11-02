"""Health endpoints and bounded SSE probe; business APIs are not implemented here."""

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

from fastapi import FastAPI, Query, Request
from fastapi.responses import JSONResponse, StreamingResponse

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


def create_app(environment: str | None = None) -> FastAPI:
    environment = environment or os.getenv("ASSEMBLEAI_ENVIRONMENT", "Production")
    development = environment == "Development"

    @asynccontextmanager
    async def lifespan(app: FastAPI):
        app.state.ready = True
        try:
            yield
        finally:
            app.state.ready = False

    app = FastAPI(
        title="AssembleAI sample probe",
        version="0.1.0",
        lifespan=lifespan,
        docs_url="/docs" if development else None,
        redoc_url=None,
        openapi_url="/openapi.json" if development else None,
    )
    app.state.ready = False
    app.state.streams = StreamState()

    @app.get("/health/live")
    async def live() -> dict:
        return {"status": "live", "service": "sample", "version": "0.1.0"}

    @app.get("/health/ready")
    async def ready() -> JSONResponse:
        # This probe has no external dependencies; it does not assert platform readiness.
        is_ready = app.state.ready
        return JSONResponse(
            {"status": "ready" if is_ready else "not_ready", "service": "sample", "dependencies": []},
            status_code=200 if is_ready else 503,
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
