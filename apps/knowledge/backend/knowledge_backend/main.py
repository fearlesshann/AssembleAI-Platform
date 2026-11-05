"""Private HTTP adapter. The platform owns document authorization and persistence."""
import asyncio
from contextlib import asynccontextmanager
import hmac
import json
import os
from pathlib import Path
import subprocess
import sys

from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse

from .documents import MAX_BYTES, DocumentError, document_kind

HEADERS = {"Cache-Control": "no-store", "X-Content-Type-Options": "nosniff"}
STATUS = {"invalid_document": 400, "document_too_large": 413, "parser_unavailable": 503, "parser_timeout": 504}


def key_from_environment():
    value = os.environ.get("ASSEMBLEAI_KNOWLEDGE_PARSER_KEY", "")
    return value if 16 <= len(value) <= 256 and value.isascii() and not any(ord(c) <= 32 or ord(c) >= 127 for c in value) else None


async def parse_isolated(content: bytes, filename: str, *, timeout=15):
    # Child processes receive no platform/provider credentials or user-controlled environment.
    environment = {name: os.environ[name] for name in ("SystemRoot", "WINDIR", "PATH", "LANG") if name in os.environ}
    environment["PYTHONIOENCODING"] = "utf-8"
    process = await asyncio.create_subprocess_exec(
        sys.executable, "-m", "knowledge_backend.worker", filename,
        cwd=Path(__file__).resolve().parent.parent, env=environment,
        stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.DEVNULL,
        creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
    try:
        async with asyncio.timeout(timeout):
            output, _ = await process.communicate(content)
        if process.returncode != 0 or len(output) > 8 * 1024 * 1024:
            raise DocumentError("document_too_complex")
        value = json.loads(output)
        if isinstance(value, dict) and "code" in value:
            raise DocumentError(value["code"])
        return value
    except TimeoutError:
        raise DocumentError("parser_timeout") from None
    except (json.JSONDecodeError, UnicodeError):
        raise DocumentError("parser_unavailable") from None
    finally:
        if process.returncode is None:
            process.kill()
        await process.wait()


def create_app():
    @asynccontextmanager
    async def lifespan(app):
        directory = os.environ.get("ASSEMBLEAI_KNOWLEDGE_MODEL_DIRECTORY")
        if directory:
            from .embeddings import EmbeddingEngine
            app.state.embeddings = EmbeddingEngine(Path(directory))
        yield
        app.state.embeddings = None

    app = FastAPI(docs_url=None, redoc_url=None, openapi_url=None, lifespan=lifespan)
    app.state.active = 0
    app.state.embeddings = None
    app.state.embedding_active = 0

    def response(code, status=None):
        return JSONResponse({"code": code}, status_code=status or STATUS.get(code, 422), headers=HEADERS)

    @app.get("/health/ready")
    async def ready():
        if key_from_environment() is None:
            return response("parser_unavailable")
        return JSONResponse({"status": "ready", "service": "knowledge-parser", "version": "0.1.0"}, headers=HEADERS)

    @app.post("/internal/parse")
    async def parse(request: Request):
        key = key_from_environment()
        if key is None:
            return response("parser_unavailable")
        auth = request.headers.getlist("authorization")
        if len(auth) != 1 or not hmac.compare_digest(auth[0].encode(), ("Bearer " + key).encode()):
            return response("unauthorized", 401)
        if (list(request.query_params.keys()) != ["filename"] or len(request.query_params.getlist("filename")) != 1
                or request.headers.getlist("content-type") != ["application/octet-stream"] or request.headers.getlist("content-encoding")):
            return response("invalid_document")
        if app.state.active >= 2:
            return response("parser_busy", 429)
        app.state.active += 1
        task = None
        try:
            filename = request.query_params["filename"]
            document_kind(filename)
            body = bytearray()
            async with asyncio.timeout(3):
                async for chunk in request.stream():
                    if len(body) + len(chunk) > MAX_BYTES:
                        return response("document_too_large")
                    body.extend(chunk)
            task = asyncio.create_task(parse_isolated(bytes(body), filename))
            while not task.done():
                await asyncio.wait({task}, timeout=0.1)
                if await request.is_disconnected():
                    task.cancel()
                    return response("request_cancelled", 499)
            result = await task
            return JSONResponse(result, headers=HEADERS)
        except DocumentError as failure:
            return response(failure.code)
        except TimeoutError:
            return response("parser_timeout")
        except OSError:
            return response("parser_unavailable")
        finally:
            if task is not None:
                if not task.done():
                    task.cancel()
                try:
                    await task
                except (asyncio.CancelledError, DocumentError, OSError):
                    pass
            app.state.active -= 1

    async def embedding(request, indexing):
        from .authentication import JSON_UTF8, strict_json
        from .embeddings import EmbeddingError, validate_chunks, validate_query
        from .models import while_connected
        key = key_from_environment()
        if key is None:
            return response("parser_unavailable")
        auth = request.headers.getlist("authorization")
        if len(auth) != 1 or not hmac.compare_digest(auth[0].encode(), ("Bearer " + key).encode()):
            return response("unauthorized", 401)
        if app.state.embeddings is None:
            return response("embedding_unavailable", 503)
        types = request.headers.getlist("content-type")
        if request.url.query or len(types) != 1 or JSON_UTF8.fullmatch(types[0]) is None or request.headers.getlist("content-encoding"):
            return response("invalid_embedding_request", 400)
        if app.state.embedding_active >= 2:
            return response("embedding_busy", 429)
        app.state.embedding_active += 1
        try:
            body = bytearray()
            async with asyncio.timeout(3):
                async for chunk in request.stream():
                    if len(body) + len(chunk) > 1024 * 1024:
                        return response("embedding_input_too_large", 413)
                    body.extend(chunk)
            value = strict_json(bytes(body))
            argument = validate_chunks(value) if indexing else validate_query(value)
            operation = app.state.embeddings.index(argument) if indexing else app.state.embeddings.query(argument)
            async with asyncio.timeout(25):
                result = await while_connected(request, operation)
            return JSONResponse(result, headers=HEADERS)
        except EmbeddingError as failure:
            return response(failure.code, 503 if failure.code == "embedding_unavailable" else 413 if failure.code == "embedding_input_too_large" else 400)
        except (ValueError, TypeError, RecursionError):
            return response("invalid_embedding_request", 400)
        except TimeoutError:
            return response("embedding_timeout", 504)
        finally:
            app.state.embedding_active -= 1

    @app.post("/internal/index")
    async def index(request: Request):
        return await embedding(request, True)

    @app.post("/internal/embed-query")
    async def embed_query(request: Request):
        return await embedding(request, False)

    return app


app = create_app()
