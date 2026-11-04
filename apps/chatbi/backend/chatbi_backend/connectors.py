"""Bounded connector callbacks using only the active data capability."""

import asyncio
from decimal import Decimal

from fastapi import HTTPException, Request
import httpx

from chatbi_backend.authentication import JSON_UTF8, REQUEST_ID
from chatbi_backend.models import CAPABILITY, validate_model_configuration
from chatbi_backend.planning import IDENTIFIER, decimal_text, exact_json, text, valid_cell, expected_columns

MAX_RESPONSE = 1024 * 1024 + 16384


def validate_connector_origin(origin: str | None, development: bool) -> str | None:
    if origin is None:
        return None
    return validate_model_configuration(origin, "connector", "1", development).origin


def read_connector_delegation(request: Request) -> str:
    values = request.headers.getlist("x-connector-delegation")
    if len(values) != 1 or CAPABILITY.fullmatch(values[0]) is None:
        raise HTTPException(403, "connector_delegation_required")
    return values[0]


def metadata(value):
    if not isinstance(value, dict) or value.keys() != {"items"} or not isinstance(value["items"], list) or len(value["items"]) > 100:
        raise ValueError()
    seen = set()
    for item in value["items"]:
        if (not isinstance(item, dict) or item.keys() != {"sourceId", "datasetId", "displayName", "columns", "maxRows"}
                or any(not isinstance(item[key], str) or IDENTIFIER.fullmatch(item[key]) is None for key in ("sourceId", "datasetId"))
                or item["datasetId"] in seen or not text(item["displayName"], 800) or not item["displayName"].strip()
                or len(item["displayName"].encode("utf-16-le")) > 400
                or any(ord(character) < 32 or 127 <= ord(character) <= 159 for character in item["displayName"])
                or type(item["maxRows"]) is not int or not 1 <= item["maxRows"] <= 500
                or not isinstance(item["columns"], list) or not 1 <= len(item["columns"]) <= 32):
            raise ValueError()
        seen.add(item["datasetId"])
        names = set()
        for column in item["columns"]:
            if (not isinstance(column, dict) or column.keys() != {"name", "type"}
                    or not isinstance(column["name"], str) or IDENTIFIER.fullmatch(column["name"]) is None
                    or column["name"] in names or column["type"] not in ("text", "integer", "decimal", "date")):
                raise ValueError()
            names.add(column["name"])
    return value


def query_result(value, dataset, plan):
    if (not isinstance(value, dict) or value.keys() != {"requestId", "columns", "rows", "authorizationVersion", "configurationRevision"}
            or not isinstance(value["requestId"], str) or REQUEST_ID.fullmatch(value["requestId"]) is None
            or any(type(value[key]) is not int or not 1 <= value[key] <= 9223372036854775807 for key in ("authorizationVersion", "configurationRevision"))
            or value["columns"] != expected_columns(plan) or not isinstance(value["rows"], list)
            or len(value["rows"]) > plan["limit"]):
        raise ValueError()
    types = {column["name"]: column["type"] for column in dataset["columns"]}
    result_types = [types[column] for column in plan["columns"]] + ["integer" if metric["function"] == "count" else "decimal" for metric in plan["metrics"]]
    rows = []
    for row in value["rows"]:
        if not isinstance(row, list) or len(row) != len(result_types):
            raise ValueError()
        converted = []
        for cell, kind in zip(row, result_types, strict=True):
            if cell is None:
                converted.append(None)
            elif not valid_cell(cell, kind):
                raise ValueError()
            elif isinstance(cell, Decimal):
                converted.append(decimal_text(cell))
            else:
                converted.append(str(cell))
        rows.append(converted)
    return {**value, "rows": rows}


class ConnectorClient:
    def __init__(self, client: httpx.AsyncClient, origin: str | None):
        self.client = client
        self.origin = origin

    async def _json(self, capability, method, path, body=None, seconds=35):
        if self.origin is None:
            raise HTTPException(503, "connector_unavailable")
        if not isinstance(capability, str) or CAPABILITY.fullmatch(capability) is None:
            raise HTTPException(403, "connector_delegation_required")
        headers = {"Authorization": "Bearer " + capability, "Accept": "application/json", "Accept-Encoding": "identity"}
        if body is not None:
            headers["Content-Type"] = "application/json; charset=utf-8"
        request = httpx.Request(method, self.origin + path, headers=headers, content=body)
        try:
            async with asyncio.timeout(seconds):
                response = await self.client.send(request, stream=True, follow_redirects=False, auth=None)
                try:
                    if response.status_code in (401, 403):
                        raise HTTPException(403, "authorization_revoked")
                    types = response.headers.get_list("content-type")
                    if (response.status_code != 200 or len(types) != 1 or JSON_UTF8.fullmatch(types[0]) is None
                            or response.headers.get_list("content-encoding")):
                        raise ValueError()
                    body = bytearray()
                    async for chunk in response.aiter_raw():
                        if len(body) + len(chunk) > MAX_RESPONSE:
                            raise ValueError()
                        body.extend(chunk)
                    return exact_json(bytes(body))
                finally:
                    await response.aclose()
        except (httpx.HTTPError, TimeoutError, ValueError, TypeError, RecursionError, OverflowError):
            raise HTTPException(502, "connector_response_unavailable") from None

    async def datasets(self, capability):
        try:
            # The platform allows three seconds of server work. Keep two more
            # seconds for transport without changing its database budget.
            return metadata(await self._json(capability, "GET", "/internal/connectors/datasets", seconds=5))
        except (ValueError, TypeError, RecursionError):
            raise HTTPException(502, "connector_response_unavailable") from None

    @staticmethod
    def _path(dataset):
        if any(not isinstance(dataset.get(key), str) or IDENTIFIER.fullmatch(dataset[key]) is None for key in ("sourceId", "datasetId")):
            raise HTTPException(502, "connector_response_unavailable")
        return f"/internal/connectors/{dataset['sourceId']}/datasets/{dataset['datasetId']}"

    async def query(self, capability, dataset, raw, plan):
        try:
            result = await self._json(capability, "POST", self._path(dataset) + "/query", raw.encode("utf-8"))
            return query_result(result, dataset, plan)
        except (ValueError, TypeError, KeyError, RecursionError, OverflowError):
            raise HTTPException(502, "connector_response_unavailable") from None

    async def check(self, capability, dataset, result):
        import json
        body = json.dumps({key: result[key] for key in ("authorizationVersion", "configurationRevision")}).encode()
        value = await self._json(capability, "POST", self._path(dataset) + "/check", body, seconds=5)
        if not isinstance(value, dict) or value.keys() != {"valid"} or value["valid"] is not True:
            raise HTTPException(403, "authorization_revoked")
