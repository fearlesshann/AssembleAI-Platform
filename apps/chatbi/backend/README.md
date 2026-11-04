# ChatBI backend 0.1.0

Independent FastAPI application. Its authentication and model client modules are
maintained copies of the versioned application template; this package never
imports the sample application at runtime.

From this directory:

```powershell
uv sync --locked --dev
uv run --locked uvicorn chatbi_backend.main:app --host 127.0.0.1 --port 8201
uv run --locked pytest
```

The isolated test configuration collects only `tests/backend/test_chatbi*.py`.
`uv.lock` contains the dependency resolution and hashes. The Windows snapshot
records the versions verified in this application's own environment. Existing
caches also support `uv lock --check --offline` and
`uv sync --locked --offline --dev`.

| Environment variable | Meaning |
| --- | --- |
| `ASSEMBLEAI_ENVIRONMENT` | `Production` by default; `Development` permits loopback HTTP callbacks. |
| `ASSEMBLEAI_VALIDATION_URL` | Fixed platform `/internal/calls/validate` URL. |
| `ASSEMBLEAI_SERVICE` | Registered application service, default `chatbi`. |
| `ASSEMBLEAI_SERVICE_VERSION` | Registered release version, default `0.1.0`. |
| `ASSEMBLEAI_MODEL_GATEWAY_ORIGIN` | Fixed platform HTTPS origin, without credentials, query or application path. |
| `ASSEMBLEAI_MODEL_ID` | Deployment-selected model alias. |
| `ASSEMBLEAI_MODEL_MAX_OUTPUT_TOKENS` | Model output limit, default `2048`. |
| `ASSEMBLEAI_CONNECTOR_GATEWAY_ORIGIN` | Optional separate fixed platform origin; defaults to the model gateway origin. |

`GET /datasets` returns authorized dataset aliases, names and field definitions.
`POST /ask` accepts only `{datasetId,question}`, at most 8 KiB and 1000 question
characters. The gateway supplies the original business credential and separate
model/data capabilities. The original credential is validated once; subsequent
callbacks use only those active capabilities.

Ask responses are SSE. The first `planning` stage is immediate, followed by
`querying` and `receiving`. Waiting calls emit a comment heartbeat every 10 seconds.
Rows are provisional string/null arrays in events at most 32 KiB. A `complete`
event contains the dataset, exact query text, column aliases, row count and
request IDs. Clients must discard provisional rows on error or missing complete.
No business rows are sent to the model. The execution budget is 150 seconds;
disconnects cancel pending callbacks, and data authorization/configuration
versions are checked after the query, before each row batch when the last check
is at least one second old, and before completion. Checks for one answer run
serially. Metadata and check callbacks allow five seconds including transport;
the platform's three-second server budget and the query budget are unchanged.
