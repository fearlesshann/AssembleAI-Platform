# T3 · OIDC / BFF 会话

平台使用 ASP.NET Core 官方 OIDC handler 执行 confidential client 的 Authorization Code + PKCE S256 流程。浏览器持有受保护的随机会话引用，access / refresh / ID token 保存在服务端 ticket 中。协议处理遵循 [ASP.NET Core OIDC 文档](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-oidc-web-authentication?view=aspnetcore-10.0)，包版本锁定为 10.0.14。

本切片新增服务端认证入口，不改变首页模板的演示登录。真实 Keycloak、PostgreSQL、前端 SSO 接入及内部签名上下文仍是 T3 后续验收项。

## HTTP 契约

| 请求 | 行为 |
| --- | --- |
| `GET /bff/login?returnUrl=/` | 跳转配置的身份中心；仅接受站内路径；未启用时 503；已有有效会话时 409，需先退出 |
| `GET /signin-oidc` | handler 验证 state、关联 Cookie、nonce、签名、issuer、audience、到期时间，再建立会话；失败 400；回调时已有有效会话则 409 |
| `GET /bff/session` | 已认证时返回 `{iss,sub,name}`；未认证 401；不返回 Token、权限或整个 ticket |
| `GET /bff/csrf` | 启用 BFF 时可匿名获取 `{requestToken,headerName}`，同时设置 HttpOnly CSRF Cookie；登录后应重新获取 token |
| `POST /bff/logout` | 已认证且 Origin 精确匹配 PublicOrigin、`X-CSRF-TOKEN` 有效时删除会话并清 Cookie，返回 204；校验失败 403 |

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

访问 `http://127.0.0.1:5100/bff/login?returnUrl=/bff/session` 可独立验证后端登录，无需修改模板。原生 Keycloak 可按 [官方 ZIP 启动说明](https://www.keycloak.org/getting-started/getting-started-zip) 运行，不要求 Docker。当前机器尚未安装 JDK / Keycloak / PostgreSQL，以上真实服务流程尚未执行。

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

迁移入口顺序执行 1、2、3。BFF 启用时 readiness 要求迁移 3；未启用时仍要求迁移 2。连接、密钥或证书配置不完整时拒绝启动认证，不回退到生产内存会话。密钥 / 证书轮换与备份恢复仍需在真实部署验收。

## 验证边界

测试工程的 `OidcFixture` 通过真实 HTTP 提供 discovery、JWKS、授权码和 token endpoint，并使用 RSA 签名 JWT；测试覆盖完整 code / S256、回调与 Cookie、伪造令牌、CSRF 与撤销。测试服务不注册进生产代码、不连接业务账号，也不代替真实 Keycloak 兼容性验收。

`TicketStoreTests` 验证内存会话隔离、容量、期限、删除与续期并发，以及 PostgreSQL 缺配置时的安全错误。SQL 与 PostgreSQL ticket store 已实现，但迁移、持久化密文、多实例共享及真实数据库故障尚待原生数据库验证。不要将本机 HTTP 测试通过理解为 T3 全部完成。
