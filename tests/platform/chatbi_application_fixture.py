"""Acceptance-only capability capture to a random authenticated loopback control endpoint."""
import os
import json
import time
import httpx
from chatbi_backend.main import app as application

for variable in os.environ.get("ASSEMBLEAI_CHATBI_FORBIDDEN_VARIABLES", "").split(","):
    if variable and variable in os.environ:
        raise RuntimeError("ChatBI child inherited a forbidden credential variable")


async def app(scope, receive, send):
    if scope["type"] == "http" and scope["path"] in ("/ask", "/datasets"):
        if not getattr(application.state, "planning_observer", False):
            application.state.planning_observer = True
            original = application.state.models.complete

            async def observe(capability, message):
                result = await original(capability, message)
                content = result["content"]
                summary = {"finishReason": result["finishReason"] if result["finishReason"] in ("stop", "length", None) else "other",
                           "characters": len(content), "markdownFence": content.lstrip().startswith("```"), "jsonObject": False}
                try:
                    value = json.loads(content)
                    summary["jsonObject"] = isinstance(value, dict)
                    if isinstance(value, dict):
                        expected = {"columns", "filters", "metrics", "limit"}
                        summary["requiredFields"] = len(expected.intersection(value))
                        summary["extraFields"] = len(value.keys() - expected)
                        summary["columnCount"] = len(value["columns"]) if isinstance(value.get("columns"), list) else -1
                        summary["metricCount"] = len(value["metrics"]) if isinstance(value.get("metrics"), list) else -1
                except (ValueError, TypeError):
                    pass
                async with httpx.AsyncClient(trust_env=False, follow_redirects=False, timeout=3) as client:
                    response = await client.post(os.environ["ASSEMBLEAI_CHATBI_CONTROL_ORIGIN"] + "/planning-diagnostic",
                        headers={"X-Fixture-Control": os.environ["ASSEMBLEAI_CHATBI_CONTROL_KEY"]}, json=summary)
                    response.raise_for_status()
                return result

            application.state.models.complete = observe
            application.state.connector_responses = []

            async def observe_request(request):
                item = {"operation": request.url.path.rsplit("/", 1)[-1], "status": None,
                        "contentType": None, "elapsedMs": None, "started": time.monotonic()}
                request.extensions["fixture_diagnostic"] = item
                application.state.connector_responses.append(item)

            async def observe_connector(response):
                # Observe transport metadata only; never read or retain a body.
                item = response.request.extensions["fixture_diagnostic"]
                item.update(status=response.status_code,
                    contentType=response.headers.get("content-type") == "application/json; charset=utf-8",
                    elapsedMs=round((time.monotonic() - item["started"]) * 1000))

            application.state.connectors.client.event_hooks["request"].append(observe_request)
            application.state.connectors.client.event_hooks["response"].append(observe_connector)
        if scope["path"] == "/ask":
            application.state.connector_responses = []
        headers = dict(scope["headers"])
        async with httpx.AsyncClient(trust_env=False, follow_redirects=False, timeout=3) as client:
            result = await client.post(os.environ["ASSEMBLEAI_CHATBI_CONTROL_ORIGIN"] + "/capture",
                headers={"X-Fixture-Control": os.environ["ASSEMBLEAI_CHATBI_CONTROL_KEY"]}, json={
                    "capability": headers.get(b"x-connector-delegation", b"").decode("ascii"),
                    "authorization": headers.get(b"authorization", b"").decode("ascii")})
            result.raise_for_status()
    async def observed_send(message):
        if (scope["type"] == "http" and scope["path"] == "/ask" and message["type"] == "http.response.body"
                and b"event: error" in message.get("body", b"")):
            async with httpx.AsyncClient(trust_env=False, follow_redirects=False, timeout=3) as client:
                response = await client.post(os.environ["ASSEMBLEAI_CHATBI_CONTROL_ORIGIN"] + "/connector-diagnostic",
                    headers={"X-Fixture-Control": os.environ["ASSEMBLEAI_CHATBI_CONTROL_KEY"]},
                    json={"responses": [{key: (round((time.monotonic() - item["started"]) * 1000)
                        if key == "elapsedMs" and value is None else value)
                        for key, value in item.items() if key != "started"}
                        for item in application.state.connector_responses[-12:]]})
                response.raise_for_status()
        await send(message)

    await application(scope, receive, observed_send)
