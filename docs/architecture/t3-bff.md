# T3 · OIDC / BFF 会话

平台使用 ASP.NET Core 官方 OIDC handler 执行 confidential client 的 Authorization Code + PKCE S256 流程。浏览器持有受保护的随机会话引用，access / refresh / ID token 保存在服务端 ticket 中。协议处理遵循 [ASP.NET Core OIDC 文档](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-oidc-web-authentication?view=aspnetcore-10.0)，包版本锁定为 10.0.14。

本切片新增服务端认证与租户选择入口，首页模板仍为演示登录。2026-11-03 真实 Keycloak + Chromium 登录 / 退出、PostgreSQL 加密会话、租户切换与内部签名凭据契约已分别通过；协议测试身份服务与真实数据库 / HTTP 下游的联合链路也已通过。

## HTTP 契约

| 请求 | 行为 |
| --- | --- |
| `GET /bff/login?returnUrl=/` | 跳转配置的身份中心；仅接受站内路径；未启用时 503；已有有效会话时 409，需先退出 |
| `GET /signin-oidc` | handler 验证 state、关联 Cookie、nonce、签名、issuer、audience、到期时间，再建立会话；失败 400；回调时已有有效会话则 409 |
| `GET /bff/session` | 已认证时返回 `{iss,sub,name}`；未认证 401；不返回 Token、权限或整个 ticket |
| `GET /bff/csrf` | 启用 BFF 时可匿名获取 `{requestToken,headerName}`，同时设置 HttpOnly CSRF Cookie；登录后应重新获取 token |
| `POST /bff/logout` | 已认证且 Origin 精确匹配 PublicOrigin、`X-CSRF-TOKEN` 有效时删除会话并清 Cookie，返回 204；校验失败 403 |
| `GET /bff/tenant` | 初次登录返回 `{tenantId:null,authorizationVersion:null}`；已选时重新核验成员并返回当前租户 / 策略版本；已撤权 403，故障 503 |
| `POST /bff/tenant` | JSON `{tenantId:UUID}`；验证登录、Origin、CSRF 和当前成员后更新服务端选择，返回相同租户结构；非法参数 400，无成员 403 |

认证相关响应均 `Cache-Control: no-store`。受保护 API 返回 401 / 403，不隐式跳转身份中心。会话存储不可用时返回脱敏 503。GET 退出与框架默认的远程退出入口均不注册，避免绕过 POST / CSRF 规则。

退出只撤销本平台会话，不结束 Keycloak 的全局 SSO 会话；再次进入身份中心时可能无需输入密码。没有将 ID token 放进浏览器退出 URL，也未实现 refresh token 续期或调用身份中心的 token revocation。此阶段不使用这些 token 调用下游。

## 身份与生命周期

- issuer / subject 保留原始 claim 名称；主体必须通过签名与协议校验，且唯一。登录不会自动建立平台租户成员关系或授予角色。
- session Cookie：HttpOnly、SameSite=Lax、Path=/、无 Domain，无持久浏览器到期时间；生产使用 `__Host-assembleai-session` 和 Secure。
- 会话引用不能改绑主体。登录入口和成功回调都检查已有会话；两个未完成登录中第一个成功后，第二个携带其 Cookie 的回调不能换成另一账号。切换账号先执行受保护的退出，再重新登录。
- 服务端会话自建立起最长 8 小时，不滑动续期。Renew 不延长原有期限，也不能复活已删除或过期的引用。
- 开发模式使用最多 1,024 条的内存存储和进程临时保护密钥，进程重启后会话失效。满容量拒绝新会话，不静默挤掉其他用户。
- 生产模式使用 `platform_bff_sessions`：随机 32 字节 key、DataProtection 加密 ticket、UTC 到期时间。密文与 key 绑定；解密失败拒绝认证。多实例需共享数据库、保护密钥目录及可解密证书。
- 权限仍按请求读取平台数据库，遵循 [授权契约](t3-authorization.md)。Cookie 中没有租户授权事实；已读取的在途请求不承诺即时撤销。

租户切换沿用同一会话引用，保留原主体、OIDC token、IssuedUtc 和 ExpiresUtc；不会重新获得 8 小时。GET 不改 ticket，也不自动挑选默认租户。并发切换最后写入者生效，旧 Cookie / 多标签页读取同一服务端选择；退出与切换竞争时，迟到的 Renew 不能恢复已删除会话。

服务端业务入口通过 `TenantSession.ReadAccessAsync(context, expectedTenantId, store, cancellationToken)` 核对请求的明确租户与当前选择，并重新读取授权。不能把“当前选中租户”当作隐式写入目标；旧页面的其他租户请求必须拒绝。取消默认继承 RequestAborted。后续调用签名见 [内部凭据契约](t3-internal-calls.md)。

## 配置与原生运行

默认关闭认证；配置由 `Authentication:Bff` 节读取。开发使用 Keycloak 已登记的 confidential client `assembleai-platform`，回调地址必须与平台 `PublicOrigin + /signin-oidc` 完全一致。

在已经启动原生 Keycloak 并导入 `deploy/keycloak/assembleai-realm.json` 的终端中，设置进程配置后运行平台：

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Authentication__Bff__Enabled = 'true'
$env:Authentication__Bff__Authority = 'http://localhost:8080/realms/assembleai'
$env:Authentication__Bff__ClientId = 'assembleai-platform'
$env:Authentication__Bff__PublicOrigin = 'http://127.0.0.1:5100'
# ClientSecret 由开发凭据管理注入 Authentication__Bff__ClientSecret；不要写进仓库。
dotnet run --project services/platform/AssembleAI.Platform.csproj --launch-profile development
```

访问 `http://127.0.0.1:5100/bff/login?returnUrl=/bff/session` 可独立验证后端登录，无需修改模板。原生 Keycloak 可按 [官方 ZIP 启动说明](https://www.keycloak.org/getting-started/getting-started-zip) 运行，不要求 Docker。项目现有 [锁定安装与身份验收入口](../native-identity.md)，已在本机用 JDK 25.0.5+8、Keycloak 26.8.1 完成真实浏览器验证；[PostgreSQL 验收](../native-postgres.md) 单独启动独立数据库运行。

| 配置键（前缀 `Authentication__Bff__`） | 约束 |
| --- | --- |
| `Enabled` | 默认 false；显式开启 |
| `Authority` | 精确 issuer；生产 HTTPS，开发 HTTP 仅 loopback |
| `ClientId` / `ClientSecret` | confidential client，均必填；secret 只通过外部配置提供 |
| `PublicOrigin` | 固定浏览器访问源，不含路径、查询或片段；回调不信任 Host / forwarded headers |
| `DataProtectionKeyPath` | 生产必填绝对目录；持久存储保护密钥 |
| `DataProtectionCertificatePath` | 生产必填绝对 PFX 路径，含可用 RSA 私钥，用于保护密钥文件 |
| `DataProtectionCertificatePassword` | 按 PFX 要求注入；不记录在日志 / 仓库 |

生产还要求 `ConnectionStrings__Platform`，应用外部必须终止可信 HTTPS。默认 `AllowedHosts` 仅 loopback，部署时显式配置实际域名。本切片未配置信任代理头；若在反向代理后部署，需要在交付阶段显式配置可信代理与 HTTPS 转发，不可直接信任任意客户端转发头。

当前核心迁移入口顺序执行 1–8；向量扩展通过 `enable-vector.sql` 独立初始化，完整入口为 `apply-with-vector.sql`。readiness 统一要求迁移 8，不随 BFF 开关降级。默认还检查 vector 0.8.7，控制面可显式设置 `Readiness:RequireVector=false`。连接、密钥或证书配置不完整时拒绝启动认证，不回退到生产内存会话。实际 RSA PFX 加密落盘、重新构建应用后恢复和错误证书拒绝已通过测试；密钥 / 证书轮换、跨机器部署和备份恢复仍需验收。

## 验证边界

测试工程的 `OidcFixture` 通过真实 HTTP 提供 discovery、JWKS、授权码和 token endpoint，并使用 RSA 签名 JWT；测试覆盖完整 code / S256、回调与 Cookie、伪造令牌、CSRF 与撤销。测试服务不注册进生产代码、不连接业务账号，也不代替真实 Keycloak 兼容性验收。

`TicketStoreTests` 验证内存会话隔离、容量、期限、删除与续期并发，以及 PostgreSQL 缺配置时的安全错误。`PostgresSessionTests` 已在真实 PostgreSQL 17.11 上验证密文 bytea、独立保护密钥 provider 与存储实例恢复、密文绑定 key、错误证书 / 篡改拒绝、UTC / 秒精度期限、撤销与并发续期、取消及连接失败脱敏。这验证了同机共享数据库与密钥的存储契约，未覆盖跨机器、生产 TLS 或完整部署恢复。

真实 Keycloak 验收由 `tools/verify-keycloak.py` 单独执行：错误密码、真实主体、Cookie / token 边界、CSRF / Origin、退出旧 Cookie 重放与 Keycloak SSO 再进入均通过。此前完成冲突环境变量隔离测试，本轮修改后又复跑通过；进程与临时凭据清理成功。它使用开发 H2 和平台内存会话，不代替 PostgreSQL 验收。

`TenantSessionTests` 通过真实 OIDC HTTP 流程验证租户切换、Origin / CSRF、撤权、旧 Cookie、明确目标核对、绝对期限及退出竞争。`PostgresTrustTests` 使用真实 PG ticket / 授权仓储和独立 HTTP 接收端串起登录 → 选租户 → 签名调用 → 撤权 → 退出；身份源为协议测试服务，不是生产 HTTPS Keycloak 联合部署。
