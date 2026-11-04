"""Bounded service-side client for the platform's active-call model delegation."""

from __future__ import annotations

import asyncio
from contextlib import asynccontextmanager
from dataclasses import dataclass
import ipaddress
import re
from urllib.parse import urlsplit

from fastapi import HTTPException, Request
import httpx

from sample_backend.authentication import JSON_UTF8, REQUEST_ID, strict_json

MODEL_ID = re.compile(r"[a-z][a-z0-9._-]{0,63}")
CAPABILITY = re.compile(r"[a-f0-9]{64}")
SSE_UTF8 = re.compile(r'text/event-stream(?:\s*;\s*charset=(?:utf-8|"utf-8"))?', re.IGNORECASE)
MAX_JSON = 1024 * 1024
MAX_LINE = 65536
MAX_EVENT = 262144


@dataclass(frozen=True)
class ModelConfiguration:
    origin: str
    model_id: str
    max_output_tokens: int


def validate_model_configuration(origin: str | None, model_id: str | None, maximum: str, development: bool) -> ModelConfiguration | None:
    if origin is None and model_id is None:
        return None
    try:
        if not isinstance(origin, str) or not isinstance(model_id, str):
            raise ValueError()
        parsed = urlsplit(origin)
        loopback = parsed.hostname == "localhost"
        if parsed.hostname:
            try:
                loopback = loopback or ipaddress.ip_address(parsed.hostname).is_loopback
            except ValueError:
                pass
        if (len(origin) > 2048 or any(c.isspace() or ord(c) < 32 for c in origin) or "\\" in origin
                or not parsed.hostname or parsed.username is not None or parsed.password is not None
                or parsed.path not in ("", "/") or "?" in origin or "#" in origin or parsed.port == 0
                or not (parsed.scheme == "https" or development and parsed.scheme == "http" and loopback)
                or MODEL_ID.fullmatch(model_id) is None or not maximum.isascii() or not maximum.isdecimal()
                or not 1 <= int(maximum) <= 32768):
            raise ValueError()
        return ModelConfiguration(origin.rstrip("/"), model_id, int(maximum))
    except ValueError:
        raise ValueError("Invalid model configuration; use a fixed HTTPS origin and model alias (Development permits loopback HTTP)") from None


def read_delegation(request: Request) -> str:
    values = request.headers.getlist("x-model-delegation")
    if len(values) != 1 or CAPABILITY.fullmatch(values[0]) is None:
        raise HTTPException(403, "model_delegation_required")
    return values[0]


async def read_model_message(request: Request) -> tuple[str, bool]:
    types = request.headers.getlist("content-type")
    if (request.url.query or len(types) != 1 or JSON_UTF8.fullmatch(types[0]) is None
            or request.headers.getlist("content-encoding")):
        raise HTTPException(400, "invalid_model_message")
    body = bytearray()
    async with asyncio.timeout(3):
        async for chunk in request.stream():
            if len(body) + len(chunk) > 4096:
                raise HTTPException(413, "model_message_too_large")
            body.extend(chunk)
    try:
        value = strict_json(bytes(body))
        if (not isinstance(value, dict) or not {"message"} <= value.keys() <= {"message", "stream"}
                or not isinstance(value["message"], str) or not value["message"].strip()
                or type(value.get("stream", False)) is not bool):
            raise ValueError()
        value["message"].encode("utf-8")
        return value["message"], value.get("stream", False)
    except (ValueError, TypeError, RecursionError):
        raise HTTPException(400, "invalid_model_message") from None


async def while_connected(request: Request, operation):
    """Cancel a pending JSON call when the incoming client has disconnected.

    Invoke after consuming the request body; otherwise disconnect polling could
    consume ASGI body messages. StreamingResponse owns cancellation for SSE.
    """
    async def disconnected():
        while not await request.is_disconnected():
            await asyncio.sleep(0.05)

    work = asyncio.create_task(operation)
    watch = asyncio.create_task(disconnected())
    try:
        done, _pending = await asyncio.wait((work, watch), return_when=asyncio.FIRST_COMPLETED)
        if watch in done:
            raise asyncio.CancelledError()
        return await work
    finally:
        work.cancel()
        watch.cancel()
        await asyncio.gather(work, watch, return_exceptions=True)


def _usage(value):
    if value is None:
        return
    if (not isinstance(value, dict) or value.keys() != {"promptTokens", "completionTokens", "totalTokens"}
            or any(type(count) is not int or not 0 <= count <= 9223372036854775807 for count in value.values())):
        raise ValueError()


def _result(value, kind=None, expected_id=None):
    if not isinstance(value, dict) or value.keys() != {"content", "finishReason", "usage", "requestId"}:
        raise ValueError()
    request_id = value["requestId"]
    if (not isinstance(request_id, str) or REQUEST_ID.fullmatch(request_id) is None
            or expected_id is not None and request_id != expected_id):
        raise ValueError()
    finish = value["finishReason"]
    if finish is not None and (not isinstance(finish, str) or len(finish) > 128 or any(ord(c) < 32 for c in finish)):
        raise ValueError()
    if finish is not None:
        finish.encode("utf-8")
    content = value["content"]
    if kind in (None, "delta"):
        if not isinstance(content, str):
            raise ValueError()
        content.encode("utf-8")
    elif content is not None:
        raise ValueError()
    _usage(value["usage"])
    if kind == "usage" and value["usage"] is None:
        raise ValueError()
    return value


class ModelClient:
    """No retries or browser identity forwarding; the caller owns client lifetime."""

    def __init__(self, client: httpx.AsyncClient, configuration: ModelConfiguration | None, total_seconds: float = 120):
        self.client = client
        self.configuration = configuration
        self.total_seconds = total_seconds

    @asynccontextmanager
    async def _response(self, capability: str, message: str, stream: bool):
        if self.configuration is None:
            raise HTTPException(503, "model_unavailable")
        if CAPABILITY.fullmatch(capability) is None:
            raise HTTPException(403, "model_delegation_required")
        configuration = self.configuration
        # A fresh Request bypasses any client cookie jar and default headers.
        request = httpx.Request("POST", f"{configuration.origin}/internal/models/{configuration.model_id}/chat",
                                headers={"Authorization": "Bearer " + capability, "Accept-Encoding": "identity",
                                         "Accept": "text/event-stream" if stream else "application/json"},
                                json={"messages": [{"role": "user", "content": message}], "stream": stream,
                                      "maxOutputTokens": configuration.max_output_tokens})
        try:
            async with asyncio.timeout(self.total_seconds):
                response = await self.client.send(request, stream=True, follow_redirects=False, auth=None)
                try:
                    if response.status_code in (401, 403):
                        raise HTTPException(403, "model_access_denied")
                    if response.status_code == 429:
                        raise HTTPException(429, "model_rate_limited")
                    media = SSE_UTF8 if stream else JSON_UTF8
                    types = response.headers.get_list("content-type")
                    if (response.status_code != 200 or len(types) != 1 or media.fullmatch(types[0]) is None
                            or response.headers.get_list("content-encoding")):
                        raise ValueError()
                    yield response
                finally:
                    await response.aclose()
        except (httpx.HTTPError, TimeoutError, ValueError, TypeError, RecursionError):
            raise HTTPException(502, "model_response_unavailable") from None

    async def complete(self, capability: str, message: str) -> dict:
        async with self._response(capability, message, False) as response:
            body = bytearray()
            async for chunk in response.aiter_raw():
                if len(body) + len(chunk) > MAX_JSON:
                    raise ValueError()
                body.extend(chunk)
            return _result(strict_json(bytes(body)))

    async def events(self, capability: str, message: str):
        async with self._response(capability, message, True) as response:
            line = bytearray()
            event_bytes = 0
            kind = None
            data = []
            request_id = None
            async for chunk in response.aiter_raw():
                for byte in chunk:
                    event_bytes += 1
                    if event_bytes > MAX_EVENT or len(line) + 1 > MAX_LINE:
                        raise ValueError()
                    if byte != 10:
                        line.append(byte)
                        continue
                    text = bytes(line).removesuffix(b"\r").decode("utf-8")
                    line.clear()
                    if text:
                        if text.startswith(":"):
                            continue
                        field, separator, value = text.partition(":")
                        if not separator:
                            raise ValueError()
                        value = value.removeprefix(" ")
                        if field == "event" and kind is None:
                            kind = value
                        elif field == "data":
                            data.append(value)
                        else:
                            raise ValueError()
                        continue
                    if data:
                        if kind not in ("delta", "usage", "complete"):
                            raise ValueError()
                        value = _result(strict_json("\n".join(data).encode("utf-8")), kind, request_id)
                        request_id = value["requestId"]
                        if asyncio.current_task().cancelling():
                            raise asyncio.CancelledError()
                        yield kind, value
                        if kind == "complete":
                            return
                    elif kind is not None:
                        raise ValueError()
                    kind = None
                    data = []
                    event_bytes = 0
            # Even syntactically complete deltas are not a successful model call.
            raise ValueError()
