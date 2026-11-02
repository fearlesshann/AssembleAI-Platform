"""Run the local .NET and FastAPI development processes without containers.

Use the project's Python 3.13 environment. Build AssembleAI.slnx first.
Ctrl+C stops only the two processes started by this command.
"""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[1]


def check_port(port: int) -> int:
    if not 1 <= port <= 65535:
        raise ValueError("Port must be between 1 and 65535")
    with socket.socket() as probe:
        # Exclusive binding avoids passing a port already owned by another process.
        if os.name == "nt":
            probe.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
        probe.bind(("127.0.0.1", port))
    return port


def wait_for_health(url: str, child: subprocess.Popen, timeout: float = 20) -> None:
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if child.poll() is not None:
            raise RuntimeError(f"Development process exited with code {child.returncode}")
        try:
            with opener.open(url, timeout=0.8) as response:
                if response.status == 200:
                    return
        except (OSError, urllib.error.URLError):
            time.sleep(0.1)
    raise TimeoutError(f"Service did not become live: {url}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--platform-port", type=int, default=5100)
    parser.add_argument("--sample-port", type=int, default=8101)
    parser.add_argument("--dotnet", default=shutil.which("dotnet"))
    parser.add_argument("--check", action="store_true", help="Start, verify native process wiring, then stop")
    args = parser.parse_args()
    if not all(1 <= port <= 65535 for port in (args.platform_port, args.sample_port)):
        parser.error("Ports must be between 1 and 65535")
    if sys.version_info[:2] != (3, 13):
        parser.error("Use Python 3.13 from the sample backend environment")
    if not args.dotnet or not Path(args.dotnet).is_file():
        parser.error("dotnet executable not found; pass its full path with --dotnet")
    assembly = ROOT / "services/platform/bin/Debug/net10.0/AssembleAI.Platform.dll"
    if not assembly.is_file():
        parser.error("Build the Debug solution first: dotnet build AssembleAI.slnx")
    if args.sample_port == args.platform_port:
        parser.error("Sample and platform need different ports")

    children: list[subprocess.Popen] = []
    try:
        check_port(args.sample_port)
        check_port(args.platform_port)
        sample_url = f"http://127.0.0.1:{args.sample_port}"
        platform_url = f"http://127.0.0.1:{args.platform_port}"
        environment = os.environ.copy()
        environment["ASSEMBLEAI_ENVIRONMENT"] = "Development"
        options = {
            "env": environment,
            "creationflags": subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
        }
        sample = subprocess.Popen(
            [sys.executable, "-m", "uvicorn", "sample_backend.main:app", "--host", "127.0.0.1",
             "--port", str(args.sample_port)],
            cwd=ROOT / "apps/sample/backend", **options,
        )
        children.append(sample)
        wait_for_health(sample_url + "/health/ready", sample)
        platform = subprocess.Popen(
            [args.dotnet, str(assembly), "--environment", "Development", "--urls", platform_url,
             "--contentRoot", str(ROOT / "services/platform"), "--DevelopmentSample:Enabled", "true",
             "--Services:Sample:Address", sample_url + "/"],
            cwd=ROOT, **options,
        )
        children.append(platform)
        wait_for_health(platform_url + "/health/live", platform)
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        with opener.open(platform_url + "/dev/sample/health", timeout=3) as response:
            payload = json.load(response)
            if payload.get("service") != "sample":
                raise RuntimeError("Gateway did not reach the configured sample service")
        print(f"Platform: {platform_url}", flush=True)
        print(f"Sample API/docs: {sample_url}/docs", flush=True)
        print(f"Sample via YARP: {platform_url}/dev/sample/stream?count=3&delay_ms=200", flush=True)
        print("Native development processes are live. Infrastructure readiness is separate and may be 503.", flush=True)
        if args.check:
            print("PASS: native startup and gateway wiring; both processes will now stop.", flush=True)
            return 0
        print("Press Ctrl+C to stop both processes.", flush=True)
        while True:
            for child in children:
                if child.poll() is not None:
                    raise RuntimeError(f"A development process exited with code {child.returncode}")
            time.sleep(0.5)
    except KeyboardInterrupt:
        return 0
    except (OSError, RuntimeError, TimeoutError) as error:
        print(f"FAIL: {error}", file=sys.stderr)
        return 1
    finally:
        for child in reversed(children):
            if child.poll() is None:
                child.terminate()
                try:
                    child.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    child.kill()
                    child.wait(timeout=5)


if __name__ == "__main__":
    raise SystemExit(main())
