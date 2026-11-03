"""Run platform migrations and contracts against a fresh native PostgreSQL.

Requires tools/install-native-postgres.ps1 and a built Debug smoke assembly.
Creates only an isolated loopback cluster under .local-data/postgres-tests.
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
import uuid

ROOT = Path(__file__).resolve().parents[1]
RUNS = ROOT / ".local-data/postgres-tests"
ARTIFACTS = ROOT / ".artifacts/postgres"


def local_path(path: Path) -> Path:
    resolved = path.resolve()
    if not resolved.is_relative_to(ROOT.resolve()):
        raise RuntimeError("Refusing a test path outside the workspace.")
    return resolved


def free_port() -> int:
    with socket.socket() as probe:
        if os.name == "nt":
            probe.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


def sql_literal(value: str) -> str:
    return "'" + value.replace("'", "''") + "'"


def execute(command: list[str], environment: dict[str, str], log: Path, *,
            input_text: str | None = None, timeout: int = 60, sensitive: tuple[str, ...] = ()) -> subprocess.CompletedProcess:
    child = subprocess.Popen(command, cwd=ROOT, env=environment,
                             stdin=subprocess.PIPE if input_text is not None else subprocess.DEVNULL,
                             stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                             text=True, encoding="utf-8", errors="replace",
                             creationflags=subprocess.CREATE_NO_WINDOW)
    try:
        output, _ = child.communicate(input_text, timeout=timeout)
    except (subprocess.TimeoutExpired, KeyboardInterrupt):
        # initdb and pg_ctl can have their own children. Stop the owned tree,
        # then let the outer finally confirm cluster shutdown before deletion.
        if child.poll() is None:
            subprocess.run([str(Path(os.environ["SystemRoot"]) / "System32/taskkill.exe"),
                            "/PID", str(child.pid), "/T", "/F"],
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                           creationflags=subprocess.CREATE_NO_WINDOW, timeout=15)
        child.communicate(timeout=15)
        raise
    result = subprocess.CompletedProcess(command, child.returncode, output)
    for value in sensitive:
        output = output.replace(value, "[redacted]")
    log.write_text(output, encoding="utf-8")
    return result


def psql(executable: Path, port: int, database: str, *, username: str = "postgres") -> list[str]:
    return [str(executable), "--no-psqlrc", "--no-password", "--host", "127.0.0.1",
            "--port", str(port), "--username", username, "--dbname", database, "--set", "ON_ERROR_STOP=1"]


def start_cluster(command: list[str], environment: dict[str, str], log: Path) -> int:
    # A detached postgres can inherit pg_ctl's output handle. Waiting for PIPE
    # EOF then waits for the server's entire lifetime, even after pg_ctl exits.
    # Startup has no credentials in its output; write it directly and wait only
    # for pg_ctl. The outer finally stops this data directory on every failure.
    with log.open("w", encoding="utf-8") as output:
        with subprocess.Popen(command, cwd=ROOT, env=environment,
                              stdin=subprocess.DEVNULL, stdout=output, stderr=subprocess.STDOUT,
                              creationflags=subprocess.CREATE_NO_WINDOW) as child:
            try:
                return child.wait(timeout=45)
            except (subprocess.TimeoutExpired, KeyboardInterrupt):
                child.kill()
                child.wait(timeout=15)
                raise


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dependencies-root", type=Path, default=ROOT / ".local-data/native-postgres")
    parser.add_argument("--portal-browser", action="store_true", help="Also verify the built portal with real OIDC/BFF and PostgreSQL in Chromium.")
    args = parser.parse_args()
    if os.name != "nt":
        parser.error("This launcher currently supports the Windows native distribution.")
    run_dir = None
    artifact_dir = None
    data = None
    pg_ctl = None
    environment = None
    started = False
    passed = False
    cleanup_ok = True
    secrets_to_hide: tuple[str, ...] = ()
    version = "unknown"
    portal_browser = args.portal_browser or os.environ.get("ASSEMBLEAI_PORTAL_BROWSER_TESTS") == "1"
    try:
        package = json.loads((ROOT / "tools/native-postgres.lock.json").read_text(encoding="utf-8-sig"))["package"]
        version = package["version"]
        postgres_home = (args.dependencies_root / package["relativeHome"]).resolve()
        if not postgres_home.is_relative_to(args.dependencies_root.resolve()):
            raise RuntimeError("Invalid PostgreSQL dependency path.")
        binaries = {name: postgres_home / "bin" / (name + ".exe") for name in ("initdb", "pg_ctl", "psql", "postgres")}
        if not all(path.is_file() for path in binaries.values()):
            raise RuntimeError("Run tools/install-native-postgres.ps1 first.")
        smoke = ROOT / "tests/platform/bin/Debug/net10.0/AssembleAI.Platform.SmokeTests.dll"
        dotnet = shutil.which("dotnet.exe")
        if not smoke.is_file() or not dotnet:
            raise RuntimeError("Build AssembleAI.slnx before running PostgreSQL acceptance.")
        node = os.environ.get("ASSEMBLEAI_NODE") or shutil.which("node.exe")
        if portal_browser and (not node or not (ROOT / "apps/portal/dist/index.html").is_file()):
            raise RuntimeError("Build the portal and install its locked browser dependencies before --portal-browser acceptance.")
        run_id = uuid.uuid4().hex
        run_dir = local_path(RUNS / ("run-" + run_id))
        artifact_dir = local_path(ARTIFACTS / ("run-" + run_id))
        run_dir.mkdir(parents=True)
        artifact_dir.mkdir(parents=True)
        data = run_dir / "data"
        port = free_port()
        admin_password = secrets.token_urlsafe(32)
        app_password = secrets.token_urlsafe(32)
        secrets_to_hide = (admin_password, app_password)
        password_file = run_dir / "initdb-password"
        password_file.write_text(admin_password + "\n", encoding="utf-8")

        environment = {name: value for name, value in os.environ.items()
                       if not name.upper().startswith(("PG", "NPGSQL_", "AUTHENTICATION__", "CONNECTIONSTRINGS__"))}
        system_root = Path(os.environ["SystemRoot"])
        environment["PATH"] = os.pathsep.join([str(postgres_home / "bin"), str(system_root / "System32"), str(system_root)])
        environment.update({"PGPASSWORD": admin_password, "PGCONNECT_TIMEOUT": "5", "PGCLIENTENCODING": "UTF8"})
        print("Initializing isolated PostgreSQL with SCRAM authentication...", flush=True)
        initialized = execute([str(binaries["initdb"]), "--pgdata", str(data), "--username", "postgres",
                               "--encoding=UTF8", "--locale=C", "--auth=scram-sha-256",
                               "--pwfile", str(password_file)], environment, artifact_dir / "initdb.log",
                              sensitive=secrets_to_hide)
        password_file.unlink(missing_ok=True)
        if initialized.returncode != 0:
            raise RuntimeError("PostgreSQL initialization failed; inspect initdb.log.")
        # The fresh directory and this explicit configuration own the listener.
        # Do not inherit external sockets, configs, services or password files.
        with (data / "postgresql.conf").open("a", encoding="utf-8") as config:
            config.write(f"\nlisten_addresses = '127.0.0.1'\nport = {port}\n"
                         "password_encryption = 'scram-sha-256'\nmax_connections = 30\nshared_buffers = '32MB'\n"
                         "log_statement = 'none'\nlog_min_error_statement = 'panic'\nlog_error_verbosity = 'terse'\n")
        (data / "pg_hba.conf").write_text("host all all 127.0.0.1/32 scram-sha-256\n", encoding="utf-8")
        pg_ctl = binaries["pg_ctl"]
        # Mark before startup so a timeout still attempts to stop this data directory.
        started = True
        startup = start_cluster([str(pg_ctl), "start", "-D", str(data), "-l", str(artifact_dir / "postgres.log"),
                                 "-w", "-t", "30"], environment, artifact_dir / "start.log")
        if startup != 0:
            raise RuntimeError("PostgreSQL startup failed; inspect start.log.")
        bootstrap = (
            "CREATE ROLE assembleai_platform LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS PASSWORD "
            + sql_literal(app_password) + ";\n"
            "CREATE DATABASE assembleai_platform OWNER assembleai_platform;\n"
            "REVOKE CONNECT ON DATABASE assembleai_platform FROM PUBLIC;\n"
            "GRANT CONNECT ON DATABASE assembleai_platform TO assembleai_platform;\n"
        )
        created = execute(psql(binaries["psql"], port, "postgres"), environment, artifact_dir / "bootstrap.log",
                          input_text=bootstrap, sensitive=secrets_to_hide)
        if created.returncode != 0:
            raise RuntimeError("Isolated database bootstrap failed.")

        print("Applying complete core migrations and checking idempotent replay...", flush=True)
        migration_file = ROOT / "deploy/postgres/migrations/apply.sql"
        for attempt in range(2):
            migrated = execute(psql(binaries["psql"], port, "assembleai_platform") + ["--file", str(migration_file)],
                               environment, artifact_dir / f"migrations-{attempt + 1}.log", sensitive=secrets_to_hide)
            if migrated.returncode != 0:
                raise RuntimeError("Core migration failed; inspect the migration log.")
        wrong_database = execute(psql(binaries["psql"], port, "postgres") + ["--file", str(migration_file)],
                                 environment, artifact_dir / "wrong-database.log", sensitive=secrets_to_hide)
        if wrong_database.returncode == 0 or "Refusing platform migration" not in wrong_database.stdout:
            raise RuntimeError("Migrations must reject a different database before changing it.")
        vector_step = ROOT / "deploy/postgres/migrations/enable-vector.sql"
        if not vector_step.is_file():
            raise RuntimeError("The explicit vector initialization script is missing.")
        vector_result = execute(psql(binaries["psql"], port, "assembleai_platform")
                                + ["--set", "VERBOSITY=sqlstate", "--file", str(vector_step)],
                                environment, artifact_dir / "missing-vector.log", sensitive=secrets_to_hide)
        if vector_result.returncode != 3 or "ERROR:  0A000" not in vector_result.stdout:
            raise RuntimeError("The absent vector extension must fail its explicit initialization step.")

        marker_sql = ("SET ROLE assembleai_platform; CREATE SCHEMA test_support; "
                      "CREATE TABLE test_support.instance_marker(run_id text PRIMARY KEY); "
                      "INSERT INTO test_support.instance_marker VALUES (" + sql_literal(run_id) + ");")
        marked = execute(psql(binaries["psql"], port, "assembleai_platform"), environment,
                         artifact_dir / "marker.log", input_text=marker_sql, sensitive=secrets_to_hide)
        if marked.returncode != 0:
            raise RuntimeError("Could not establish the isolated database test marker.")
        connection_string = (f"Host=127.0.0.1;Port={port};Database=assembleai_platform;Username=assembleai_platform;"
                             f"Password={app_password};Timeout=5;Command Timeout=5;SSL Mode=Disable;Pooling=false")
        config_path = run_dir / "test-config.json"
        config_path.write_text(json.dumps({"connectionString": connection_string,
                                          "runDirectory": str(run_dir), "runId": run_id}), encoding="utf-8")
        test_environment = environment.copy()
        test_environment.pop("PGPASSWORD", None)
        test_environment["ASSEMBLEAI_POSTGRES_TEST_CONFIG"] = str(config_path)
        test_environment["ASSEMBLEAI_PORTAL_BROWSER_TESTS"] = "1" if portal_browser else "0"
        if portal_browser:
            test_environment["ASSEMBLEAI_NODE"] = str(node)
        print("Running real PostgreSQL authorization, session, registry, gateway and authorized-catalog contracts...", flush=True)
        tested = execute([dotnet, str(smoke), "--postgres-integration"], test_environment,
                         artifact_dir / "integration.log", timeout=240 if portal_browser else 120, sensitive=secrets_to_hide)
        for line in tested.stdout.splitlines():
            if line.startswith(("PASS:", "FAIL: PostgreSQL integration")):
                print(line, flush=True)
        if tested.returncode != 0:
            raise RuntimeError("PostgreSQL contract verification failed; inspect integration.log.")
        passed = True
    except KeyboardInterrupt:
        print("PostgreSQL verification interrupted; cleaning up the isolated cluster.", file=sys.stderr)
    except (OSError, ValueError, KeyError, RuntimeError, subprocess.SubprocessError) as error:
        print(f"FAIL: {error}" if isinstance(error, RuntimeError)
              else "FAIL: Native PostgreSQL verification could not finish.", file=sys.stderr)
    finally:
        if started and pg_ctl is not None and data is not None and environment is not None:
            try:
                stopped = execute([str(pg_ctl), "stop", "-D", str(data), "-m", "fast", "-w", "-t", "30"],
                                  environment, artifact_dir / "stop.log", sensitive=secrets_to_hide)
                if stopped.returncode != 0:
                    status = execute([str(pg_ctl), "status", "-D", str(data)], environment,
                                     artifact_dir / "status.log", sensitive=secrets_to_hide)
                    if status.returncode != 3:  # pg_ctl documents 3 as not running.
                        cleanup_ok = False
                        print("FAIL: Could not confirm isolated PostgreSQL shutdown; data retained.", file=sys.stderr)
            except (OSError, RuntimeError, subprocess.SubprocessError):
                cleanup_ok = False
                print("FAIL: Isolated PostgreSQL shutdown failed; data retained.", file=sys.stderr)
        if run_dir is not None and cleanup_ok:
            try:
                target = local_path(run_dir)
                if target.parent != RUNS.resolve() or not target.name.startswith("run-"):
                    raise RuntimeError("Refusing an unexpected cleanup target.")
                if target.is_dir():
                    shutil.rmtree(target)
            except (OSError, RuntimeError):
                cleanup_ok = False
                print("FAIL: PostgreSQL temporary-directory cleanup failed.", file=sys.stderr)
        if artifact_dir is not None and artifact_dir.is_dir():
            (artifact_dir / "result.json").write_text(json.dumps({"passed": passed and cleanup_ok,
                "cleanupComplete": cleanup_ok, "postgresVersion": version,
                "portalBrowserRequested": portal_browser,
                "scope": "core migrations, authorization, protected tickets, registry, runtime leases, production gateway HTTP chain and authorized catalog; vector absent"}, indent=2), encoding="utf-8")
            print(f"Artifacts: {artifact_dir.relative_to(ROOT)}", flush=True)
    if passed and cleanup_ok:
        print("PASS: native PostgreSQL acceptance; isolated cluster stopped and temporary credentials removed.", flush=True)
        return 0
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
