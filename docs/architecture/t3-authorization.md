# T3 · 身份与授权基础

本阶段实现平台成员关系与授权读取、RBAC / 组织范围判断，以及受保护的本人租户接口。[OIDC / BFF 会话与租户切换](t3-bff.md)、[内部调用凭据](t3-internal-calls.md) 已接入；真实 Keycloak 浏览器、PostgreSQL 授权 / 加密会话及协议身份服务与 PG / HTTP 下游联合验收通过。测试工程的身份处理器只用于测试，不会注册到平台服务。

## 身份边界

平台只从一个已认证的 `ClaimsIdentity` 读取唯一的 `iss` 和 `sub`，按原值匹配数据库；不使用 email、显示名称或浏览器请求头作为身份主键。重复 claim、多个已认证 identity、缺少 issuer / subject 都拒绝。OIDC 适配器验证签名、issuer、audience、有效期及协议状态，并保留原始 claim 名称（`MapInboundClaims = false`），再建立 BFF 会话。

平台默认关闭登录。显式配置 BFF 后只接受经过 OIDC 回调建立且仍有效的会话；任意 Bearer Token、伪造 Cookie 或 `X-Internal-*` 均不能建立身份。健康检查和现有开发流演示维持原有用途。

## 读取接口

两个接口均位于 `/api/platform/v1`，使用 JSON，并发送 `Cache-Control: no-store`。API 返回 401 / 403，不自动跳转身份中心。实现采用 ASP.NET Core 的 [授权结果处理器](https://learn.microsoft.com/en-us/aspnet/core/security/authorization/customizingauthorizationmiddlewareresponse?view=aspnetcore-10.0)。

| 请求 | 成功响应 | 拒绝 / 故障 |
| --- | --- | --- |
| `GET /tenants` | `{ "items": [{ "tenantId": "UUID", "name": "租户名称" }] }`；只列出主体仍有效的成员租户 | 未认证 401；主体有效但没有成员关系时列表为空；授权存储不可用 503 |
| `GET /tenants/{tenantId}/access` | `userId`、`tenantId`、`authorizationVersion`、`grants` | 未认证 401；无成员关系、停用或未知租户均为 403；存储不可用 503 |

每条 `grants` 包含 `permission`、`resourceType`、`resourceId`、`tenantWide`、`organizationIds`。这些是本人授权的显示信息，后端不会接受浏览器回传的相同 JSON 作为授权凭据。内部用于判断组织有效性的租户组织全集不会出现在响应中。

错误响应使用 Problem Details，包含 HTTP 状态与稳定 `code`：`authentication_required`、`access_denied`、`tenant_access_denied`、`authorization_unavailable`。数据库连接串、凭据、SQL 和异常详情不进入这些响应。

`POST /bff/tenant` 已提供经成员关系、Origin 与 CSRF 验证的会话租户选择；选择不授予权限。本阶段没有授予角色、修改权限或管理租户的写接口；后续管理写接口需同时具备管理授权、CSRF 与审计。

## 授权语义

1. 在同一数据库查询快照内读取有效用户、租户、成员关系、角色、授予与组织；任何必要关系停用即不授予访问。
2. 调用方提供服务端读取的资源事实：资源所属租户、类型、标识、组织。不得直接把 URL / body / header 中自报的归属当成资源事实。ASP.NET Core 的 `PlatformPermissionRequirement` 与 handler 供业务端点在读取资源后调用 [资源授权](https://learn.microsoft.com/en-us/aspnet/core/security/authorization/resource-based?view=aspnetcore-10.0)。
3. 一条 grant 必须完整匹配操作权限、资源类型、资源标识及组织范围。匹配按大小写精确比较；没有隐式通配符或组织树继承。
4. `tenantWide = true` 仅放宽这条 grant 的组织范围，仍受资源匹配、租户匹配和组织有效性限制。租户级资源没有组织归属，只能由显式的 tenant-wide grant 授权。
5. 组织授权集合为空、组织停用、未知组织或跨租户资源一律拒绝。不能从角色 A 取操作权限，再从角色 B 取工厂范围组合授权。

授权读取不缓存。撤权提交后开始的新查询读取新的已提交状态；PostgreSQL 的 [Read Committed 语义](https://www.postgresql.org/docs/current/transaction-iso.html) 不会追溯改变已取得的查询快照。内部凭据签发与消费均重新查询，消费时必须匹配策略版本及精确资源授权；30 秒有效期、静态密钥轮换和退出窗口见 [内部凭据契约](t3-internal-calls.md)。流式在途请求的撤销继续在 T5 落实。

## 数据与迁移

`deploy/postgres/migrations/0002_authorization.sql` 建立身份、租户、成员、组织、角色与授权关系。租户内关联使用复合外键，禁止把其他租户的组织、角色或成员关联到本租户。授权相关变化递增租户策略版本；用户身份与启用状态变化会更新受影响租户版本。

没有预置管理员、自动授权或固定账号。迁移通过项目原生 `psql` 脚本执行，配置沿用 [开发服务说明](../development-services.md)。迁移 3 为加密 BFF 会话表；当前 `apply.sql` 已扩展到迁移 1–7，readiness 统一要求迁移 7，不随 BFF 开关降级。

知识库的 pgvector 0.8.7 由 `enable-vector.sql` 独立初始化；`apply-with-vector.sql` 执行核心与扩展，Compose 初始化使用完整入口。默认 readiness 还要求该扩展版本；显式 `Readiness:RequireVector=false` 仅用于控制面运行，不代表知识库已就绪。

`PostgresAuthorizationStore` 使用现有 Npgsql，参数化查询，并将请求取消传递到数据库操作；连接和读取器及时释放。没有配置连接时进程可以启动，但授权读取报告不可用，不把故障当成空授权的成功结果。

## 验证与下一步

授权测试接入现有 .NET HTTP smoke，统一命令保持不变：

```powershell
powershell -NoProfile -File tools/verify.ps1 -Scope Core
```

测试覆盖双租户、双工厂、授予范围不可拼接、停用组织、同 subject 不同 issuer、匿名 / 伪造请求、撤权后新请求、故障拒绝及响应脱敏。HTTP 测试使用真实临时监听端口，身份和授权仓储由测试工程替换；它不代替 PostgreSQL 迁移执行或 Keycloak 登录联调。

[原生 PostgreSQL 独立验收](../native-postgres.md) 已执行真实迁移、幂等重放、参数化查询、uuid[]、复合外键、事务回滚与版本、各层停用和提交后撤权。其 HTTP 测试只替换身份处理器，授权仓储连接真实数据库，并验证 401 / 403 / 503 与响应脱敏。

联合测试已验证协议身份服务 → PG 会话与租户选择 → 签名 HTTP 下游调用，撤权后新调用和有效期内旧凭据均被拒绝。真实 Keycloak 浏览器入口单独运行。正式注册 / 网关、管理写接口、跨语言消费者与生产部署继续按 T4–T7 / T13 / T14 落实。
