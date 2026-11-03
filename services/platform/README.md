# AssembleAI platform and BFF

Target: .NET 10. SDK and dependencies are pinned by the root global.json / Directory.Packages.props and project lockfiles.

Identity / authorization and T4 application registry APIs are implemented. The registry includes immutable releases, independent review, tenant activation, revision checks and transactional audit. See [registry configuration and contracts](../../docs/architecture/registry.md), [SPEC](../../SPEC.md) and [current verification records](../../docs/development.md). T5 provides a [protected dynamic gateway](../../docs/architecture/gateway.md), durable pinned runs, SSE cancellation, bounded admission and transport audit. T6 adds the [authorized application catalog and portal](../../docs/architecture/portal.md), with real BFF login / tenant selection and PostgreSQL browser acceptance. Complete application SDK integration continues in T7.

From repository root:

```powershell
dotnet restore AssembleAI.slnx --locked-mode
dotnet build AssembleAI.slnx --no-restore
dotnet run --project services/platform/AssembleAI.Platform.csproj --no-build
dotnet run --project tests/platform/AssembleAI.Platform.SmokeTests.csproj --no-build
# Use the interpreter containing the sample backend's locked dependencies:
python tests/platform/smoke_fastapi_proxy.py
```

The development profile listens on `http://127.0.0.1:5100`. BFF authentication is disabled by default. Production authentication requires trusted HTTPS, PostgreSQL sessions and persistent protected keys; use the [BFF configuration](../../docs/architecture/t3-bff.md). Native processes are supported; Docker is optional.

## Endpoints

| Path | Meaning |
| --- | --- |
| GET /health/live | Process accepts requests; does not claim dependencies or authentication are ready |
| GET /health/ready | All configured dependency checks pass; otherwise HTTP 503 |
| GET /bff/login; GET /signin-oidc | Explicit OIDC Code + PKCE login and validated callback |
| GET /bff/session; GET /bff/csrf | Subject display information and antiforgery token; no OIDC credentials |
| POST /bff/logout | Origin / CSRF protected revocation of the platform session |
| GET /bff/tenant; POST /bff/tenant | Read or update server-side tenant selection; writes require Origin / CSRF and current membership |
| GET /api/platform/v1/tenants | Authenticated subject's enabled memberships |
| GET /api/platform/v1/tenants/{tenantId}/access | Subject's own grants; non-member and unknown tenants both return 403 after authentication |
| GET /api/platform/v1/tenants/{tenantId}/catalog; GET /catalog/{appId} | Selected-tenant authorized display catalog / detail; all entry permissions, published active release and trusted deployment; hidden detail 404, explicit 500-item ceiling |
| GET /api/platform/v1/registry/apps/{appId}/releases; GET /{version} | Owner developer / reviewer reads; bounded release list and exact version |
| PUT /api/platform/v1/registry/apps/{appId}/releases/{version} | Create / edit owned draft with expectedRevision; Origin / CSRF protected |
| POST /api/platform/v1/registry/apps/{appId}/releases/{version}/transitions | Submit, reject, publish, disable or enable; independent approval and revision checks |
| GET /api/platform/v1/tenants/{tenantId}/applications/{appId}; PUT same | Read / update tenant release pointer; selected tenant and fresh application-specific registry:manage grant |
| POST /api/platform/v1/tenants/{tenantId}/applications/{appId}/releases/{version}/runs | Create a session-bound run for the current enabled release after fresh authorization and backend protocol health validation; Origin / CSRF protected |
| GET /api/apps/{appId}/releases/{version}/{operation}; POST same | Forward an exact configured operation using X-Run-ID / X-Tenant-ID intent and the server-side session; POST requires Origin / CSRF; signs downstream calls and supports bounded SSE |
| GET /.well-known/assembleai-jwks.json | Public keys only, when InternalCalls:Enabled is true; no browser mint / token-issuance endpoint |
| GET /dev/sample/health | Development + DevelopmentSample:Enabled only; forwards to sample /health/live |
| GET /dev/sample/stream | Development + DevelopmentSample:Enabled only; forwards sample /stream SSE incrementally |

Sample proxy routes do not exist in Production, even if the enable flag is true. They allow only GET on the two exact paths and do not copy incoming identity headers or cookies. The destination is server configuration `Services__Sample__Address`, default `http://127.0.0.1:8101/`; it is not selected from request parameters. These routes validate the transport only and are not production application APIs.

## Dependency checks

Set environment variables before starting the platform; do not commit credentials:

- `ConnectionStrings__Platform`: PostgreSQL connection string. Readiness opens a real connection and requires migration 5 regardless of the BFF switch. `apply.sql` executes core migrations 1–5, including registry, runtime leases and call records.
- `Readiness__RequireVector`: defaults to true and requires pgvector 0.8.7. Explicit false supports control-plane-only checks; it does not make other dependencies optional. See [core and vector initialization](../../docs/native-postgres.md).
- `Readiness__RedisHost`, `Readiness__RedisPort`, `Readiness__RedisPassword`: Redis connection; sends AUTH when a password is supplied and then validates PONG. T2's local connection is plain TCP; TLS / ACL user support must be added for deployments that require them.
- `Readiness__MinioEndpoint`: MinIO `/minio/health/ready`.
- `Readiness__RabbitMqEndpoint`, `Readiness__RabbitMqUser`, `Readiness__RabbitMqPassword`: management readiness endpoint and dedicated monitoring credentials.
- `Readiness__OidcDiscoveryEndpoint`: expected Keycloak realm discovery URL; validates required HTTP(S) endpoint fields. This is availability validation, not user token authentication.

Every check has a 3-second budget. Response output contains dependency names and states, never exception messages, connection strings or credentials. Missing configuration remains unhealthy. HTTP checks refuse redirects to avoid moving monitoring credentials to another target.

## Authentication boundary

T3 reads membership and grants from PostgreSQL and evaluates permission/resource/organization scopes through ASP.NET Core Authorization. It does not cache decisions or trust caller-supplied identity headers. OIDC / BFF login is implemented; browsers hold only an HttpOnly session reference. Development uses bounded memory sessions and ephemeral keys; Production uses encrypted PostgreSQL tickets and a persistent DataProtection keyring protected by an external RSA PFX. Sessions have an absolute eight-hour lifetime. Tenant switching preserves the original subject and deadline; a late renewal cannot revive logout. See [authorization](../../docs/architecture/t3-authorization.md) and [BFF / tenant selection](../../docs/architecture/t3-bff.md).

Internal calls are separately opt-in. Their RS256 credentials bind the target application, resource, permission and request, last at most 30 seconds, and require current authorization and policy version at consumption. Only public JWKS are exposed; there is no browser issuance API. Configuration, public-key-only consumers and rotation are documented in the [internal-call contract](../../docs/architecture/t3-internal-calls.md). The T5 gateway now consumes these credentials on registered operations; cross-language SDK / application integration remains T7 work.

The console smoke suite starts real Kestrel servers on temporary local ports. It verifies liveness, unavailable-dependency readiness, exact route and method restrictions, identity-header removal, first SSE event delivery before completion, and absence of sample routes in Production. Its isolated in-memory key repository does not read or write the machine's DataProtection keys; an unencrypted-key warning refers only to that in-memory repository.

The Core suite also verifies authorization isolation, BFF OIDC / CSRF, persistent key recovery, tenant selection, absolute expiry, logout / renewal races, and internal credential validation / rotation. These checks have passed. Production never registers the test authentication handler or test business routes.

T5 acceptance adds the production gateway with real OIDC protocol exchanges and a separate signed HTTP downstream: pinned runs, safe headers / logs, incremental SSE, revocation / logout / disconnect cancellation, deadlines and admission recovery. The final focused gateway run also verified that a deadline exhausted during start-record persistence prevents downstream dispatch while still finalizing the record. Gateway and internal calls remain disabled by default; the [gateway contract](../../docs/architecture/gateway.md) describes explicit configuration and per-instance limit boundaries.

The [native PostgreSQL acceptance](../../docs/native-postgres.md) has passed real migrations, authorization and encrypted-session contracts. Its joint `PostgresTrustTests` chain uses the HTTP `OidcFixture`, BFF, PG tenant selection and an independent HTTP receiver holding only public keys. [Real Keycloak browser acceptance](../../docs/native-identity.md) runs separately. Neither result claims a combined real Keycloak + PostgreSQL + production HTTPS deployment, cross-machine recovery or vector search acceptance.

`PostgresGatewayStoreTests` verifies real run / call persistence, version draining, permanent revocation, bounded capacity and audit failures. `PostgresGatewayFlowTests` uses the production gateway and PostgreSQL stores without replacement to verify signed JSON / SSE, durable call records and in-flight cancellation after database permission revocation or registry disablement. This T5 chain uses OidcFixture and development memory sessions; its latest successful run is recorded in the native PostgreSQL guide.

The Python smoke script starts the actual FastAPI sample and built .NET platform on temporary ports, verifies health and incremental UTF-8 SSE through YARP, and terminates both processes. External dependencies can run natively or on existing development services; their readiness is not implied by either suite.
