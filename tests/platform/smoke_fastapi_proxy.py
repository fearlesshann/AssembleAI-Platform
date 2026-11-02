"""Run the real FastAPI sample behind YARP; stdlib-only test runner.

First build AssembleAI.slnx and install the sample backend's locked dependencies
into the Python interpreter used to run this script. No infrastructure services
or persistent test servers are needed.
"""

from __future__ import annotations

import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request


ROOT = Path(__file__).resolve().parents[2]


def unused_port() -> int:
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        return listener.getsockname()[1]


def wait_live(address: str, process: subprocess.Popen[str]) -> None:
    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise RuntimeError(f"Service exited early: {process.stdout.read()}")
        try:
            with urllib.request.urlopen(address + "/health/live", timeout=0.5) as response:
                if response.status == 200:
                    return
        except (OSError, urllib.error.URLError):
            time.sleep(0.1)
    raise TimeoutError(f"Service did not become live: {address}")


def main() -> None:
    assembly = ROOT / "services/platform/bin/Debug/net10.0/AssembleAI.Platform.dll"
    if not assembly.exists():
        raise RuntimeError("Build AssembleAI.slnx before running this test.")

    sample_port = unused_port()
    gateway_port = unused_port()
    while gateway_port == sample_port:
        gateway_port = unused_port()
    sample_address = f"http://127.0.0.1:{sample_port}"
    gateway_address = f"http://127.0.0.1:{gateway_port}"
    processes: list[subprocess.Popen[str]] = []
    environment = os.environ.copy()
    environment["ASSEMBLEAI_ENVIRONMENT"] = "Development"
    options = dict(
        env=environment,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        encoding="utf-8",
        errors="replace",
        creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
    )
    try:
        sample = subprocess.Popen(
            [sys.executable, "-m", "uvicorn", "sample_backend.main:app", "--host", "127.0.0.1",
             "--port", sample_address.rsplit(":", 1)[1]],
            cwd=ROOT / "apps/sample/backend", **options,
        )
        processes.append(sample)
        wait_live(sample_address, sample)

        platform = subprocess.Popen(
            ["dotnet", str(assembly), "--environment", "Development", "--urls", gateway_address,
             "--contentRoot", str(ROOT / "services/platform"),
             "--Services:Sample:Address", sample_address + "/"],
            cwd=ROOT, **options,
        )
        processes.append(platform)
        wait_live(gateway_address, platform)

        with urllib.request.urlopen(gateway_address + "/dev/sample/health", timeout=5) as response:
            assert response.status == 200, "YARP must reach the FastAPI health endpoint."

        first_event_at = None
        complete_at = None
        deltas = []
        request_ids = set()
        event_name = ""
        with urllib.request.urlopen(
            gateway_address + "/dev/sample/stream?count=3&delay_ms=1000", timeout=8
        ) as response:
            assert response.headers.get_content_type() == "text/event-stream"
            for raw_line in response:
                line = raw_line.decode("utf-8").strip()
                if line.startswith("event: "):
                    event_name = line.removeprefix("event: ")
                    if event_name == "delta" and first_event_at is None:
                        first_event_at = time.monotonic()
                    elif event_name == "complete":
                        complete_at = time.monotonic()
                elif line.startswith("data: "):
                    payload = json.loads(line.removeprefix("data: "))
                    request_ids.add(payload["requestId"])
                    if event_name == "delta":
                        deltas.append(payload)
                    elif event_name == "complete":
                        assert payload["count"] == 3

        assert len(deltas) == 3, f"Expected three delta events; received {len(deltas)}."
        assert len(request_ids) == 1, "Request identity must survive through all events."
        assert first_event_at is not None and complete_at is not None
        assert complete_at - first_event_at >= 1, "Proxy buffered the response instead of forwarding increments."
        print("PASS: actual FastAPI -> YARP health, parameter forwarding, UTF-8 SSE, incremental delivery and request correlation.")
    finally:
        for process in reversed(processes):
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=5)
            if process.stdout:
                process.stdout.close()


if __name__ == "__main__":
    main()
