# Platform skeleton

Target: .NET 10. SDK and dependencies are pinned by the root global.json / Directory.Packages.props and project lockfiles.

From repository root:

```powershell
dotnet restore AssembleAI.slnx --locked-mode
dotnet build AssembleAI.slnx --no-restore
dotnet run --project services/platform/AssembleAI.Platform.csproj --no-build
dotnet run --project tests/platform/AssembleAI.Platform.SmokeTests.csproj --no-build
# Use the interpreter containing the sample backend's locked dependencies:
python tests/platform/smoke_fastapi_proxy.py
```

The development profile listens on `http://127.0.0.1:5100`. To run Production, omit launch settings and set the environment explicitly:

```powershell
dotnet run --project services/platform/AssembleAI.Platform.csproj --no-launch-profile -- --environment Production --urls http://127.0.0.1:5100
```

## Endpoints

| Path | Meaning |
| --- | --- |
| GET /health/live | Process accepts requests; does not claim dependencies or authentication are ready |
| GET /health/ready | All configured dependency checks pass; otherwise HTTP 503 |
| GET /api/platform/v1/tenants | Authenticated subject's enabled memberships; no registered login adapter yet, so direct calls return 401 |
| GET /api/platform/v1/tenants/{tenantId}/access | Subject's own grants; non-member and unknown tenants both return 403 after authentication |
| GET /dev/sample/health | Development + DevelopmentSample:Enabled only; forwards to sample /health/live |
| GET /dev/sample/stream | Development + DevelopmentSample:Enabled only; forwards sample /stream SSE incrementally |

Sample proxy routes do not exist in Production, even if the enable flag is true. They allow only GET on the two exact paths and do not copy incoming identity headers or cookies. The destination is server configuration `Services__Sample__Address`, default `http://127.0.0.1:8101/`; it is not selected from request parameters. These routes validate the transport only and are not production application APIs.

## Dependency checks

Set environment variables before starting the platform; do not commit credentials:

- `ConnectionStrings__Platform`: PostgreSQL connection string. Readiness opens a real connection and checks `public.platform_schema_migrations` contains version 2; apply both deploy migrations first.
- `Readiness__RedisHost`, `Readiness__RedisPort`, `Readiness__RedisPassword`: Redis connection; sends AUTH when a password is supplied and then validates PONG. T2's local connection is plain TCP; TLS / ACL user support must be added for deployments that require them.
- `Readiness__MinioEndpoint`: MinIO `/minio/health/ready`.
- `Readiness__RabbitMqEndpoint`, `Readiness__RabbitMqUser`, `Readiness__RabbitMqPassword`: management readiness endpoint and dedicated monitoring credentials.
- `Readiness__OidcDiscoveryEndpoint`: expected Keycloak realm discovery URL; validates required HTTP(S) endpoint fields. This is availability validation, not user token authentication.

Every check has a 3-second budget. Response output contains dependency names and states, never exception messages, connection strings or credentials. Missing configuration remains unhealthy. HTTP checks refuse redirects to avoid moving monitoring credentials to another target.

## Authentication boundary

T3's authorization foundation now reads membership and grants from PostgreSQL and evaluates permission/resource/organization scopes through ASP.NET Core Authorization. It does not cache decisions or trust caller-supplied identity headers. OIDC login, BFF sessions and internal signed contexts remain in progress; without a validated session adapter, protected APIs return 401. See [the contract and verification boundary](../../docs/architecture/t3-authorization.md). Development DataProtection is still ephemeral; persistent/shared key management is required before storing authenticated sessions. Platform logs use console output rather than privileged Windows EventLog.

The dependency-free console smoke suite starts real Kestrel servers on temporary local ports. It verifies liveness, unavailable-dependency readiness, exact route and method restrictions, identity-header removal, first SSE event delivery before completion, and absence of sample routes in Production. Its isolated in-memory key repository does not read or write the machine's DataProtection keys; an unencrypted-key warning refers only to that in-memory repository.

The authorization smoke tests also use real temporary HTTP listeners, with test-only authentication and an in-memory store fixture. They verify tenant/factory isolation, grant binding, issuer separation, revocation, cancellation, and 401/403/503 responses. Production never registers the test authentication handler. SQL migration execution and real OIDC login are separate integration checks.

The Python smoke script starts the actual FastAPI sample and built .NET platform on temporary ports, verifies health and incremental UTF-8 SSE through YARP, and terminates both processes. External dependencies can run natively or on existing development services; their readiness is not implied by either suite.
