# T3 · 内部调用身份与权限凭据

平台向应用后端传递独立的短期签名凭据。OIDC 仍负责登录，BFF Cookie 仍只引用服务端会话；两者都不能直接作为内部业务调用凭据。本阶段提供签发、校验、受控公钥输出和联合测试，正式应用路由在 T5 接入。

## 调用契约

`InternalCallTokenService` 提供两个服务端方法：

```csharp
Task<string?> IssueAsync(ClaimsPrincipal principal, Guid selectedTenantId,
    InternalCallTarget target, string requestId, CancellationToken cancellationToken = default);

Task<VerifiedInternalCall?> ValidateAsync(string token, InternalCallTarget target,
    string requestId, CancellationToken cancellationToken = default);
```

`IssueAsync` 从已认证主体读取唯一原始 OIDC `iss` / `sub`，验证选中租户等于资源所属租户，重新查询当前授权，并要求一条完整 grant 满足权限、资源和组织范围后才签名。选中租户来自 BFF ticket 中的选择，使用前须通过 `TenantSession.ReadAccessAsync` 与请求的明确目标租户核对。租户选择本身不授予权限。

`InternalCallTarget` 包含 `AppId`、`ReleaseVersion`、`Audience`、`Permission`、`ProtectedResource`、`Method`、`Path`。这些字段必须由服务端发布映射、路由和资源读取提供，不能直接绑定客户端 JSON 或从未验证的 Token 复制。`ProtectedResource` 包含租户、资源类型 / 标识和可选组织 ID；空组织表示租户级资源，仍需对应的 tenant-wide 授权。

接收端先固定本次请求的预期目标与 requestId，再调用 `ValidateAsync`。通过签名、格式、时间和全部目标字段校验后，它重新读取平台授权，要求主体对应的 `userId`、策略版本、权限和组织范围仍有效。只返回 `VerifiedInternalCall(userId, tenantId, authorizationVersion, requestId)`；未验证的 claims 不成为业务上下文。

普通拒绝返回 null，调用层将其映射为拒绝访问；授权存储故障抛出既有 `AuthorizationStoreUnavailableException`，不得以缓存权限继续执行；取消继续向调用者传播。签发与消费都不缓存授权结果。接收端在数据库读取结束后再次检查截止时间，避免慢查询把已过期凭据变成有效调用。

## JWT 格式

算法固定 **RS256**，RSA 至少 2048 位。JOSE header 只允许 `alg`、`typ`、`kid` 三个字段；`typ` 必须为 `assembleai-internal+jwt`。`kid` 是受控 RSA 公钥 SubjectPublicKeyInfo 的 SHA256，经 Base64URL 编码，用来精确选择当前部署已加载的公钥。未知 key、重复字段、算法替换及额外的 `jku` / `x5u` / `jwk` / `crit` 等 header 均拒绝，服务不会跟随 Token 中的地址取钥。

| Claim | 含义与约束 |
| --- | --- |
| `iss` | 配置的平台内部签发者，与 OIDC issuer 分离，精确匹配 |
| `aud` | 服务端目标应用的单一 audience 字符串；数组或多 audience 不接受 |
| `sub` | 平台 `userId`，非空 UUID；消费时与当前数据库用户匹配 |
| `oidc_iss` / `oidc_sub` | 原始 OIDC 身份对，用于重新读取平台成员与授权 |
| `tenant_id` | 明确目标租户，必须等于服务端资源租户 |
| `authz_ver` | 正整数策略版本，消费时必须与当前数据库版本完全相同 |
| `app_id` / `release` | 服务端选定的应用与发布版本 |
| `permission` | 本次请求的一个操作权限 |
| `resource_type` / `resource_id` | 本次请求的具体资源，不是通配授权 |
| `organization_id` | 资源所属组织 UUID；租户级资源使用空字符串 |
| `method` / `path` | 预期 HTTP 方法与路径，逐值精确匹配 |
| `request_id` | 本次调用的关联标识，逐值精确匹配 |
| `jti` | 随机非空 UUID 的 N 格式，标识这次签发 |
| `iat` / `nbf` / `exp` | 整数 Unix 秒；`nbf=iat`，签发设置 `exp=iat+30` |

校验采用零时钟宽限：未来签发、尚未生效、到达截止时间或超过 30 秒的有效期均拒绝。签名有效仍不能绕过上述约束。JWT 总长度上限为 16 KiB，文本字段有长度及控制字符限制，payload 顶层重复字段拒绝。

算法白名单、独立 Token 类型、issuer / audience 校验及禁止信任 Token 自带取钥地址，依据 [RFC 8725 §3](https://www.rfc-editor.org/rfc/rfc8725.html#section-3)；时间字段含义依据 [RFC 7519 §4.1](https://www.rfc-editor.org/rfc/rfc7519.html#section-4.1)。30 秒、零宽限、目标字段和双次查询授权是本项目的具体约束。

## 配置与公钥

`InternalCalls:Enabled` 默认 false。开启后在启动时校验配置并加载密钥，配置或密钥错误会阻止该服务启动，不回退到临时签名密钥。

| 配置键（环境变量前缀 `InternalCalls__`） | 要求 |
| --- | --- |
| `Enabled` | 显式开启 |
| `Issuer` | 固定绝对 HTTPS 标识；开发可用 loopback HTTP；不含凭据、query 或 fragment |
| `SigningCertificatePath` | 签发端使用的绝对 PFX 路径，包含 RSA 私钥 |
| `SigningCertificatePassword` | PFX 密码，由外部凭据配置注入 |
| `ValidationCertificatePaths` | 额外受信任公钥证书路径数组；最多 8 项，用于只验证的消费者或轮换重叠期 |

签发端配置示例仅展示非秘密字段：

```powershell
$env:InternalCalls__Enabled = 'true'
$env:InternalCalls__Issuer = 'https://platform.example/internal'
$env:InternalCalls__SigningCertificatePath = 'D:\platform-secrets\internal-signer.pfx'
# 按部署方式注入 InternalCalls__SigningCertificatePassword。
# 密钥文件和密码不进入源码、前端资源、日志或命令行参数。
```

只验证的消费者不设置签名 PFX，显式加载公钥证书：

```powershell
$env:InternalCalls__Enabled = 'true'
$env:InternalCalls__Issuer = 'https://platform.example/internal'
$env:InternalCalls__ValidationCertificatePaths__0 = 'D:\platform-config\internal-signer.cer'
```

`InternalCallKeys` 支持仅持公钥的实例，该实例可以验证但不能签发。当前 .NET 消费者从部署控制的证书文件加载密钥；没有自动发现或远程刷新 JWKS 的客户端。私钥只交给签发端。

平台启用此功能后提供 `GET /.well-known/assembleai-jwks.json`，返回已加载的公钥集并设置 `Cache-Control: no-store`；禁用时不注册此路径。JWK 仅含 `kty`、`use`、`alg`、`kid`、`n`、`e`，无 RSA 私密参数。没有面向浏览器的 mint / 签发 API，浏览器不接收内部凭据。

## 轮换与撤权

密钥集在构造时加载，不会因磁盘证书文件变化而自动更新。正常轮换先把新旧公钥部署到全部消费者并重建其密钥实例，再切换签发端私钥；至少经过 30 秒，确认旧凭据过期且所有消费者已经应用新配置后，移除旧公钥并重新加载。紧急撤钥也要求消费者应用不含该 key 的新集合；不能声称只改一处文件就能立即影响所有进程。

权限变更提交后开始的新签发 / 消费查询读取当前状态。成员撤销、资源授权收回或策略版本变化都会使对应请求被拒绝，包括仍处在 30 秒内的旧签名凭据。已取得授权快照、已经执行中的业务操作或 SSE 不在本阶段的即时撤销承诺内。

退出平台会话不会撤销已经签发的内部凭据：其剩余有效时间最多为 30 秒，并仍受每次消费时的当前授权校验。`jti` 和 requestId 不是单次使用账本；同一凭据在有效期内可能再次被接受。业务写操作需要独立幂等机制。

当前签名绑定 method / path / 权限资源，未签名 query 或 body 的字节内容。后端仍须按请求实际操作重新约束业务资源与组织范围，不能以“签名有效”为由信任任意查询条件、Prompt 或写入内容。

## 验证入口与后续接入

```powershell
powershell -NoProfile -File tools/verify.ps1 -Scope Core
& '.\apps\sample\backend\.venv\Scripts\python.exe' tools/verify-postgres.py
```

Core 的 `InternalCallTests` 覆盖签发、仅公钥验证、严格字段 / 时间拒绝、当前授权、轮换和公开 JWKS 边界。`PostgresTrustTests` 在一次性数据库中串联真实 HTTP OIDC 协议测试服务、BFF、PostgreSQL 会话、租户选择、服务端签发和独立 HTTP 接收端；它验证真实数据库策略版本及撤权，不把 token 放进浏览器响应。实际执行结果记录在 [开发记录](../development.md)，不由代码存在推断通过。

2026-11-03 上述契约、完整 Core 及 PG 联合验收均通过；实库记录为 `.artifacts/postgres/run-4423450349cc4c1a9ba6da9851c4dbf8`，进程与临时凭据清理完成。

联合测试里的 gateway 和 receiver 路由仅在测试 host 注册，不进入平台生产路由。该身份提供方是 `OidcFixture`，不是 Keycloak；测试也未覆盖生产 HTTPS、跨机器或完整发布拓扑。已有真实 Keycloak 浏览器验收仍单独记录。

T5 将该契约接入真实已发布应用的路由、入口权限、内部请求头清理、下游失败处理与流生命周期；T7 落实跨语言 SDK / 消费者及集成接入。当前不以测试路由代替应用注册，也不把 `/dev/sample` 代理当作正式受保护网关。
