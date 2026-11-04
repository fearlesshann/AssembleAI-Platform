# Knowledge document parser

Private FastAPI service for the platform's ingestion worker. This service parses bytes already authorized and retrieved by the platform; it does not grant tenant access, persist documents, retrieve URLs, generate embeddings, or call a model.

## Install and start

Python 3.13 and locked dependencies:

```powershell
cd apps/knowledge/backend
uv sync --locked --dev
# Configure ASSEMBLEAI_KNOWLEDGE_PARSER_KEY in this process environment.
.venv/Scripts/python.exe -m uvicorn knowledge_backend.main:app --host 127.0.0.1 --port 8093 --no-access-log
```

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
- Each request runs in a fresh subprocess, with a 15-second wall-clock timeout and 512 MiB process memory limit. Windows uses a Job Object and Linux uses resource limits. Failure to establish limits fails closed. Linux also has a 12-second CPU cap. The child receives no parser/platform/provider credentials and communicates through pipes, without temporary document files.
- Each service instance admits at most two active requests, with no queue; reading a request body has a 3-second limit. Disconnect and cancellation terminate and reap the child. These are per-instance limits; deployment concurrency / process isolation remain operator responsibilities.

`parse_document` is a deterministic library function for tests and trusted bounded callers. Untrusted bytes must use the HTTP adapter / `parse_isolated`, which enforce process memory and time limits. See the [pypdf extraction documentation](https://pypdf.readthedocs.io/en/stable/user/extract-text.html) for text-layer and memory limitations.

## Verification

```powershell
.venv/Scripts/python.exe -m pytest -q
```

Tests generate actual PDF and DOCX bytes locally and cover all four formats, citation locations, overlap, unsafe names, size / Unicode / ZIP / XML boundaries, encrypted and empty PDFs, hidden/deleted Word content, private HTTP authentication, missing configuration, and actual subprocess completion / timeout / cancellation. The repository's sample test environment also includes the pinned parser dependency and knowledge package path for unified Core regression.
