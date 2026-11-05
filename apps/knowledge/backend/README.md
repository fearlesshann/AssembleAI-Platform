# Knowledge services

Two independent FastAPI entry points share a versioned package. `knowledge_backend.main:app` privately parses and embeds bytes authorized by the platform. `knowledge_backend.application:app` performs delegated retrieval, grounded model answers and citation rechecks. The platform owns document ACLs, storage, index publication and audit.

## Install and start

Python 3.13 and locked dependencies:

```powershell
cd apps/knowledge/backend
uv sync --locked --dev
# Configure ASSEMBLEAI_KNOWLEDGE_PARSER_KEY in this process environment.
.venv/Scripts/python.exe -m uvicorn knowledge_backend.main:app --host 127.0.0.1 --port 8093 --no-access-log
```

Install the fixed, official multilingual E5 model separately (135,429,554 bytes):

```powershell
# From the repository root; model files stay in the ignored local-data directory.
./tools/install-knowledge-model.ps1
$env:ASSEMBLEAI_KNOWLEDGE_MODEL_DIRECTORY = (Resolve-Path '.local-data/knowledge-model/e5-small-dd476dd0-384-v1').Path
```

The installer accepts `-Destination <absolute path>` for an existing cache or another drive. This workstation's acceptance uses `D:\cache\assembleai-models\e5-small-dd476dd0-384-v1`; no model file is moved or downloaded again when its digest already matches.

The private process only enables embeddings when this directory is explicitly configured. Startup checks the sizes and SHA256 of both files against profile `e5-small-dd476dd0-384-v1`; wrong or missing files fail startup. Model loading and inference never occur in the isolated parser child. ONNX Runtime 1.31.1 uses CPUExecutionProvider, two intra-op threads, sequential execution and one active native inference. Cancellation sets `RunOptions.terminate` and waits for native completion before releasing that slot. No model download occurs at runtime.

Start the public application separately:

```powershell
# In apps/knowledge/backend; configure these values in the process environment.
# ASSEMBLEAI_ENVIRONMENT=Development (only for loopback HTTP)
# ASSEMBLEAI_VALIDATION_URL=http://127.0.0.1:<platform-port>/internal/calls/validate
# ASSEMBLEAI_MODEL_GATEWAY_ORIGIN=http://127.0.0.1:<platform-port>
# ASSEMBLEAI_MODEL_ID=<platform-configured-alias>
.venv/Scripts/python.exe -m uvicorn knowledge_backend.application:app --host 127.0.0.1 --port 8094 --no-access-log
```

The public service defaults to `ASSEMBLEAI_SERVICE=knowledge`, `ASSEMBLEAI_SERVICE_VERSION=0.1.0` and `ASSEMBLEAI_MODEL_MAX_OUTPUT_TOKENS=2048`. `ASSEMBLEAI_KNOWLEDGE_GATEWAY_ORIGIN` optionally supplies a separate fixed origin; otherwise it uses the model gateway origin. Production origins require HTTPS without a path, query, fragment or credentials; Development additionally allows loopback HTTP. The public process requires no parser, provider, object-store, broker or database key. Copying these modules into this package is deliberate versioning: runtime imports never cross into sample or ChatBI.

The key is 16–256 printable ASCII characters without whitespace, provided to both this service and the platform's private parser client. Do not commit it or expose it to a browser. `GET /health/ready` returns 503 until the key is configured. Development binds loopback; deployments must protect the private network and use HTTPS at their trusted service boundary. Uvicorn access logging stays disabled because filenames are request query values.

## Protocol

`POST /internal/parse?filename=<URL-encoded basename>` requires exactly one `Authorization: Bearer <key>` header and `Content-Type: application/octet-stream`. The request body is the original document bytes, at most 5 MiB. Unknown / duplicate query parameters, unsafe names, body compression and other content types are rejected. Configuration and response bodies never include the key.

Success JSON:

```json
{"kind":"pdf","chunks":[{"index":0,"text":"Factory A production 300","locator":"page:1"}]}
```

`kind` is `txt`, `md`, `pdf` or `docx`. Indices are contiguous from zero. Locators are `page:<one-based page>`, `paragraph:<one-based document paragraph>` or `lines:<start>-<end>`. Text chunks are at most 1200 Unicode characters with a 150-character overlap within their original page / paragraph / text source. Chunks never combine different PDF pages or DOCX paragraphs. Newlines are normalized, surrounding whitespace is removed, and plain text source line positions retain leading blank lines.

Errors expose only `{code}`: 400 `invalid_document`, 413 `document_too_large`, 422 `unsupported_document` / `encrypted_document` / `ocr_required` / `empty_document` / `extracted_text_too_large` / `document_too_complex`, 503 `parser_unavailable`, or 504 `parser_timeout`. Authentication failure is 401; saturation is 429. All service responses prohibit caching. Request contents and library diagnostics are not logged.

## Supported content and limits

- TXT and Markdown must be strict UTF-8, with optional UTF-8 BOM. Binary controls and invalid Unicode are rejected. Markdown stays plain text; links, HTML and code are never executed or fetched.
- PDF uses pinned `pypdf==6.21.4`, at most 200 pages and 16 MiB of decoded page content streams. Encrypted PDFs are rejected. Only existing text layers are extracted; no OCR runs. A PDF with no extractable text returns `ocr_required`; image-only pages in a mixed PDF yield no text. Page order and actual page numbers are retained.
- Word means `.docx` OOXML, with UTF-8 XML. Legacy `.doc`, `.docm`, embedded objects and macros are rejected. Only body paragraphs (including table cells) are extracted; headers, footers, drawings and complex layout are outside this version. Deleted and directly hidden runs and field instructions are excluded. External resource relationships are rejected except hyperlinks, whose visible text is kept without following their URL. DTD / entity declarations, unsafe ZIP entries, duplicate entries and excessive expansion are rejected; no ZIP is extracted to disk.
- Extracted text is limited to 1,000,000 characters and 2,000 chunks. DOCX permits at most 2,048 ZIP entries, 16 MiB total expanded data and 8 MiB per parsed XML part. Large entries with expansion above 200:1 are rejected.
- Including overlap, emitted parser text stays below 1,200,000 Unicode scalar values / 2,400,000 UTF-16 code units, including an all-emoji source at the maximum input text limit.
- Each request runs in a fresh subprocess, with a 15-second wall-clock timeout and 512 MiB process memory limit. Windows uses a Job Object and Linux uses resource limits. Failure to establish limits fails closed. Linux also has a 12-second CPU cap. The child receives no parser/platform/provider credentials and communicates through pipes, without temporary document files.
- Each service instance admits at most two active requests, with no queue; reading a request body has a 3-second limit. Disconnect and cancellation terminate and reap the child. These are per-instance limits; deployment concurrency / process isolation remain operator responsibilities.

`parse_document` is a deterministic library function for tests and trusted bounded callers. Untrusted bytes must use the HTTP adapter / `parse_isolated`, which enforce process memory and time limits. See the [pypdf extraction documentation](https://pypdf.readthedocs.io/en/stable/user/extract-text.html) for text-layer and memory limitations.

## Embedding protocol

Both routes require the same single private Bearer key and strict UTF-8 JSON, without query parameters or Content-Encoding. Bodies are at most 1 MiB; two HTTP requests may be admitted while native inference is serialized. Each operation has a 25-second budget, leaving transport margin within the platform task's 30 seconds.

- `POST /internal/index {"chunks":[{"index":0,"text":"设备维护原文","locator":"page:1"}]}` accepts 1–8 unique parser chunk indices (0–1999), text up to 1200 characters and locator up to 200. Returns `{profile,dimensions:384,segments:[{chunkIndex,segmentIndex,text,locator,embedding}]}`. At most 64 segments; each original chunk is split into consecutive exact substrings without dropping text; segmentIndex starts at zero for each chunk and locator is unchanged.
- `POST /internal/embed-query {"query":"轴承过热如何处理？"}` accepts 1–1000 characters and returns `{profile,dimensions:384,embedding}`.

E5's English `passage: ` / `query: ` prefixes and special tokens count toward the 512-token limit. Oversized query tokens are rejected; passage segmentation re-encodes each exact source substring instead of silently truncating or decoding altered text. Masked mean pooling of last_hidden_state followed by L2 normalization yields 384 finite unit-vector values. Errors are fixed codes: `invalid_embedding_request` (400), `embedding_input_too_large` (413), `embedding_busy` (429), `embedding_unavailable` (503), `embedding_timeout` (504).

## Public question and reference protocol

`POST /ask {"question":"轴承过热如何处理？"}` requires platform-issued business JWT, X-Request-ID, X-Model-Delegation and the distinct X-Knowledge-Delegation. The JWT is remotely validated once; subsequent requests use only the respective active capability, with no cookies, browser identity or retries. Request JSON is strict, at most 8 KiB / 1000 question characters.

SSE begins immediately with `delta {kind:"stage",stage:"retrieving"}`; nonempty contexts progress to `answering` and then `verifying`. Empty retrieval skips `answering`. Waiting emits a comment heartbeat every ten seconds. Search / model / check / total budgets are 35 / 120 / 5 / 180 seconds. Both checks cover every retrieved context document, including documents the answer does not cite; checks are serial. Cancellation closes active HTTP, including a final check, and error events never include a complete event or upstream diagnostics.

The model receives only the current question and authorized snippets, explicitly treated as untrusted data. It must return exactly `{answer,citations:[{chunkId,quote}]}` or `{refusal:"insufficient_evidence"}`, with finishReason `stop`. Answers are at most 4000 characters. One to five unique citations must use retrieved chunk IDs and exact original substrings of at most 600 characters. Filename, document revision and locator come from platform metadata, never from the model. Empty retrieval avoids the model and returns a fixed insufficient-evidence answer.

The final `complete` event (at most 64 KiB) contains `{requestId,modelRequestId,searchRequestId,answer,refusal,citations,profile,authorizationVersion,documents}`. `modelRequestId` is null only when no model was called. `authorizationVersion` is a decimal string to preserve Int64 precision in a browser. Citations contain `{documentId,documentRevision,chunkId,fileName,locator,quote}`; documents contain unique `{documentId,revision}` for all contexts. Document IDs are canonical UUID strings; chunk IDs use `<document hex>-<revision>-<parent index>-<segment index>`.

`POST /references {profile,authorizationVersion,documents}` performs a fresh remote JWT validation and knowledge-capability check, returning `{valid:true}` only on success. It needs no model capability. The browser must clear retained answers when this check fails.

## Verification

```powershell
.venv/Scripts/python.exe -m pytest -q
# Include real offline CPU semantic, segmentation and native cancellation tests:
$env:ASSEMBLEAI_KNOWLEDGE_TEST_MODEL_DIRECTORY = $env:ASSEMBLEAI_KNOWLEDGE_MODEL_DIRECTORY
.venv/Scripts/python.exe -m pytest -q
```

Tests generate actual PDF and DOCX bytes locally and cover parser bounds, exact segmentation, Chinese semantic ranking, real native inference termination, strict query/answer/reference contracts, forged citations, current authorization, upstream failures, first SSE frame, heartbeat and real socket cancellation. Provider calls use local transport fixtures; real supplier acceptance belongs to the platform integration harness. `uv.lock` locks the complete resolution; `requirements.windows.lock` is a hash-pinned requirements export.
