"""Offline, digest-pinned E5 CPU embeddings. Never imported by the parser child."""
import asyncio
import hashlib
from pathlib import Path

import anyio

PROFILE = "e5-small-dd476dd0-384-v1"
DIMENSIONS = 384
FILES = {
    "model_qint8_avx512_vnni.onnx": (118346824, "dd476dd0c2514e9b9be83aeb3853fac0763e0bdf4a71645407587d77c48a2d88"),
    "tokenizer.json": (17082730, "0b44a9d7b51c3c62626640cda0e2c2f70fdacdc25bbbd68038369d14ebdf4c39"),
}


class EmbeddingError(Exception):
    def __init__(self, code):
        self.code = code
        super().__init__(code)


def text(value, limit):
    if not isinstance(value, str) or not 1 <= len(value) <= limit or not value.strip():
        return False
    try:
        value.encode("utf-8")
        return not any(ord(c) < 32 and c not in "\n\r\t" or 127 <= ord(c) <= 159 for c in value)
    except UnicodeError:
        return False


def validate_chunks(value):
    if (not isinstance(value, dict) or value.keys() != {"chunks"} or not isinstance(value["chunks"], list)
            or not 1 <= len(value["chunks"]) <= 8):
        raise EmbeddingError("invalid_embedding_request")
    seen = set()
    for chunk in value["chunks"]:
        if (not isinstance(chunk, dict) or chunk.keys() != {"index", "text", "locator"}
                or type(chunk["index"]) is not int or not 0 <= chunk["index"] < 2000 or chunk["index"] in seen
                or not text(chunk["text"], 1200) or not text(chunk["locator"], 200)):
            raise EmbeddingError("invalid_embedding_request")
        seen.add(chunk["index"])
    return value["chunks"]


def validate_query(value):
    if not isinstance(value, dict) or value.keys() != {"query"} or not text(value["query"], 1000):
        raise EmbeddingError("invalid_embedding_request")
    return value["query"]


class EmbeddingEngine:
    def __init__(self, directory: Path):
        try:
            for name, (size, digest) in FILES.items():
                path = directory / name
                if path.stat().st_size != size:
                    raise ValueError()
                with path.open("rb") as source:
                    if hashlib.file_digest(source, "sha256").hexdigest() != digest:
                        raise ValueError()
        except (OSError, ValueError):
            raise ValueError("Local files do not match the fixed model profile") from None
        # These heavy dependencies remain outside the isolated document parser.
        import numpy as np
        import onnxruntime as ort
        from tokenizers import Tokenizer
        self.np, self.ort = np, ort
        self.tokenizer = Tokenizer.from_file(str(directory / "tokenizer.json"))
        self.tokenizer.no_truncation()
        self.tokenizer.no_padding()
        options = ort.SessionOptions()
        options.intra_op_num_threads = 2
        options.inter_op_num_threads = 1
        options.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
        options.log_severity_level = 4
        self.session = ort.InferenceSession(str(directory / "model_qint8_avx512_vnni.onnx"),
                                           sess_options=options, providers=["CPUExecutionProvider"])
        self.lock = asyncio.Lock()

    def segment(self, chunks):
        result = []
        for chunk in chunks:
            remaining = chunk["text"]
            segment = 0
            while remaining:
                if len(self.tokenizer.encode("passage: " + remaining).ids) <= 512:
                    count = len(remaining)
                else:
                    low, high = 1, len(remaining)
                    # Re-encode each exact candidate: no truncation or decoding
                    # normalized tokens back into fabricated source text.
                    while low < high:
                        middle = (low + high + 1) // 2
                        if len(self.tokenizer.encode("passage: " + remaining[:middle]).ids) <= 512:
                            low = middle
                        else:
                            high = middle - 1
                    count = low
                part = remaining[:count]
                if len(self.tokenizer.encode("passage: " + part).ids) > 512:
                    raise EmbeddingError("embedding_input_too_large")
                result.append({"chunkIndex": chunk["index"], "segmentIndex": segment,
                               "text": part, "locator": chunk["locator"]})
                if len(result) > 64:
                    raise EmbeddingError("embedding_input_too_large")
                remaining = remaining[count:]
                segment += 1
        return result

    async def _vector(self, value, prefix):
        encoding = self.tokenizer.encode(prefix + value)
        if len(encoding.ids) > 512:
            raise EmbeddingError("embedding_input_too_large")
        np = self.np
        inputs = {"input_ids": np.asarray([encoding.ids], dtype=np.int64),
                  "attention_mask": np.asarray([encoding.attention_mask], dtype=np.int64),
                  "token_type_ids": np.asarray([encoding.type_ids], dtype=np.int64)}
        async with self.lock:
            options = self.ort.RunOptions()
            options.log_severity_level = 4
            worker = asyncio.create_task(asyncio.to_thread(self.session.run, ["last_hidden_state"], inputs, options))
            try:
                hidden = (await asyncio.shield(worker))[0]
            except asyncio.CancelledError:
                options.terminate = True
                # Hold the inference lock until native work has acknowledged
                # termination. An abandoned Python thread is not cancellation.
                with anyio.CancelScope(shield=True):
                    await asyncio.gather(worker, return_exceptions=True)
                raise
            except Exception:
                raise EmbeddingError("embedding_unavailable") from None
        mask = inputs["attention_mask"][:, :, None]
        pooled = (hidden * mask).sum(axis=1, dtype=np.float64) / mask.sum(axis=1)
        vector = pooled[0]
        norm = np.linalg.norm(vector)
        if vector.shape != (DIMENSIONS,) or not np.isfinite(vector).all() or not np.isfinite(norm) or norm <= 0:
            raise EmbeddingError("embedding_unavailable")
        return (vector / norm).tolist()

    async def query(self, query):
        validate_query({"query": query})
        return {"profile": PROFILE, "dimensions": DIMENSIONS, "embedding": await self._vector(query, "query: ")}

    async def index(self, chunks):
        validate_chunks({"chunks": chunks})
        segments = self.segment(chunks)
        for segment in segments:
            segment["embedding"] = await self._vector(segment["text"], "passage: ")
        return {"profile": PROFILE, "dimensions": DIMENSIONS, "segments": segments}
