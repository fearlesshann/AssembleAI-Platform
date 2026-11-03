# T9 Model Gateway implementation plan

Goal: Add tenant-authorized OpenAI-compatible text chat, preserving the existing portal.
Protocol selection: user confirmed OpenAI-compatible, including local inference servers.
Scope: server-owned provider/credential-reference catalog; PostgreSQL tenant configuration and audit; JSON and SSE invocation; exact model resource permissions, current BFF session, cancellation and bounded requests. Model management UI remains T13. Real provider acceptance requires an operator-provided endpoint/model/credential; tests use a local protocol fixture, not invented model results.

- [x] Protocol tests red, then bounded JSON/SSE adapter and safe errors.
- [x] PostgreSQL migration 6, revisioned configuration and durable call records.
- [x] Live BFF/CSRF and model resource authorization, limits, cancellation and API tests.
- [x] Complete shared build, Core smoke suites and isolated native PostgreSQL verification.
- [x] Document deployment, API, limits and outstanding real-provider acceptance.

Verification: `tools/verify.ps1 -Scope Core` exited 0 (36 Node tests, 51 Python tests, full .NET HTTP suite including provider/model gateway, real FastAPI proxy). Native PostgreSQL 17.11 run `c925148adf66461a826bc7d895b9fccc` passed with cleanup complete; migrations 1–6, model policy/audit tests and prior integration suites passed. Real provider and application-delegation acceptance are still pending, so this backend slice does not mark T9 complete.

Decisions: no default external provider, no automatic retries, no prompt/output/secret logging. Per-call output-token ceiling and instance-local concurrency/rate limits are the first quota policy; no billing or distributed token budget is claimed. Provider configuration is operator-controlled, never accepted in an invocation body. Exact template appearance remains a standing user requirement; this slice makes no UI changes.
