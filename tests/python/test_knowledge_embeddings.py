"""Private embedding boundary and actual fixed CPU model acceptance."""
import asyncio
import os
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from knowledge_backend.main import create_app
from knowledge_backend.documents import parse_document

KEY = "test-private-knowledge-parser-key"
PROFILE = "e5-small-dd476dd0-384-v1"


def test_embedding_endpoints_fail_closed_without_explicit_model(monkeypatch):
    monkeypatch.setenv("ASSEMBLEAI_KNOWLEDGE_PARSER_KEY", KEY)
    monkeypatch.delenv("ASSEMBLEAI_KNOWLEDGE_MODEL_DIRECTORY", raising=False)
    with TestClient(create_app()) as client:
        for path, body in [("/internal/index", {"chunks": [{"index": 0, "text": "生产说明", "locator": "page:1"}]}),
                           ("/internal/embed-query", {"query": "设备怎样维护？"})]:
            assert client.post(path, json=body).status_code == 401
            response = client.post(path, json=body, headers={"Authorization": "Bearer " + KEY})
            assert response.status_code == 503
            assert response.json() == {"code": "embedding_unavailable"}


def test_parser_overlap_scalar_and_utf16_budget_for_maximum_emoji_document():
    parsed = parse_document(("😀" * 1_000_000).encode(), "emoji.txt")
    total = sum(len(chunk.text) for chunk in parsed.chunks)
    assert 1_000_000 < total <= 1_200_000
    assert sum(len(chunk.text.encode("utf-16-le")) // 2 for chunk in parsed.chunks) <= 2_400_000
    assert parsed.chunks[0].text + "".join(chunk.text[150:] for chunk in parsed.chunks[1:]) == "😀" * 1_000_000


@pytest.mark.parametrize("control", ["\x7f", "\x85", "\x9f"])
def test_parser_rejects_controls_that_indexing_cannot_accept(control):
    from knowledge_backend.documents import DocumentError
    with pytest.raises(DocumentError, match="invalid_document"):
        parse_document(("设备"+control+"维护").encode(), "controls.txt")


def test_parser_omits_whitespace_windows_and_preserves_source_line_locations():
    parsed = parse_document(("a"+"\n"*2500+"b").encode(), "sparse.txt")
    assert all(chunk.text.strip() for chunk in parsed.chunks)
    assert [chunk.index for chunk in parsed.chunks] == list(range(len(parsed.chunks)))
    assert len(parsed.chunks) == 2 and parsed.chunks[0].text.startswith("a") and parsed.chunks[-1].text.endswith("b")
    assert parsed.chunks[-1].locator.endswith("-2501")


@pytest.fixture(scope="module")
def engine():
    from knowledge_backend.embeddings import EmbeddingEngine
    directory = os.getenv("ASSEMBLEAI_KNOWLEDGE_TEST_MODEL_DIRECTORY")
    if not directory:
        pytest.skip("Set ASSEMBLEAI_KNOWLEDGE_TEST_MODEL_DIRECTORY for fixed local CPU model acceptance")
    return EmbeddingEngine(Path(directory))


def test_real_cpu_vectors_rank_chinese_meaning_and_are_unit_vectors(engine):
    import math
    import time
    async def exercise():
        started = time.monotonic()
        query = await engine.query("机器轴承发热时应该怎么处理？")
        passages = await engine.index([
            {"index": 3, "text": "设备轴承温度过高，应立即停机检查润滑油和冷却系统。", "locator": "page:2"},
            {"index": 4, "text": "员工请假需在考勤系统中提交申请，经部门负责人审批。", "locator": "page:3"},
            {"index": 5, "text": "仓库采购办公用品需每月汇总费用并核对发票。", "locator": "page:4"}])
        assert query["profile"] == PROFILE and query["dimensions"] == 384
        vectors = [query["embedding"]] + [item["embedding"] for item in passages["segments"]]
        assert all(len(v) == 384 and all(math.isfinite(n) for n in v) and abs(sum(n*n for n in v)-1)<0.002 for v in vectors)
        scores = [sum(a*b for a,b in zip(vectors[0], v)) for v in vectors[1:]]
        assert scores[0] > max(scores[1:]) + 0.02
        print(f"CPU semantic scores={scores}, elapsed={time.monotonic()-started:.3f}s")
    asyncio.run(exercise())


def test_token_segments_keep_every_character_and_locator(engine):
    text = "润滑维护甲乙丙丁戊己庚辛壬癸😀 " * 60
    chunks = engine.segment([{"index": 7, "text": text, "locator": "paragraph:9"}])
    assert len(chunks) > 1 and "".join(c["text"] for c in chunks) == text
    assert [c["segmentIndex"] for c in chunks] == list(range(len(chunks)))
    assert all(c["chunkIndex"] == 7 and c["locator"] == "paragraph:9" and c["text"] in text for c in chunks)
    assert all(len(engine.tokenizer.encode("passage: " + c["text"]).ids) <= 512 for c in chunks)


def test_token_segments_preserve_sparse_whitespace_without_empty_segments(engine):
    text = "a" + "\n"*400 + "甲乙丙丁戊己庚辛壬癸"*60 + " "*150 + "b"
    parts = engine.segment([{"index":0,"text":text,"locator":"lines:1-401"}])
    assert len(parts) > 1 and all(p["text"].strip() for p in parts)
    assert "".join(p["text"] for p in parts) == text
    assert all(len(engine.tokenizer.encode("passage: "+p["text"]).ids)<=512 for p in parts)


def test_query_token_overflow_is_rejected_without_inference(engine):
    from knowledge_backend.embeddings import EmbeddingError
    with pytest.raises(EmbeddingError, match="embedding_input_too_large"):
        asyncio.run(engine.query("甲乙丙丁戊己庚辛壬癸" * 100))


def test_model_digest_mismatch_fails_startup(tmp_path):
    from knowledge_backend.embeddings import EmbeddingEngine
    (tmp_path / "model_qint8_avx512_vnni.onnx").write_bytes(b"untrusted-model")
    (tmp_path / "tokenizer.json").write_bytes(b"{}")
    with pytest.raises(ValueError, match="fixed model"):
        EmbeddingEngine(tmp_path)


def test_platform_locator_bound_is_accepted_without_modification():
    from knowledge_backend.embeddings import EmbeddingError, validate_chunks
    item = {"index":0,"text":"x","locator":"x"*200}
    assert validate_chunks({"chunks":[item]}) == [item]
    with pytest.raises(EmbeddingError):
        validate_chunks({"chunks":[{**item,"locator":"x"*201}]})


@pytest.mark.parametrize("path,body", [
    ("/internal/index", {"chunks": []}),
    ("/internal/index", {"chunks": [{"index":0,"text":"x","locator":"page:1"}]*9}),
    ("/internal/index", {"chunks": [{"index":0,"text":"x","locator":"page:1"}]*2}),
    ("/internal/index", {"chunks": [{"index":True,"text":"x","locator":"page:1"}]}),
    ("/internal/index", {"chunks": [{"index":0,"text":"x"*1201,"locator":"page:1"}]}),
    ("/internal/index", {"chunks": [{"index":0,"text":"x","locator":"page:1","embedding":[1]}]}),
    ("/internal/embed-query", {"query":"x","profile":"other"}),
    ("/internal/embed-query", {"query":"x"*1001}),
    ("/internal/embed-query", {"query":"\ud800"})])
def test_private_invalid_requests_never_run_inference(monkeypatch, path, body):
    import json
    class MustNotRun:
        async def index(self, _):
            raise AssertionError("invalid request reached inference")
        query = index
    monkeypatch.setenv("ASSEMBLEAI_KNOWLEDGE_PARSER_KEY", KEY)
    monkeypatch.delenv("ASSEMBLEAI_KNOWLEDGE_MODEL_DIRECTORY", raising=False)
    app = create_app()
    with TestClient(app) as browser:
        app.state.embeddings = MustNotRun()
        response = browser.post(path, headers={"Authorization":"Bearer "+KEY,"Content-Type":"application/json"}, content=json.dumps(body))
    assert response.status_code == 400 and response.json() == {"code":"invalid_embedding_request"}


def test_private_body_media_duplicate_and_size_boundaries(monkeypatch):
    monkeypatch.setenv("ASSEMBLEAI_KNOWLEDGE_PARSER_KEY", KEY)
    monkeypatch.delenv("ASSEMBLEAI_KNOWLEDGE_MODEL_DIRECTORY", raising=False)
    app = create_app()
    with TestClient(app) as browser:
        app.state.embeddings = object()
        headers = {"Authorization":"Bearer "+KEY,"Content-Type":"application/json"}
        for body, extra, status in [(b'{"query":"x","query":"y"}', {},400), (b"x"*(1024*1024+1), {},413),
                                    (b'{}', {"Content-Encoding":"gzip"},400), (b'{}', {"Content-Type":"text/plain"},400)]:
            assert browser.post("/internal/embed-query", content=body, headers=headers|extra).status_code == status


def test_actual_native_inference_cancellation_releases_single_cpu_slot(engine):
    import threading
    real = engine.session
    started, finished = threading.Event(), threading.Event()
    observed = {}
    class RecordingNativeSession:
        def run(self, outputs, inputs, options):
            observed["options"] = options
            started.set()
            try:
                return real.run(outputs, inputs, options)
            except Exception as failure:
                observed["terminated"] = "terminate" in str(failure).lower()
                raise
            finally:
                finished.set()
    async def exercise():
        engine.session = RecordingNativeSession()
        try:
            task = asyncio.create_task(engine.query("甲乙丙丁戊己庚辛壬癸" * 40))
            while not started.is_set():
                await asyncio.sleep(0.001)
            await asyncio.sleep(0.01)
            assert not finished.is_set(), "fixture must cancel while real native computation is active"
            task.cancel()
            with pytest.raises(asyncio.CancelledError):
                await task
            assert observed["options"].terminate is True
            assert finished.is_set() and observed.get("terminated") is True and not engine.lock.locked()
        finally:
            engine.session = real
        assert len((await engine.query("设备维护"))["embedding"]) == 384
    asyncio.run(exercise())


def test_parallel_queries_never_overlap_native_inference(engine):
    real = engine.session
    observed = {"active":0,"maximum":0}
    class RecordingNativeSession:
        def run(self, outputs, inputs, options):
            observed["active"] += 1
            observed["maximum"] = max(observed["maximum"], observed["active"])
            try:
                return real.run(outputs, inputs, options)
            finally:
                observed["active"] -= 1
    async def exercise():
        engine.session = RecordingNativeSession()
        try:
            values = await asyncio.gather(*(engine.query("设备维护"+str(i)) for i in range(3)))
            assert all(len(v["embedding"]) == 384 for v in values)
            assert observed == {"active":0,"maximum":1}
        finally:
            engine.session = real
    asyncio.run(exercise())
