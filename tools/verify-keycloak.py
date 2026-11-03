"""Verify BFF login against an isolated native Keycloak and real Chromium.

Prepare locked dependencies with tools/install-native-identity.ps1 and build the
Debug solution first. No downloads, global installation or existing services
are involved. Only this run's processes and temporary data are cleaned up.
"""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import secrets
import shutil
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request
import uuid


ROOT = Path(__file__).resolve().parents[1]
RUNS = ROOT / ".local-data/keycloak-tests"
ARTIFACTS = ROOT / ".artifacts/keycloak"


def free_port() -> int:
    with socket.socket() as probe:
        if os.name == "nt":
            probe.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


def require_file(path: Path, message: str) -> Path:
    if not path.is_file():
        raise RuntimeError(message)
    return path.resolve()


def dependency_home(base: Path, package: dict) -> Path:
    path = (base / package["relativeHome"]).resolve()
    if not path.is_relative_to(base.resolve()):
        raise RuntimeError("Native dependency path must remain inside its install directory.")
    return path


def wait_http(url: str, child: subprocess.Popen, timeout: float, label: str) -> dict:
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    deadline = time.monotonic() + timeout
    next_report = time.monotonic() + 10
    while time.monotonic() < deadline:
        if child.poll() is not None:
            raise RuntimeError(f"{label} exited before becoming ready; inspect the local service log.")
        try:
            with opener.open(url, timeout=1) as response:
                if response.status == 200:
                    return json.load(response)
        except (OSError, urllib.error.URLError, json.JSONDecodeError):
            pass
        if time.monotonic() >= next_report:
            print(f"Waiting for {label}...", flush=True)
            next_report = time.monotonic() + 10
        time.sleep(0.2)
    raise RuntimeError(f"{label} startup timed out; inspect the local service log.")


def stop_child(child: subprocess.Popen) -> None:
    if child.poll() is not None:
        return
    # The retained Popen object identifies the child we launched. cmd waits for
    # its Java child; kill its tree before the wrapper can leave an orphan JVM.
    taskkill = Path(os.environ["SystemRoot"]) / "System32/taskkill.exe"
    result = subprocess.run(
        [str(taskkill), "/PID", str(child.pid), "/T", "/F"],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
        creationflags=subprocess.CREATE_NO_WINDOW, timeout=15,
    )
    if result.returncode != 0 and child.poll() is None:
        raise RuntimeError("Could not stop a process owned by this test run.")
    child.wait(timeout=15)


def remove_run(path: Path) -> None:
    # Resolve immediately before recursive deletion; never accept an external
    # CLI path or the parent .local-data directory as a cleanup target.
    target = path.resolve()
    parent = RUNS.resolve()
    if (not target.is_relative_to(ROOT.resolve()) or target.parent != parent
            or not target.name.startswith("run-") or target == parent):
        raise RuntimeError("Refusing to remove a path outside this test run.")
    if target.is_dir():
        shutil.rmtree(target)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dependencies-root", type=Path, default=ROOT / ".local-data/native-identity")
    parser.add_argument("--startup-timeout", type=int, default=180)
    args = parser.parse_args()
    if os.name != "nt":
        parser.error("This launcher currently verifies the Windows native distribution.")
    if not 10 <= args.startup_timeout <= 600:
        parser.error("startup-timeout must be between 10 and 600 seconds")

    children: list[subprocess.Popen] = []
    logs = []
    run_dir: Path | None = None
    artifact_dir: Path | None = None
    passed = False
    cleanup_ok = True
    try:
        lock = json.loads((ROOT / "tools/native-identity.lock.json").read_text(encoding="utf-8-sig"))
        packages = lock["packages"]
        java_home = dependency_home(args.dependencies_root, packages["jdk"])
        keycloak_home = dependency_home(args.dependencies_root, packages["keycloak"])
        require_file(java_home / "bin/java.exe", "Run tools/install-native-identity.ps1 first (JDK missing).")
        require_file(keycloak_home / "bin/kc.bat", "Run tools/install-native-identity.ps1 first (Keycloak missing).")
        assembly = require_file(ROOT / "services/platform/bin/Debug/net10.0/AssembleAI.Platform.dll",
                                "Build AssembleAI.slnx before running the Keycloak test.")
        node = shutil.which("node.exe")
        dotnet = shutil.which("dotnet.exe")
        if not node or not dotnet:
            raise RuntimeError("Node and .NET must be installed before running this test.")
        require_file(ROOT / "node_modules/@playwright/test/package.json", "Install locked frontend dependencies and Chromium first.")

        run_id = "run-" + uuid.uuid4().hex
        run_dir = RUNS / run_id
        artifact_dir = ARTIFACTS / run_id
        if not all(path.resolve().is_relative_to(ROOT.resolve()) for path in (run_dir, artifact_dir)):
            raise RuntimeError("Test runtime and artifacts must remain inside the workspace.")
        run_dir.mkdir(parents=True)
        artifact_dir.mkdir(parents=True)
        # A private copy keeps Quarkus augmentation, H2 and imported users away
        # from the reusable installation and any other test run.
        runtime = run_dir / "keycloak"
        print("Preparing isolated native Keycloak runtime...", flush=True)
        shutil.copytree(keycloak_home, runtime)
        platform_port = free_port()
        identity_port = free_port()
        while identity_port == platform_port:
            identity_port = free_port()
        platform_origin = f"http://127.0.0.1:{platform_port}"
        identity_origin = f"http://127.0.0.1:{identity_port}"
        realm_name = "assembleai-test-" + uuid.uuid4().hex
        authority = identity_origin + "/realms/" + realm_name
        subject = str(uuid.uuid4())
        username = "native-test-" + secrets.token_hex(4)
        password = secrets.token_urlsafe(32)
        client_secret = secrets.token_urlsafe(32)
        realm = json.loads((ROOT / "deploy/keycloak/assembleai-realm.json").read_text(encoding="utf-8-sig"))
        realm["realm"] = realm_name
        # Keep the checked-in client's security settings; change only the
        # isolated callback and runtime secret, with no business-user seeding.
        client = realm["clients"][0]
        client["redirectUris"] = [platform_origin + "/signin-oidc"]
        client["secret"] = "${ASSEMBLEAI_OIDC_CLIENT_SECRET}"
        client["attributes"].pop("post.logout.redirect.uris", None)
        realm["users"] = [{
            "id": subject, "username": username, "enabled": True,
            "firstName": "Native", "lastName": "Test", "email": username + "@example.test",
            "emailVerified": True, "requiredActions": [],
            "credentials": [{"type": "password", "value": password, "temporary": False}],
        }]
        import_dir = runtime / "data/import"
        import_dir.mkdir(parents=True, exist_ok=True)
        realm_path = import_dir / (realm_name + "-realm.json")
        realm_path.write_text(json.dumps(realm), encoding="utf-8")
        config_path = run_dir / "browser-config.json"
        config_path.write_text(json.dumps({
            "platformOrigin": platform_origin, "authority": authority,
            "username": username, "password": password, "subject": subject,
            "artifactDirectory": str(artifact_dir),
        }), encoding="utf-8")

        system_root = Path(os.environ["SystemRoot"])
        environment = os.environ.copy()
        # Do not inherit a developer's Keycloak database, provider configuration
        # or JVM agents into an isolated test server.
        for name in list(environment):
            if name.upper().startswith(("KC_", "QUARKUS_", "JAVA_OPTS")) or name.upper() in {
                "JAVA_TOOL_OPTIONS", "JDK_JAVA_OPTIONS", "_JAVA_OPTIONS", "JAVA", "JAVA_ADD_OPENS",
                "JAVA_LOCALE", "PRINT_ENV", "DEBUG", "DEBUG_PORT", "DEBUG_SUSPEND",
            }:
                environment.pop(name)
        # The inherited Windows PATH may exceed cmd.exe's limit. These children
        # only need Java, Node and the Windows process-management commands.
        environment["PATH"] = os.pathsep.join([str(java_home / "bin"), str(Path(node).parent),
                                               str(system_root / "System32"), str(system_root)])
        environment["JAVA_HOME"] = str(java_home)
        environment["JAVA"] = str(java_home / "bin/java.exe")
        environment["KC_DEBUG"] = "false"
        environment["ASSEMBLEAI_OIDC_CLIENT_SECRET"] = client_secret
        environment["KC_BOOTSTRAP_ADMIN_USERNAME"] = "test-bootstrap"
        environment["KC_BOOTSTRAP_ADMIN_PASSWORD"] = secrets.token_urlsafe(32)
        environment["KC_HTTP_MANAGEMENT_HOST"] = "127.0.0.1"
        environment["KC_HEALTH_ENABLED"] = "false"
        environment["KC_METRICS_ENABLED"] = "false"
        environment["JAVA_OPTS_KC_HEAP"] = "-Xms128m -Xmx512m"
        cmd = system_root / "System32/cmd.exe"
        kc_script = runtime / "bin/kc.bat"
        if any(character in str(kc_script) for character in '\r\n"&|<>^%!'):
            raise RuntimeError("The workspace path contains unsupported cmd.exe metacharacters.")
        command = (f'"{cmd}" /d /s /c ""{kc_script}" start-dev --db=dev-file --http-host=127.0.0.1 '
                   f'--http-port={identity_port} --hostname={identity_origin} --hostname-strict=true --import-realm"')
        keycloak_log = (artifact_dir / "keycloak.log").open("w", encoding="utf-8")
        logs.append(keycloak_log)
        keycloak = subprocess.Popen(command, executable=str(cmd), cwd=runtime, env=environment,
                                    stdout=keycloak_log, stderr=subprocess.STDOUT,
                                    creationflags=subprocess.CREATE_NO_WINDOW)
        children.append(keycloak)
        discovery = wait_http(authority + "/.well-known/openid-configuration", keycloak,
                              args.startup_timeout, "Keycloak")
        if discovery.get("issuer") != authority:
            raise RuntimeError("Keycloak discovery did not match the isolated issuer.")
        print("Keycloak discovery is ready; starting BFF...", flush=True)
        platform_env = environment.copy()
        for name in list(platform_env):
            if name.upper().startswith(("AUTHENTICATION__BFF__", "CONNECTIONSTRINGS__",
                                        "DEVELOPMENTSAMPLE__", "LOGGING__", "ASPNETCORE_")):
                platform_env.pop(name)
        platform_env.update({
            "ASPNETCORE_ENVIRONMENT": "Development",
            "DOTNET_ENVIRONMENT": "Development",
            "Authentication__Bff__Enabled": "true",
            "Authentication__Bff__Authority": authority,
            "Authentication__Bff__ClientId": client["clientId"],
            "Authentication__Bff__ClientSecret": client_secret,
            "Authentication__Bff__PublicOrigin": platform_origin,
            "ConnectionStrings__Platform": "",
            "DevelopmentSample__Enabled": "false",
            "Logging__LogLevel__Default": "Warning",
            "Logging__LogLevel__Microsoft.AspNetCore": "Warning",
        })
        platform_log = (artifact_dir / "platform.log").open("w", encoding="utf-8")
        logs.append(platform_log)
        platform = subprocess.Popen([dotnet, str(assembly), "--environment", "Development", "--urls", platform_origin,
                                     "--contentRoot", str(ROOT / "services/platform")],
                                    cwd=ROOT, env=platform_env, stdout=platform_log,
                                    stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW)
        children.append(platform)
        wait_http(platform_origin + "/health/live", platform, 30, "BFF")
        browser_env = os.environ.copy()
        # Playwright protocol debug output includes form values and cookies.
        # Keep browser-cache configuration, but never inherit tracing/debug or
        # arbitrary Node preload options into a credential-bearing test.
        for name in list(browser_env):
            if name.upper() in {"DEBUG", "DEBUG_FILE", "PWDEBUG", "PWDEBUGIMPL", "NODE_OPTIONS"}:
                browser_env.pop(name)
        browser_env["PATH"] = environment["PATH"]
        browser_env["ASSEMBLEAI_KEYCLOAK_TEST_CONFIG"] = str(config_path)
        browser = subprocess.Popen([node, str(ROOT / "tests/platform/keycloak-browser.mjs")],
                                   cwd=ROOT, env=browser_env, stdout=subprocess.PIPE,
                                   stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace",
                                   creationflags=subprocess.CREATE_NO_WINDOW)
        children.append(browser)
        browser_output, _ = browser.communicate(timeout=180)
        (artifact_dir / "browser.log").write_text(browser_output, encoding="utf-8")
        for line in browser_output.splitlines():
            if line.startswith(("PASS: real Keycloak", "FAIL: Keycloak browser verification")):
                print(line, flush=True)
        if browser.returncode != 0:
            raise RuntimeError("Real Keycloak browser acceptance failed.")
        passed = True
    except KeyboardInterrupt:
        print("Keycloak verification interrupted; cleaning up owned processes.", file=sys.stderr)
    except (OSError, ValueError, KeyError, RuntimeError, subprocess.SubprocessError) as error:
        # Exceptions from service logs and HTTP payloads are never printed.
        print(f"FAIL: {error}" if isinstance(error, RuntimeError) else
              "FAIL: Native Keycloak verification could not finish; inspect the local artifacts.", file=sys.stderr)
    finally:
        for child in reversed(children):
            try:
                stop_child(child)
            except (OSError, RuntimeError, subprocess.SubprocessError):
                cleanup_ok = False
                print("FAIL: An owned process could not be stopped; temporary data retained.", file=sys.stderr)
        for log in logs:
            log.close()
        if run_dir is not None and cleanup_ok:
            try:
                remove_run(run_dir)
            except (OSError, RuntimeError):
                cleanup_ok = False
                print("FAIL: Temporary runtime cleanup did not finish.", file=sys.stderr)
        if artifact_dir is not None:
            (artifact_dir / "result.json").write_text(json.dumps({
                "passed": passed and cleanup_ok, "cleanupComplete": cleanup_ok,
                "keycloakVersion": packages["keycloak"]["version"],
                "jdkVersion": packages["jdk"]["version"],
                "scope": "native Keycloak browser login; development memory sessions; no PostgreSQL",
            }, indent=2), encoding="utf-8")
            print(f"Artifacts: {artifact_dir.relative_to(ROOT)}", flush=True)
    if passed and cleanup_ok:
        print("PASS: native Keycloak browser acceptance; owned processes stopped and temporary credentials removed.", flush=True)
        return 0
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
