# T5 · 应用运行与正式网关

网关从 PostgreSQL 的已发布记录、租户活动版本与运行租约读取目标，使用 YARP `IHttpForwarder` 转发真实 HTTP / SSE。客户端提供的应用版本和运行编号必须与服务端记录匹配。服务地址仅来自部署者维护的 `Registry:BackendBindings`；运行租约不会将 OIDC 或内部调用 Token 交给浏览器。

## 启用与服务契约

先执行核心迁移 1–5，配置 [BFF](t3-bff.md)、[内部签名密钥](t3-internal-calls.md) 和 [注册部署映射](registry.md)。`Gateway:Enabled` 默认 false；启用时必须同时启用 BFF 和持有私钥的 InternalCalls，否则启动失败。

配置公开的业务操作，每条精确指定逻辑服务、后端版本、HTTP 方法、下游路径及一个入口权限：

```json
{
  "Gateway": {
    "Enabled": true,
    "HealthPath": "/health/ready",
    "Operations": [
      { "Service": "production-api", "Version": "1.0.0", "Method": "POST", "Path": "/query", "Permission": "production:read" },
      { "Service": "production-api", "Version": "1.0.0", "Method": "GET", "Path": "/stream", "Permission": "production:read" }
    ]
  }
}
```

此例不自动部署服务。方法仅支持 GET / POST；路径为有界 ASCII 字母、数字、下划线、短横线组成的非空分段，不能注册通配路径、URL、点段或编码路径。重复 service/version/method/path 配置阻止启动。配置在进程启动时加载；服务实例变更可经部署配置与重启生效，不修改应用发布记录或门户源码。自动服务发现订阅、负载均衡和滚动配置更新不属于当前实现。

创建运行前，网关以 3 秒预算请求可信目标的 HealthPath，最多读取 4 KiB JSON，要求 HTTP 200 和下列匹配结果：

```json
{ "status": "healthy", "service": "production-api", "version": "1.0.0", "protocolVersion": 1 }
```

健康端点应为只读、无需浏览器身份的部署协议端点。版本与协议匹配是运行准入检查，不等于完整业务兼容测试或前端资源 SHA-256 实测；真实发布物构建 / 静态资源接入按 T7 / T14 完善。网关不跟随健康或业务请求重定向，不使用系统代理，不缓存下游 Cookie，不自动重试业务请求。

## 创建与使用运行

1. 通过 BFF 登录，再用 `POST /bff/tenant` 选择目标租户。
2. `POST /api/platform/v1/tenants/{tenantId}/applications/{appId}/releases/{version}/runs`，空正文，附同源 Origin / CSRF。要求当前租户活动版本已启用、全局已发布、Manifest 全部入口权限均满足，且部署映射 / 健康协议匹配。健康检查后在数据库事务中重新核验当前版本与权限，再建立租约。
3. 响应包含 runId、tenantId、appId、releaseVersion、manifest、expiresAt、apiBase。apiBase 为 `/api/apps/{appId}/releases/{version}/`，不含下游地址或凭据。
4. 调用 apiBase 下已登记的操作，附 `X-Run-ID` 和 `X-Tenant-ID`（均为标准带短横线 UUID）。它们只表达请求意图，必须与同一 BFF 会话、平台主体、租户和发布版本绑定。另一会话即使登录同一用户也不能接管旧租约。

POST 业务请求仍需固定 Origin / `X-CSRF-TOKEN`，正文必须声明 UTF-8 JSON，最大 1 MiB；不接受 Content-Encoding，读取正文预算 10 秒。GET 不允许正文。query 最多 2048 字符，可用于业务参数；网关不记录它，后端仍需验证参数对应的业务资源权限。平台目前只代理这些 JSON / SSE 操作，不提供文件上传、任意 URL 代理或 WebSocket。

每次请求从真实会话存储与授权仓储重新核验，无授权缓存。租户切换不会把旧页面请求隐式改到另一个租户。所有受保护接口未登录为 401；失效会话 / 租约 / 权限为 403，未登记操作 404，参数错误 400，正文过大 413，编码错误 415，限流 429，配置 / 存储 / 健康失败 503，转发或下游响应拒绝为安全 502，首响应 / 总期限等可在响应开始前返回 504。响应开始后的断流不能再改 HTTP 状态。

## 固定版本、排空与撤销

租约默认 900 秒；创建时按 BFF 剩余时间收紧请求时长，实际运行始终受 BFF 原期限约束。数据库按事务开始时间计算 expiresAt，可能因创建延迟略晚于会话到期，不能将该字段视为延长会话授权。每用户最多 100 个未失效租约，创建与租户指针修改共用应用事务锁，防止切换期间创建旧版本。数据库只保存 BFF 引用的 SHA-256，不保存引用原文；运行编号不构成独立授权凭据。

升级 / 回滚指针时，现有旧版本租约获得默认 120 秒排空截止时间。此截止只写一次，A→B→A 或继续切换都不能延长、清除或复活旧租约。只有当前活动版本可新建运行。全局停用或租户停用永久撤销既有租约；再次启用必须创建新运行。

在途调用每 2 秒重新读取服务端会话、运行状态及全部入口授权，单次复核预算 3 秒。退出、切换租户、撤权、停用或复核失败即取消转发；正常调度且依赖遵守取消时，发现与发出取消的预算约 5 秒。数据库和网络取消是协作式行为，不承诺强制终止不响应取消的下游业务进程。

Cookie 认证处理器会缓存同一请求的认证结果，因此周期复核直接通过可信会话引用读取 ITicketStore。引用由存储成功读取时放入内部 feature，不能从 Header 或浏览器可读内容构造。自定义 SessionStore 装饰器须转发带 HttpContext 的 RetrieveAsync 重载。

## 时限与限流

| 配置项 | 默认 | 约束 |
| --- | --- | --- |
| LeaseSeconds | 900 | 1–3600；还受 BFF 原期限约束 |
| DrainSeconds | 120 | 1–300；创建时写入每个租约 |
| CallSeconds | 300 | 1–600；单次调用最长时间 |
| ResponseHeadersSeconds | 10 | 1–60；下游首个响应头预算 |
| IdleSeconds | 30 | 1–120；YARP I/O 空闲预算 |
| RecheckSeconds | 2 | 1–10；在途复核间隔 |
| CheckTimeoutSeconds | 3 | 1–5；单次复核预算，PG 仓储整体另有 3 秒预算 |
| PerUserConcurrency | 4 | 每个 tenant/user 在途上限 |
| GlobalConcurrency | 64 | 当前进程在途上限，不小于每用户上限 |
| RequestsPerMinute | 60 | 每个 tenant/user 的 60 秒固定窗口计数 |
| MaximumPartitions | 10000 | 当前进程最多保留的限流分区 |

限流只对已解析的获准运行请求和创建尝试执行，不是整个身份服务或网络入口的防攻击配额。无队列；429 带 Retry-After: 60。只有当前窗口已过期且没有在途请求的分区可回收。限额为单实例，不能当作跨副本全局配额；生产入口防护与多副本配额按 T14 配置。

调用最终时限同时受调用时长、会话期限、租约期限与排空截止约束。使用单调时间消耗预算，UTC 回拨、重复复核或较晚的截止值都不能增加剩余时间。调用开始审计完成后若已耗尽预算，直接终结记录，不能抢在 CancelAfter(0) 生效前发出请求。

## 转发、SSE 与信息边界

网关丢弃调用方 Cookie、Authorization、Origin、CSRF、租户 / 运行与身份模拟头，仅生成自己的 RS256 Authorization、X-Request-ID 及必要内容头。内部凭据绑定具体应用版本、租户、入口资源、操作权限、method / 下游 path；下游必须按 [内部调用契约](t3-internal-calls.md) 验签、验证当前策略，并按实际业务资源再做授权。网关检查 Manifest 全部入口权限，签发单个已登记操作权限；不能以该入口凭据替代数据集 / 文档 / 工具授权。

成功响应仅接受无 Content-Encoding 的 UTF-8 JSON 或 SSE。下游 Cookie、Location、CORS、鉴权头及 trailers 不进入平台源；响应固定 no-store / nosniff 和本次 requestId。YARP 清理尚未开始的错误响应后，也重新恢复同一关联标识和安全头。3xx / 普通下游故障返回安全 Problem，不回显原始错误正文；下游 401 / 403 保留状态。

SSE 使用 UTF-8，事件为 delta、complete、error；可以使用注释心跳。按段交付，不等生成完成再返回。只有明确的 complete 表示业务完成；error 表示业务失败，EOF / 连接重置且未见 complete 必须当作中断。网关不解析或伪造业务完成事件；流开始后的超时 / 撤权 / 断连通过终止传输体现，不能保证再写一条完整 error 帧。门户消费实现留 T6 / T7。

正式转发器在隔离 DI 容器中使用 NullLogger，避免 YARP 默认日志打印完整目标 URL、query 或底层异常消息；即使主应用开启 Yarp Trace 也不重新启用它的原始日志。主应用保留安全调用记录和完成审计失败的结构化日志。部署者不得另外打开会采集凭据 / query / 正文的通用请求日志或诊断导出。

## 调用记录与故障语义

`platform_gateway_calls` 在访问下游前持久化 running，记录 requestId、runId、主体、租户、应用 / 版本、登记的方法 / 路径和时间。开始持久化失败时拒绝转发。结束使用独立的 3 秒取消预算，记录 HTTP 状态、传输 outcome 与耗时；客户端断开不会直接取消这一步。

`transport_completed` 只代表 HTTP 转发完成，不等于 SSE 业务成功；revoked、deadline、headers_timeout、disconnected、downstream_rejected、forwarding_failed 等记录其他结束原因。结束审计失败时保留 running 并结构化报警，后续需要运维对账；不能宣称已回滚交付的响应，也不能自动重试业务写请求。留存 / 查询 / 对账界面归 T13。表中不保存 query、body、Token、Cookie、会话引用或目标地址。

过期 / 撤销租约在同一用户创建新运行时有界清理，每次最多 100 条；调用历史无租约外键，清理不丢失归属。全平台历史保留和周期清理归 D9 / T13，当前不宣称后台已自动清空所有历史数据。

## 验证入口

`tools/verify.ps1 -Scope Core` 覆盖真实 OIDC / YARP / 公钥下游链路，使用明确标注的内存控制面测试 fixture，验证签名、头与日志隔离、首段、取消、时限、限流和编码错误。`GatewayDeadlineTests` 与 `GatewayAdmissionTests` 使用可控单调时钟和并发请求验证边界。

`tools/verify-postgres.py` 运行完整迁移 1–5，再验证真实 PG 租约 / 审计和生产 Gateway → PostgreSQL → 签名 HTTP 下游组合，不替换仓储。组合身份服务为 OidcFixture，BFF 使用开发内存会话；T3 真 Keycloak 浏览器和 PG 加密会话另有验收，不等同完整生产 HTTPS 部署。结果以 [开发记录](../development.md) 为准。

API 机制依据 [YARP Direct Forwarding](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/yarp/direct-forwarding?view=aspnetcore-10.0)；上文超时、排空、权限与审计规则是本项目决定。
