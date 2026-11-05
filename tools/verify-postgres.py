"""Run platform migrations and contracts against a fresh native PostgreSQL.

Requires tools/install-native-postgres.ps1 and a built Debug smoke assembly.
Creates only an isolated loopback cluster under .local-data/postgres-tests.
"""
from __future__ import annotations

import argparse
import importlib.util
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
        output, _ = child.communicate(timeout=15)
        for value in sensitive:
            output = output.replace(value, "[redacted]")
        log.write_text(output, encoding="utf-8")
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
    parser.add_argument("--application-browser", action="store_true", help="Also verify SDK, independently built applications and real FastAPI through OIDC/BFF/YARP in Chromium.")
    parser.add_argument("--registry-browser", action="store_true", help="Also verify multi-user registry management and in-flight gateway policies through the real browser UI.")
    parser.add_argument("--resource-browser", action="store_true", help="Verify model/data-source management pages using real OIDC/BFF/PG and a local model fixture.")
    parser.add_argument("--resource-only", action="store_true", help="Focus on migrations, resource management browser and readiness; requires --resource-browser.")
    parser.add_argument("--identity-browser", action="store_true", help="Also verify identity administration with real OIDC/BFF, PostgreSQL and browser pages.")
    parser.add_argument("--identity-only", action="store_true", help="Focus on migrations, bootstrap, identity administration and readiness; add --identity-browser for the actual pages.")
    parser.add_argument("--model-application", action="store_true", help="Verify real OIDC/PG/YARP/FastAPI model delegation with a local protocol fixture.")
    parser.add_argument("--real-model", action="store_true", help="Also make one JSON and one SSE call using an explicit operator model acceptance configuration.")
    parser.add_argument("--chatbi-browser", action="store_true", help="Verify ChatBI through real OIDC, browser, Python, model protocol and PostgreSQL connectors.")
    parser.add_argument("--chatbi-real-model", action="store_true", help="Also verify three synthetic ChatBI questions with the configured real model.")
    parser.add_argument("--chatbi-only", action="store_true", help="Focus an explicit ChatBI run on migrations, instance safety and the ChatBI chain; does not claim the full platform suite.")
    parser.add_argument("--knowledge-browser", action="store_true", help="Verify knowledge using isolated native MinIO/RabbitMQ, real parser/embedding/pgvector, BFF and browser.")
    parser.add_argument("--knowledge-real-model", action="store_true", help="Also verify synthetic document answers with the explicitly configured real model.")
    parser.add_argument("--knowledge-index-only", action="store_true", help="Verify only core migrations and real pgvector indexing/authorization contracts; no broker/model/browser.")
    parser.add_argument("--interactive-demo", action="store_true", help="Keep an isolated local ChatBI experience alive for up to four hours with synthetic data and three fixed query plans; never calls a real model.")
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
    knowledge_services = None
    secrets_to_hide: tuple[str, ...] = ()
    version = "unknown"
    portal_browser = args.portal_browser or os.environ.get("ASSEMBLEAI_PORTAL_BROWSER_TESTS") == "1"
    application_browser = args.application_browser or os.environ.get("ASSEMBLEAI_APPLICATION_BROWSER_TESTS") == "1"
    registry_browser = args.registry_browser or os.environ.get("ASSEMBLEAI_REGISTRY_BROWSER_TESTS") == "1"
    resource_browser = args.resource_browser or os.environ.get("ASSEMBLEAI_RESOURCE_BROWSER_TESTS") == "1"
    identity_browser = args.identity_browser or os.environ.get("ASSEMBLEAI_IDENTITY_BROWSER_TESTS") == "1"
    real_model = args.real_model or os.environ.get("ASSEMBLEAI_REAL_MODEL_TESTS") == "1"
    model_application = args.model_application or real_model or os.environ.get("ASSEMBLEAI_MODEL_APPLICATION_TESTS") == "1"
    chatbi_real_model = args.chatbi_real_model or os.environ.get("ASSEMBLEAI_CHATBI_REAL_MODEL_TESTS") == "1"
    chatbi_browser = args.chatbi_browser or chatbi_real_model or os.environ.get("ASSEMBLEAI_CHATBI_BROWSER_TESTS") == "1"
    knowledge_real_model = args.knowledge_real_model
    knowledge_browser = args.knowledge_browser or knowledge_real_model
    knowledge = knowledge_browser or args.knowledge_index_only
    if args.resource_only and (not resource_browser or knowledge or args.interactive_demo or chatbi_browser or args.chatbi_only or real_model or model_application or portal_browser or application_browser or registry_browser or identity_browser or args.identity_only):
        parser.error("--resource-only requires --resource-browser and cannot be combined with other verification scopes")
    if resource_browser and (knowledge or args.interactive_demo or args.chatbi_only or args.identity_only):
        parser.error("--resource-browser cannot be combined with another focused scope or interactive demo")
    if args.identity_only and (knowledge or args.interactive_demo or chatbi_browser or args.chatbi_only or real_model or model_application or portal_browser or application_browser or registry_browser):
        parser.error("--identity-only cannot be combined with other application verification scopes")
    if knowledge and (args.interactive_demo or chatbi_browser or args.chatbi_only or real_model or model_application or portal_browser or application_browser or registry_browser or identity_browser):
        parser.error("Knowledge acceptance is an isolated scope; do not combine it with another application scope")
    if args.knowledge_index_only and knowledge_browser:
        parser.error("--knowledge-index-only cannot be combined with browser/model acceptance")
    if args.interactive_demo:
        if args.real_model or args.chatbi_real_model:
            parser.error("--interactive-demo cannot call a real model")
        portal_browser = application_browser = registry_browser = identity_browser = model_application = False
        real_model = chatbi_real_model = False
        chatbi_browser = args.chatbi_only = True
    if args.chatbi_only and identity_browser:
        parser.error("--identity-browser cannot be combined with --chatbi-only")
    if args.chatbi_only and not chatbi_browser:
        parser.error("--chatbi-only requires --chatbi-browser or --chatbi-real-model")
    model_secrets: tuple[str, ...] = ()
    try:
        package = json.loads((ROOT / "tools/native-postgres.lock.json").read_text(encoding="utf-8-sig"))["package"]
        version = package["version"]
        postgres_home = (args.dependencies_root / package["relativeHome"]).resolve()
        if not postgres_home.is_relative_to(args.dependencies_root.resolve()):
            raise RuntimeError("Invalid PostgreSQL dependency path.")
        if knowledge:
            installed_path = ROOT / ".local-data/native-knowledge/installed.json"
            if not installed_path.is_file():
                raise RuntimeError("Install the native knowledge dependencies before knowledge acceptance.")
            installed = json.loads(installed_path.read_text(encoding="utf-8-sig"))
            if installed.get("vectorVersion") != "0.8.7" or not installed.get("postgresHome"):
                raise RuntimeError("The independent pgvector 0.8.7 PostgreSQL runtime is required.")
            postgres_home = local_path(Path(installed["postgresHome"]))
            if not postgres_home.is_relative_to((ROOT / ".local-data/native-knowledge").resolve()):
                raise RuntimeError("Knowledge acceptance requires its independent PostgreSQL runtime.")
        binaries = {name: postgres_home / "bin" / (name + ".exe") for name in ("initdb", "pg_ctl", "psql", "postgres")}
        if not all(path.is_file() for path in binaries.values()):
            raise RuntimeError("Run tools/install-native-postgres.ps1 first.")
        smoke = ROOT / "tests/platform/bin/Debug/net10.0/AssembleAI.Platform.SmokeTests.dll"
        dotnet = shutil.which("dotnet.exe")
        if not smoke.is_file() or not dotnet:
            raise RuntimeError("Build AssembleAI.slnx before running PostgreSQL acceptance.")
        node = os.environ.get("ASSEMBLEAI_NODE") or shutil.which("node.exe")
        if not node and not knowledge and not args.chatbi_only:
            raise RuntimeError("Node is required for the production first-administrator bootstrap verification.")
        if (portal_browser or application_browser or registry_browser or identity_browser or resource_browser or chatbi_browser or knowledge_browser) and (not node or not (ROOT / "apps/portal/dist/index.html").is_file()):
            raise RuntimeError("Build the portal and install its locked browser dependencies before browser acceptance.")
        application_python = Path(os.environ.get("ASSEMBLEAI_PYTHON", ROOT / "apps/sample/backend/.venv/Scripts/python.exe")).resolve()
        if (application_browser or model_application or chatbi_browser) and not application_python.is_file():
            raise RuntimeError("Sync the sample backend locked environment before Python application acceptance.")
        if chatbi_browser and not (ROOT / "apps/chatbi/frontend/dist/index.html").is_file():
            raise RuntimeError("Build the independent ChatBI frontend before ChatBI acceptance.")
        knowledge_python = ROOT / "apps/knowledge/backend/.venv/Scripts/python.exe"
        if knowledge_browser and (not knowledge_python.is_file() or not (ROOT / "apps/knowledge/frontend/dist/index.html").is_file()):
            raise RuntimeError("Build the knowledge frontend and install its independent backend environment.")
        if knowledge_browser and not os.environ.get("ASSEMBLEAI_KNOWLEDGE_MODEL_DIRECTORY"):
            raise RuntimeError("Knowledge acceptance requires the explicitly installed local embedding model directory.")
        if real_model or chatbi_real_model or knowledge_real_model:
            acceptance_path = os.environ.get("ASSEMBLEAI_MODEL_ACCEPTANCE_CONFIG")
            if not acceptance_path or not Path(acceptance_path).is_file():
                raise RuntimeError("Real model acceptance requires ASSEMBLEAI_MODEL_ACCEPTANCE_CONFIG.")
            acceptance = json.loads(Path(acceptance_path).read_text(encoding="utf-8-sig"))
            required = {"endpoint", "upstreamModel", "credentialEnvironmentVariable"}
            if not required.issubset(acceptance) or set(acceptance) - required - {"reasoningEffort"}:
                raise RuntimeError("Real model acceptance configuration has unexpected fields.")
            variable = acceptance.get("credentialEnvironmentVariable")
            if not isinstance(variable, str) or not os.environ.get(variable):
                raise RuntimeError("The configured real-model credential environment variable is unavailable.")
            model_secrets = (os.environ[variable],)
        run_id = uuid.uuid4().hex
        run_dir = local_path(RUNS / ("run-" + run_id))
        artifact_dir = local_path(ARTIFACTS / ("run-" + run_id))
        run_dir.mkdir(parents=True)
        artifact_dir.mkdir(parents=True)
        data = run_dir / "data"
        port = free_port()
        admin_password = secrets.token_urlsafe(32)
        app_password = secrets.token_urlsafe(32)
        connector_password = secrets.token_urlsafe(32)
        secrets_to_hide = (admin_password, app_password, connector_password) + model_secrets
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
            "CREATE ROLE assembleai_connector_reader LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS PASSWORD "
            + sql_literal(connector_password) + ";\n"
            "CREATE DATABASE assembleai_platform OWNER assembleai_platform;\n"
            "REVOKE CONNECT ON DATABASE assembleai_platform FROM PUBLIC;\n"
            "GRANT CONNECT ON DATABASE assembleai_platform TO assembleai_platform;\n"
            "GRANT CONNECT ON DATABASE assembleai_platform TO assembleai_connector_reader;\n"
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
                                environment, artifact_dir / ("enable-vector.log" if knowledge else "missing-vector.log"), sensitive=secrets_to_hide)
        if knowledge:
            if vector_result.returncode != 0:
                raise RuntimeError("The real pgvector extension must initialize before knowledge acceptance.")
            vector_repeat = execute(psql(binaries["psql"], port, "assembleai_platform") + ["--file", str(vector_step)],
                                    environment, artifact_dir / "enable-vector-repeat.log", sensitive=secrets_to_hide)
            if vector_repeat.returncode != 0:
                raise RuntimeError("pgvector initialization must be idempotent.")
        elif vector_result.returncode != 3 or "ERROR:  0A000" not in vector_result.stdout:
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
        connector_connection_string = (f"Host=127.0.0.1;Port={port};Database=assembleai_platform;Username=assembleai_connector_reader;"
                                       f"Password={connector_password};Timeout=5;Command Timeout=5;SSL Mode=Disable;Pooling=false")
        config_path.write_text(json.dumps({"connectionString": connection_string,
                                          "connectorConnectionString": connector_connection_string,
                                          "runDirectory": str(run_dir), "runId": run_id}), encoding="utf-8")
        test_environment = environment.copy()
        test_environment.pop("PGPASSWORD", None)
        if args.interactive_demo:
            test_environment.pop("ASSEMBLEAI_MODEL_ACCEPTANCE_CONFIG", None)
        test_environment["ASSEMBLEAI_POSTGRES_TEST_CONFIG"] = str(config_path)
        test_environment["ASSEMBLEAI_POSTGRES_PSQL"] = str(binaries["psql"])
        if node:
            test_environment["ASSEMBLEAI_NODE"] = str(node)
        test_environment["ASSEMBLEAI_PORTAL_BROWSER_TESTS"] = "1" if portal_browser else "0"
        test_environment["ASSEMBLEAI_APPLICATION_BROWSER_TESTS"] = "1" if application_browser else "0"
        test_environment["ASSEMBLEAI_REGISTRY_BROWSER_TESTS"] = "1" if registry_browser else "0"
        test_environment["ASSEMBLEAI_IDENTITY_BROWSER_TESTS"] = "1" if identity_browser else "0"
        test_environment["ASSEMBLEAI_RESOURCE_BROWSER_TESTS"] = "1" if resource_browser else "0"
        test_environment["ASSEMBLEAI_RESOURCE_ONLY"] = "1" if args.resource_only else "0"
        test_environment["ASSEMBLEAI_IDENTITY_ONLY"] = "1" if args.identity_only else "0"
        test_environment["ASSEMBLEAI_MODEL_APPLICATION_TESTS"] = "1" if model_application else "0"
        test_environment["ASSEMBLEAI_REAL_MODEL_TESTS"] = "1" if real_model else "0"
        test_environment["ASSEMBLEAI_CHATBI_BROWSER_TESTS"] = "1" if chatbi_browser else "0"
        test_environment["ASSEMBLEAI_CHATBI_REAL_MODEL_TESTS"] = "1" if chatbi_real_model else "0"
        test_environment["ASSEMBLEAI_CHATBI_ONLY"] = "1" if args.chatbi_only else "0"
        test_environment["ASSEMBLEAI_INTERACTIVE_DEMO"] = "1" if args.interactive_demo else "0"
        test_environment["ASSEMBLEAI_KNOWLEDGE_ONLY"] = "1" if knowledge else "0"
        test_environment["ASSEMBLEAI_KNOWLEDGE_BROWSER_TESTS"] = "1" if knowledge_browser else "0"
        test_environment["ASSEMBLEAI_KNOWLEDGE_REAL_MODEL_TESTS"] = "1" if knowledge_real_model else "0"
        if knowledge_browser:
            module_spec = importlib.util.spec_from_file_location("knowledge_native", ROOT / "tools/knowledge-native.py")
            native = importlib.util.module_from_spec(module_spec)
            module_spec.loader.exec_module(native)
            knowledge_services = native.NativeKnowledgeServices(run_dir, artifact_dir=artifact_dir / "knowledge-services")
            knowledge_services.__enter__()
            secrets_to_hide += knowledge_services.sensitive
            service_config = run_dir / "knowledge-services.json"
            service_config.write_text(json.dumps(knowledge_services.configuration), encoding="utf-8")
            test_environment["ASSEMBLEAI_KNOWLEDGE_SERVICES_CONFIG"] = str(service_config)
            test_environment["ASSEMBLEAI_PYTHON"] = str(knowledge_python)
        if application_browser or model_application or chatbi_browser:
            test_environment["ASSEMBLEAI_PYTHON"] = str(application_python)
        if portal_browser or application_browser or registry_browser or identity_browser or resource_browser or chatbi_browser or knowledge_browser:
            test_environment["ASSEMBLEAI_NODE"] = str(node)
        print(f"Starting local interactive ChatBI; readiness: {run_dir / 'demo-ready.json'}" if args.interactive_demo else
              "Running isolated knowledge contracts with real pgvector and explicitly selected native services..." if knowledge else
              "Running focused ChatBI contracts against the verified isolated PostgreSQL instance..." if args.chatbi_only else
              "Running focused model/data-source management browser with real OIDC/BFF/PG and local model/source protocols..." if args.resource_only else
              "Running focused identity administration, bootstrap, requested browser pages and readiness contracts..." if args.identity_only else
              "Running real PostgreSQL identity administration/bootstrap, authorization, session, registry, gateway, model, connector and catalog contracts...", flush=True)
        tested = execute([dotnet, str(smoke), "--postgres-integration"], test_environment,
                         artifact_dir / "integration.log", timeout=14700 if args.interactive_demo else 1500 if knowledge_real_model else 1200 if chatbi_real_model or knowledge_browser else 900 if real_model or chatbi_browser else 600, sensitive=secrets_to_hide)
        for line in tested.stdout.splitlines():
            if line.startswith(("PASS:", "FAIL: PostgreSQL integration")):
                print(line, flush=True)
        if tested.returncode != 0:
            raise RuntimeError("PostgreSQL contract verification failed; inspect integration.log.")
        passed = True
    except KeyboardInterrupt:
        print("PostgreSQL verification interrupted; cleaning up the isolated cluster.", file=sys.stderr)
    except subprocess.TimeoutExpired:
        print("FAIL: Native PostgreSQL verification exceeded its outer acceptance budget; inspect the last completed contract in integration.log.", file=sys.stderr)
    except (OSError, ValueError, KeyError, RuntimeError, subprocess.SubprocessError) as error:
        print(f"FAIL: {error}" if isinstance(error, RuntimeError)
              else "FAIL: Native PostgreSQL verification could not finish.", file=sys.stderr)
    finally:
        if knowledge_services is not None:
            try:
                knowledge_services.__exit__(None, None, None)
                if not knowledge_services.cleanup_ok:
                    cleanup_ok = False
                    print("FAIL: Native knowledge cleanup could not be confirmed; run data retained.", file=sys.stderr)
            except (OSError, ValueError, RuntimeError, subprocess.SubprocessError):
                cleanup_ok = False
                print("FAIL: Native knowledge shutdown failed; run data retained.", file=sys.stderr)
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
            (artifact_dir / "result.json").write_text(json.dumps({"passed": passed and cleanup_ok and not args.interactive_demo,
                "interactiveDemo": args.interactive_demo,
                "demoExitedCleanly": passed and cleanup_ok if args.interactive_demo else None,
                "cleanupComplete": cleanup_ok, "postgresVersion": version,
                "portalBrowserRequested": portal_browser,
                "applicationBrowserRequested": application_browser,
                "registryBrowserRequested": registry_browser,
                "identityBrowserRequested": identity_browser,
                "identityOnly": args.identity_only,
                "resourceBrowserRequested": resource_browser,
                "resourceOnly": args.resource_only,
                "modelApplicationRequested": model_application,
                "realModelRequested": real_model,
                "chatbiBrowserRequested": chatbi_browser,
                "chatbiRealModelRequested": chatbi_real_model,
                "chatbiOnly": args.chatbi_only,
                "knowledgeBrowserRequested": knowledge_browser,
                "knowledgeRealModelRequested": knowledge_real_model,
                "knowledgeIndexOnly": args.knowledge_index_only,
                "scope": "interactive local demo with synthetic data and fixed example plans; not acceptance" if args.interactive_demo else "knowledge indexing and ACL with real pgvector, plus native services/browser when requested" if knowledge else "core migrations, instance safety and ChatBI chain only; vector absent" if args.chatbi_only else "core migrations, identity bootstrap, administration, requested browser pages and readiness only; broader platform contracts not run" if args.identity_only else "core migrations, model/data-source browser, OIDC/BFF/PG, local model/source protocols and readiness only; broader platform contracts not run" if args.resource_only else "core migrations, identity administration and bootstrap, authorization, protected tickets, registry, runtime leases, production gateway HTTP chain, tenant model configuration and call history, read-only data connector HTTP chain and audit, authorized catalog; vector absent"}, indent=2), encoding="utf-8")
            print(f"Artifacts: {artifact_dir.relative_to(ROOT)}", flush=True)
    if passed and cleanup_ok:
        print("Local demo stopped; isolated cluster and temporary credentials removed." if args.interactive_demo else
              "PASS: native PostgreSQL acceptance; isolated cluster stopped and temporary credentials removed.", flush=True)
        return 0
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
