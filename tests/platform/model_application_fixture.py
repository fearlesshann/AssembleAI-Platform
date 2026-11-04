"""Test-only ASGI wrapper: transfer capabilities to an authenticated loopback driver.

The production sample application handles every application request. Credentials
stay in driver memory and are never returned to the browser, stored, or logged.
"""
import os

import httpx

forbidden_variable = os.environ.get("ASSEMBLEAI_MODEL_TEST_FORBIDDEN_CREDENTIAL_VARIABLE")
if forbidden_variable and forbidden_variable in os.environ:
    raise RuntimeError("A provider credential was inherited by the application fixture.")

from sample_backend.main import app as application


async def app(scope, receive, send):
    if scope["type"] == "http" and scope["method"] == "POST" and scope["path"] == "/model-chat":
        headers = dict(scope["headers"])
        async with httpx.AsyncClient(trust_env=False, timeout=3) as driver:
            response = await driver.post(
                os.environ["ASSEMBLEAI_MODEL_TEST_CONTROL_ORIGIN"] + "/capture",
                headers={"X-Fixture-Control": os.environ["ASSEMBLEAI_MODEL_TEST_CONTROL_KEY"]},
                json={"capability": headers.get(b"x-model-delegation", b"").decode("ascii"),
                      "authorization": headers.get(b"authorization", b"").decode("ascii")},
            )
            response.raise_for_status()
    await application(scope, receive, send)
