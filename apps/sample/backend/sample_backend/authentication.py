"""Fail-closed remote verification of platform-issued internal credentials.

Only the platform validates JWT signatures and current authorization. This
consumer never decodes a token into identity or accepts identity headers.
"""

from __future__ import annotations

import asyncio
from dataclasses import dataclass, field
import ipaddress
import json
import re
from urllib.parse import urlsplit
from uuid import UUID

from fastapi import HTTPException, Request
import httpx


SLUG = re.compile(r"[a-z][a-z0-9-]{0,63}")
VERSION = re.compile(r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)")
REQUEST_ID = re.compile(r"[a-f0-9]{32}")
BEARER = re.compile(r"Bearer [A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+")
JSON_UTF8 = re.compile(r'application/json(?:\s*;\s*charset=(?:utf-8|"utf-8"))?', re.IGNORECASE)
MAX_BODY = 4096


@dataclass(frozen=True)
class InternalCredentials:
    authorization: str = field(repr=False)
    request_id: str


def read_credentials(request: Request) -> InternalCredentials:
    authorization = request.headers.getlist("authorization")
    if (len(authorization) != 1 or len(authorization[0]) > 16391
            or BEARER.fullmatch(authorization[0]) is None):
        raise HTTPException(401, "internal_credentials_required")
    request_ids = request.headers.getlist("x-request-id")
    if len(request_ids) != 1 or REQUEST_ID.fullmatch(request_ids[0]) is None:
        raise HTTPException(400, "invalid_request_id")
    return InternalCredentials(authorization[0], request_ids[0])


def validate_configuration(url: str | None, development: bool) -> str | None:
    if not url:
        return None
    try:
        parsed = urlsplit(url)
        valid = (len(url) <= 2048 and not any(character.isspace() or ord(character) < 32 for character in url)
                 and "\\" not in url and parsed.hostname and parsed.username is None
                 and parsed.password is None and not parsed.query and not parsed.fragment
                 and parsed.path == "/internal/calls/validate" and parsed.port != 0)
        loopback = parsed.hostname == "localhost"
        if parsed.hostname:
            try:
                loopback = loopback or ipaddress.ip_address(parsed.hostname).is_loopback
            except ValueError:
                pass
        valid = valid and (parsed.scheme == "https" or (development and parsed.scheme == "http" and loopback))
        if not valid:
            raise ValueError()
    except ValueError:
        # Configuration may contain credentials: never echo it in exceptions.
        raise ValueError("ASSEMBLEAI_VALIDATION_URL must be a fixed HTTPS internal validation endpoint; Development permits loopback HTTP") from None
    return url


def strict_json(body: bytes):
    def unique_object(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError("Duplicate JSON member")
            result[key] = value
        return result

    def invalid_constant(_value):
        raise ValueError("Non-JSON number")

    return json.loads(body.decode("utf-8"), object_pairs_hook=unique_object, parse_constant=invalid_constant)


async def read_message(request: Request) -> str:
    content_types = request.headers.getlist("content-type")
    if (len(content_types) != 1 or JSON_UTF8.fullmatch(content_types[0]) is None
            or request.headers.get("content-encoding", "identity").lower() != "identity"):
        raise HTTPException(415, "utf8_json_required")
    body = bytearray()
    async for chunk in request.stream():
        body.extend(chunk)
        if len(body) > MAX_BODY:
            raise HTTPException(413, "message_too_large")
    try:
        payload = strict_json(bytes(body))
        if (not isinstance(payload, dict) or payload.keys() != {"message"}
                or not isinstance(payload["message"], str) or not 1 <= len(payload["message"]) <= 200):
            raise ValueError()
        # JSON can express isolated surrogates; the response must remain UTF-8.
        payload["message"].encode("utf-8")
        return payload["message"]
    except (ValueError, TypeError, RecursionError):
        raise HTTPException(400, "invalid_message") from None


class RemoteValidator:
    def __init__(self, client: httpx.AsyncClient, url: str | None, service: str, version: str):
        self.client = client
        self.url = url
        self.service = service
        self.version = version

    async def validate(self, credentials: InternalCredentials, method: str, path: str) -> dict:
        if self.url is None:
            raise HTTPException(503, "validation_unavailable")
        request = self.client.build_request("POST", self.url, headers={
            "Authorization": credentials.authorization,
            "X-Request-ID": credentials.request_id,
            "Accept": "application/json",
            "Accept-Encoding": "identity",
        }, json={"service": self.service, "version": self.version, "method": method, "path": path})
        # Never turn this service-to-service call into a browser/cookie session.
        request.headers.pop("cookie", None)
        try:
            async with asyncio.timeout(3):
                response = await self.client.send(request, stream=True, follow_redirects=False)
                try:
                    if response.status_code in (401, 403):
                        raise HTTPException(response.status_code, "internal_credentials_rejected")
                    if (response.status_code != 200
                            or JSON_UTF8.fullmatch(response.headers.get("content-type", "")) is None
                            or response.headers.get("content-encoding", "identity").lower() != "identity"):
                        raise ValueError()
                    body = bytearray()
                    async for chunk in response.aiter_bytes():
                        body.extend(chunk)
                        if len(body) > MAX_BODY:
                            raise ValueError()
                finally:
                    await response.aclose()
            context = strict_json(bytes(body))
            self._validate_context(context, credentials.request_id)
            return context
        except (httpx.HTTPError, TimeoutError, ValueError, TypeError, RecursionError):
            # No token, body, URL or network exception is returned or logged.
            raise HTTPException(503, "validation_unavailable") from None

    @staticmethod
    def _validate_context(context, request_id: str) -> None:
        if not isinstance(context, dict) or context.keys() != {
            "userId", "tenantId", "authorizationVersion", "requestId", "appId", "releaseVersion"
        }:
            raise ValueError()
        for key in ("userId", "tenantId"):
            value = context[key]
            if not isinstance(value, str):
                raise ValueError()
            parsed = UUID(value)
            if parsed.int == 0 or str(parsed) != value:
                raise ValueError()
        if (type(context["authorizationVersion"]) is not int or not 1 <= context["authorizationVersion"] <= 9223372036854775807
                or context["requestId"] != request_id
                or not isinstance(context["appId"], str) or SLUG.fullmatch(context["appId"]) is None
                or not isinstance(context["releaseVersion"], str) or len(context["releaseVersion"]) > 64
                or VERSION.fullmatch(context["releaseVersion"]) is None):
            raise ValueError()
