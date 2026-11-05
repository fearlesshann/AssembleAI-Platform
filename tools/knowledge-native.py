"""Isolated Windows MinIO/RabbitMQ processes for real local acceptance.

Import with importlib (the filename follows the other command-line tools), then
use ``with NativeKnowledgeServices(run_directory) as services``. Credentials
exist only in its configuration, child environments and owned temporary files.
This helper never creates Windows services or modifies an existing broker.
"""
from __future__ import annotations

import argparse
import base64
from datetime import datetime, timezone
import hashlib
import hmac
import json
import os
from pathlib import Path
import secrets
import shutil
import socket
import subprocess
import threading
import time
from urllib.error import HTTPError, URLError
from urllib.parse import quote, urlsplit
from urllib.request import Request, urlopen
import uuid

ROOT = Path(__file__).resolve().parents[1]
DEPENDENCIES = ROOT / ".local-data/native-knowledge"
MARKER = ".assembleai-native-knowledge-run"


def _local(path: Path, parent: Path = ROOT) -> Path:
    """Resolve only workspace paths, rejecting existing junctions/symlinks."""
    absolute = Path(os.path.abspath(path))
    root = Path(os.path.abspath(parent))
    if absolute == root or not absolute.is_relative_to(root):
        raise ValueError("Native knowledge paths must be inside their owned parent.")
    current = absolute
    while current != root.parent:
        if current.is_symlink() or (hasattr(current, "is_junction") and current.is_junction()):
            raise ValueError("Native knowledge paths cannot contain links or junctions.")
        current = current.parent
    return absolute


def _port() -> int:
    with socket.socket() as probe:
        if os.name == "nt":
            probe.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
        probe.bind(("127.0.0.1", 0))
        return int(probe.getsockname()[1])


def _listening(port: int) -> bool:
    try:
        with socket.create_connection(("127.0.0.1", port), timeout=0.2):
            return True
    except OSError:
        return False


class NativeKnowledgeServices:
    def __init__(self, run_dir: Path, config_path: Path | None = None,
                 artifact_dir: Path | None = None):
        self.run_id = uuid.uuid4().hex
        self.parent = _local(Path(run_dir))
        self.directory = _local(self.parent / ("knowledge-" + self.run_id), self.parent)
        self.config_path = _local(Path(config_path) if config_path else DEPENDENCIES / "installed.json")
        self.artifact_dir = _local(Path(artifact_dir) if artifact_dir else ROOT / ".artifacts/knowledge" / ("run-" + self.run_id))
        self.configuration: dict = {}
        self.sensitive: tuple[str, ...] = ()
        self.cleanup_ok = False
        self.phase = "initialization"
        self._processes: list[subprocess.Popen] = []
        self._readers: list[threading.Thread] = []
        self._ports: list[int] = []
        self._environment: dict[str, str] = {}
        self._rabbit_home: Path | None = None
        self._node = ""
        self._started = False
        self._external_objects: Path | None = None
        self._external_parent: Path | None = None

    def _redact(self, value: str) -> str:
        for secret in self.sensitive:
            value = value.replace(secret, "[redacted]")
        return value

    def _spawn(self, name: str, command: list[str], environment: dict[str, str]) -> subprocess.Popen:
        process = subprocess.Popen(command, cwd=self.directory, env=environment,
                                   stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                   creationflags=subprocess.CREATE_NO_WINDOW)
        self._processes.append(process)

        def capture() -> None:
            assert process.stdout is not None
            with (self.artifact_dir / (name + ".log")).open("w", encoding="utf-8") as output:
                for line in iter(process.stdout.readline, b""):
                    output.write(self._redact(line.decode("utf-8", errors="replace")))
                    output.flush()
            process.stdout.close()

        thread = threading.Thread(target=capture, name="knowledge-log-" + name, daemon=True)
        thread.start()
        self._readers.append(thread)
        return process

    @staticmethod
    def _kill(process: subprocess.Popen) -> None:
        if process.poll() is None:
            subprocess.run([str(Path(os.environ["SystemRoot"]) / "System32/taskkill.exe"),
                            "/PID", str(process.pid), "/T", "/F"],
                           stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                           creationflags=subprocess.CREATE_NO_WINDOW, timeout=15, check=False)
            process.wait(timeout=15)

    def __enter__(self) -> NativeKnowledgeServices:
        if os.name != "nt":
            raise RuntimeError("Native knowledge currently requires Windows x64.")
        if self._started or self.directory.exists():
            raise RuntimeError("Native knowledge service instances cannot be reused.")
        installed = json.loads(self.config_path.read_text(encoding="utf-8-sig"))
        locked = json.loads((ROOT / "tools/native-knowledge.lock.json").read_text(encoding="utf-8"))
        if installed.get("schemaVersion") != 1 or installed.get("minioCommit") != locked["minio"]["commit"]:
            raise RuntimeError("Run the locked native knowledge installer first.")
        minio = _local(Path(installed.get("minioExecutable") or "missing"), DEPENDENCIES)
        erlang = _local(Path(installed["erlangHome"]), DEPENDENCIES)
        self._rabbit_home = _local(Path(installed["rabbitmqHome"]), DEPENDENCIES)
        runtimes = list(erlang.glob("erts-*/bin/epmd.exe"))
        if len(runtimes) != 1:
            raise RuntimeError("Erlang package must contain exactly one runtime.")
        epmd = _local(runtimes[0], DEPENDENCIES)
        required = [minio, erlang / "bin/erl.exe", epmd, self._rabbit_home / "sbin/rabbitmq-server.bat"]
        if not all(path.is_file() for path in required):
            raise RuntimeError("Native knowledge dependency entry points are missing.")
        with minio.open("rb") as binary:
            if hashlib.file_digest(binary, "sha256").hexdigest() != installed.get("minioSha256"):
                raise RuntimeError("Native MinIO executable does not match its build manifest.")
        for name in ("erlang", "rabbitmq"):
            if installed.get("versions", {}).get(name) != locked["packages"][name]["version"]:
                raise RuntimeError("Native dependency version does not match its lock.")
        self.parent.mkdir(parents=True, exist_ok=True)
        self.directory.mkdir()
        (self.directory / MARKER).write_text(self.run_id, encoding="ascii")
        self.artifact_dir.mkdir(parents=True, exist_ok=True)
        self._started = True
        try:
            for directory in ("profile", "tmp", "objects", "rabbit", "rabbit/log", "rabbit/db", "rabbit/plugins"):
                (self.directory / directory).mkdir(parents=True, exist_ok=True)
            objects = self.directory / "objects"
            # MinIO reserves 1% of the backing volume. An explicitly marked
            # external build root also permits isolated acceptance object data,
            # without changing the server's safety threshold or other files.
            if installed.get("buildCacheRoot"):
                cache_root = Path(installed["buildCacheRoot"])
                if cache_root.is_absolute() and cache_root.drive.lower() != ROOT.drive.lower():
                    marker = _local(cache_root / ".assembleai-native-build-cache.json", cache_root)
                    ownership = json.loads(marker.read_text(encoding="utf-8-sig"))
                    if ownership.get("schemaVersion") != 1 or Path(ownership.get("workspace", "")).resolve() != ROOT:
                        raise RuntimeError("External native data root is not owned by this workspace.")
                    self._external_parent = _local(cache_root / "services", cache_root)
                    objects = _local(self._external_parent / ("objects-" + self.run_id), self._external_parent)
                    objects.mkdir(parents=True)
                    (objects / MARKER).write_text(self.run_id, encoding="ascii")
                    self._external_objects = objects
            while len(self._ports) < 6:
                candidate = _port()
                if candidate not in self._ports:
                    self._ports.append(candidate)
            s3_port, console_port, amqp_port, management_port, epmd_port, distribution_port = self._ports
            access_key = "knowledge" + secrets.token_hex(8)
            secret_key = secrets.token_urlsafe(32)
            username = "knowledge_" + secrets.token_hex(8)
            password = secrets.token_urlsafe(32)
            cookie = secrets.token_hex(32)
            self.sensitive = (access_key, secret_key, username, password, cookie)
            virtual_host = "acceptance_" + self.run_id
            self.configuration = {
                "minio": {"endpoint": f"http://127.0.0.1:{s3_port}", "accessKey": access_key,
                          "secretKey": secret_key, "bucket": "knowledge-" + self.run_id, "region": "us-east-1"},
                "rabbit": {"host": "127.0.0.1", "port": amqp_port,
                           "managementEndpoint": f"http://127.0.0.1:{management_port}",
                           "username": username, "password": password, "virtualHost": virtual_host},
            }
            # Deliberate allowlist: no model/provider/database credentials from the caller.
            environment = {key: os.environ[key] for key in ("SystemRoot", "WINDIR", "COMSPEC", "PATHEXT") if key in os.environ}
            environment.update({
                "PATH": str(erlang / "bin") + os.pathsep + str(Path(os.environ["SystemRoot"]) / "System32"),
                "HOME": str(self.directory / "profile"), "USERPROFILE": str(self.directory / "profile"),
                "TEMP": str(self.directory / "tmp"), "TMP": str(self.directory / "tmp"),
                "ERLANG_HOME": str(erlang), "ERL_EPMD_PORT": str(epmd_port),
                "ERL_EPMD_ADDRESS": "127.0.0.1",
            })
            self._environment = environment
            minio_environment = environment | {
                "MINIO_ROOT_USER": access_key, "MINIO_ROOT_PASSWORD": secret_key,
                "MINIO_BROWSER": "off", "MINIO_UPDATE": "off",
            }
            self._spawn("minio", [str(minio), "server", str(objects),
                                  "--address", f"127.0.0.1:{s3_port}",
                                  "--console-address", f"127.0.0.1:{console_port}", "--quiet"], minio_environment)
            self._spawn("epmd", [str(epmd), "-port", str(epmd_port), "-address", "127.0.0.1"], environment)
            self._node = "knowledge_" + self.run_id[:12] + "@localhost"
            rabbit_conf = self.directory / "rabbit/rabbitmq.conf"
            rabbit_conf.write_text("\n".join([
                f"listeners.tcp.1 = 127.0.0.1:{amqp_port}",
                "management.tcp.ip = 127.0.0.1", f"management.tcp.port = {management_port}",
                f"default_user = {username}", f"default_pass = {password}",
                f"default_vhost = {virtual_host}", "default_user_tags.administrator = true",
                "loopback_users.guest = true", "log.console = true", "log.console.level = warning",
                "log.file = false", "disk_free_limit.absolute = 100MB", "vm_memory_high_watermark.absolute = 256MB",
                "cluster_partition_handling = pause_minority", "",]), encoding="utf-8")
            enabled = self.directory / "rabbit/enabled_plugins"
            enabled.write_text("[rabbitmq_management].\n", encoding="ascii")
            environment.update({
                "RABBITMQ_BASE": str(self.directory / "rabbit"),
                "RABBITMQ_MNESIA_BASE": str(self.directory / "rabbit/db"),
                "RABBITMQ_MNESIA_DIR": str(self.directory / "rabbit/db/node"),
                "RABBITMQ_LOG_BASE": str(self.directory / "rabbit/log"),
                "RABBITMQ_PLUGINS_EXPAND_DIR": str(self.directory / "rabbit/plugins"),
                "RABBITMQ_ENABLED_PLUGINS_FILE": str(enabled),
                "RABBITMQ_CONFIG_FILE": str(rabbit_conf),
                "RABBITMQ_CONF_ENV_FILE": str(self.directory / "rabbit/absent-env-conf.bat"),
                "RABBITMQ_NODENAME": self._node, "RABBITMQ_DIST_PORT": str(distribution_port),
                # The upstream Windows batch checks presence, not a boolean:
                # any nonempty value enables werl.exe and an interactive shell.
                "RABBITMQ_ERLANG_COOKIE": cookie, "RABBITMQ_ALLOW_INPUT": "",
                "RABBITMQ_SERVER_ADDITIONAL_ERL_ARGS": "-start_epmd false -kernel inet_dist_use_interface {127,0,0,1} +S 2:2",
            })
            command = [str(Path(os.environ["SystemRoot"]) / "System32/cmd.exe"), "/d", "/c",
                       str(self._rabbit_home / "sbin/rabbitmq-server.bat")]
            self._spawn("rabbitmq", command, environment)
            deadline = time.monotonic() + 120
            while time.monotonic() < deadline:
                if any(process.poll() is not None for process in self._processes):
                    raise RuntimeError("A native dependency exited before readiness; see redacted logs.")
                try:
                    with urlopen(self.configuration["minio"]["endpoint"] + "/minio/health/ready", timeout=2) as response:
                        minio_ready = response.status == 200
                    overview = self.rabbit_request("GET", "/api/overview")
                    if minio_ready and overview.get("rabbitmq_version") == locked["packages"]["rabbitmq"]["version"]:
                        self.phase = "create bucket"
                        self.s3_request("PUT", "/" + self.configuration["minio"]["bucket"])
                        self.phase = "ready"
                        return self
                except (HTTPError, URLError, TimeoutError, ConnectionError, OSError):
                    time.sleep(0.25)
            raise RuntimeError("Native knowledge dependencies did not become ready in time.")
        except BaseException:
            self.close()
            raise

    def rabbit_request(self, method: str, path: str, body: dict | None = None):
        rabbit = self.configuration["rabbit"]
        authentication = base64.b64encode((rabbit["username"] + ":" + rabbit["password"]).encode()).decode()
        request = Request(rabbit["managementEndpoint"] + path,
                          data=json.dumps(body).encode() if body is not None else None,
                          headers={"Authorization": "Basic " + authentication, "Content-Type": "application/json"}, method=method)
        with urlopen(request, timeout=5) as response:
            data = response.read(1024 * 1024)
            return json.loads(data) if data else None

    def s3_request(self, method: str, path: str, body: bytes = b"") -> bytes:
        storage = self.configuration["minio"]
        endpoint = storage["endpoint"]
        host = urlsplit(endpoint).netloc
        now = datetime.now(timezone.utc)
        timestamp = now.strftime("%Y%m%dT%H%M%SZ")
        date = now.strftime("%Y%m%d")
        payload_hash = hashlib.sha256(body).hexdigest()
        signed_headers = "host;x-amz-content-sha256;x-amz-date"
        headers = f"host:{host}\nx-amz-content-sha256:{payload_hash}\nx-amz-date:{timestamp}\n"
        canonical = "\n".join((method, quote(path, safe="/-_.~"), "", headers, signed_headers, payload_hash))
        scope = date + "/us-east-1/s3/aws4_request"
        to_sign = "AWS4-HMAC-SHA256\n" + timestamp + "\n" + scope + "\n" + hashlib.sha256(canonical.encode()).hexdigest()
        key = ("AWS4" + storage["secretKey"]).encode()
        for value in (date, "us-east-1", "s3", "aws4_request"):
            key = hmac.new(key, value.encode(), hashlib.sha256).digest()
        signature = hmac.new(key, to_sign.encode(), hashlib.sha256).hexdigest()
        authorization = f"AWS4-HMAC-SHA256 Credential={storage['accessKey']}/{scope}, SignedHeaders={signed_headers}, Signature={signature}"
        request = Request(endpoint + quote(path, safe="/-_.~"), data=body if method in ("PUT", "POST") else None,
                          headers={"Host": host, "X-Amz-Date": timestamp, "X-Amz-Content-Sha256": payload_hash,
                                   "Authorization": authorization}, method=method)
        with urlopen(request, timeout=10) as response:
            return response.read(1024 * 1024)

    def smoke(self) -> dict[str, bool]:
        body = b"native knowledge acceptance: synthetic bytes\n"
        path = "/" + self.configuration["minio"]["bucket"] + "/smoke.txt"
        self.phase = "object upload"
        self.s3_request("PUT", path, body)
        self.phase = "object download"
        if self.s3_request("GET", path) != body:
            raise RuntimeError("MinIO object round-trip did not preserve bytes.")
        self.phase = "object delete"
        self.s3_request("DELETE", path)
        rabbit = self.configuration["rabbit"]
        vhost = quote(rabbit["virtualHost"], safe="")
        queue = "native-smoke"
        self.phase = "queue declare"
        self.rabbit_request("PUT", f"/api/queues/{vhost}/{queue}", {"durable": True, "auto_delete": False, "arguments": {}})
        self.phase = "queue publish"
        published = self.rabbit_request("POST", f"/api/exchanges/{vhost}/amq.default/publish",
                                        {"properties": {}, "routing_key": queue, "payload": "synthetic-message", "payload_encoding": "string"})
        self.phase = "queue consume"
        messages = self.rabbit_request("POST", f"/api/queues/{vhost}/{queue}/get",
                                       {"count": 1, "ackmode": "ack_requeue_false", "encoding": "auto", "truncate": 128})
        if published != {"routed": True} or len(messages) != 1 or messages[0].get("payload") != "synthetic-message":
            raise RuntimeError("RabbitMQ publish/consume did not preserve the synthetic message.")
        self.phase = "queue delete"
        self.rabbit_request("DELETE", f"/api/queues/{vhost}/{queue}")
        self.phase = "AMQP handshake"
        with socket.create_connection((rabbit["host"], rabbit["port"]), timeout=5) as connection:
            connection.sendall(b"AMQP\x00\x00\x09\x01")
            frame = connection.recv(11)
            if len(frame) < 11 or frame[0] != 1 or frame[7:11] != b"\x00\x0a\x00\x0a":
                raise RuntimeError("RabbitMQ did not answer the AMQP 0-9-1 handshake.")
        return {"minioObjectRoundTrip": True, "rabbitQueueRoundTrip": True, "amqpHandshake": True}

    def close(self) -> None:
        if not self._started:
            return
        errors = []
        if self._rabbit_home and self._node:
            try:
                # No credential on the command line; the cookie stays in this child environment.
                command = [str(Path(os.environ["SystemRoot"]) / "System32/cmd.exe"), "/d", "/c",
                           str(self._rabbit_home / "sbin/rabbitmqctl.bat"), "-n", self._node, "stop"]
                process = self._spawn("rabbitmq-stop", command, self._environment)
                try:
                    process.wait(timeout=20)
                except subprocess.TimeoutExpired:
                    self._kill(process)
            except (OSError, subprocess.SubprocessError):
                pass  # The owned process-tree fallback below is authoritative.
        for process in reversed(self._processes):
            try:
                self._kill(process)
            except (OSError, subprocess.SubprocessError):
                errors.append("process")
        for reader in self._readers:
            reader.join(timeout=5)
            if reader.is_alive():
                errors.append("log reader")
        deadline = time.monotonic() + 10
        while any(_listening(port) for port in self._ports) and time.monotonic() < deadline:
            time.sleep(0.1)
        if any(_listening(port) for port in self._ports):
            errors.append("port")
        if not errors:
            target = _local(self.directory, self.parent)
            if not (target / MARKER).is_file() or (target / MARKER).read_text(encoding="ascii") != self.run_id:
                raise RuntimeError("Native knowledge cleanup ownership marker changed.")
            external = None
            if self._external_objects and self._external_parent:
                external = _local(self._external_objects, self._external_parent)
                if not (external / MARKER).is_file() or (external / MARKER).read_text(encoding="ascii") != self.run_id:
                    raise RuntimeError("External object cleanup ownership marker changed.")
            shutil.rmtree(target)
            if external:
                shutil.rmtree(external)
            self.cleanup_ok = True
        self._started = False
        if errors:
            raise RuntimeError("Native knowledge cleanup could not confirm all owned processes stopped.")

    def __exit__(self, exc_type, exc_value, traceback) -> None:
        self.close()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--smoke", action="store_true", required=True)
    args = parser.parse_args()
    run = ROOT / ".local-data/knowledge-tests" / ("run-" + uuid.uuid4().hex)
    services = NativeKnowledgeServices(run)
    result = {}
    try:
        with services:
            result.update(services.smoke())
        result["cleanup"] = services.cleanup_ok and not services.directory.exists()
        (services.artifact_dir / "result.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
        run.rmdir()
        print(json.dumps(result))
        return 0
    except Exception as error:
        # Never print exception arguments from HTTP/process libraries: they may hold credentials.
        status = "; HTTP " + str(error.code) if isinstance(error, HTTPError) else ""
        print("Native knowledge smoke failed (" + type(error).__name__ + status + ") at " + services.phase + "; inspect its redacted logs.")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
