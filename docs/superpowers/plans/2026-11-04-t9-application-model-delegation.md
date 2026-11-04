# T9 Application Model Delegation Implementation Plan

**Goal:** 子应用服务端经统一模型网关执行当前用户获准的模型调用，并完成 PostgreSQL / OIDC / 应用 / 模型链路验收。

**Architecture:** 保留已拉取的 OpenAI 兼容协议实现。浏览器模型请求与应用委托请求共用执行器；网关只向明确配置模型白名单的后端操作注入随机、短期、随活动调用撤销的能力。委托端点从该能力恢复主体 / 租户 / 应用 / 运行，重新验证当前权限，不能从正文或一般内部 JWT 取得模型授权。

**Tech Stack:** 既有 .NET 10 / PostgreSQL / YARP、Python 3.13 / FastAPI / httpx；不增加依赖。继续当前 `codex/t3-bff`，门户外观沿用既有实现。

**Spec:** SPEC.md T9 / I.models / I.identity / I.gateway / I.audit；V4,V6,V7,V8,V12,V14,V18,V20,V25,V29。前置提交 `10b8118` 已实现 BFF 模型 API 和协议适配。

## 决定与约束

- 采用活动调用内的 opaque capability；不把原有面向业务操作的 30 秒 JWT 扩大为模型凭据。普通 JWT、浏览器伪造头和已结束调用均不能使用委托接口。
- `GatewayOperation.ModelIds` 是部署者配置的精确模型别名白名单，默认空。能力仅由 GatewayTransformer 发往受控后端；浏览器请求头被清除，响应白名单不返回能力。
- 模型入口 `/internal/models/{modelId}/chat` 使用 `Authorization: Bearer <capability>`；消息格式沿用 T9，正文不接收身份 / 端点 / 密钥。能力注册表有容量上限、仅保存摘要，条目随原调用取消 / 完成删除，持有者也不能扩大模型集合。
- 模型请求必须同时满足原应用调用仍有效、当前用户 model:invoke、租户配置启用。共用并发 / 频率限制、输出上限、审计与流式取消；会话 / 租约 / 排空 / 模型任一失效均停止。
- 能力只存在签发平台进程内；回调须路由至同一实例，重启即失效。多副本委托路由与部署拓扑留 T14，不假定共享注册表。
- 新迁移 7 在模型调用审计增加可空来源 app / release / run / gateway requestId，四字段全空或全部有效；不记录消息、回复、端点或凭据。
- Python 提供显式配置、无重试、有界 JSON / SSE 的服务端客户端和受保护示例操作；客户端断连释放上游，缺少 complete 为失败。测试供应商明确标注，不充当实际模型。
- 用户提供 DeepSeek 官方 deepseek-flash；使用独立验收配置和无业务数据的 JSON / SSE 请求验证，凭据只在本机用户环境变量，不提交仓库。

## 任务与验证

- [x] 网关委托生命周期：新增 `services/platform/Models/ModelDelegationRegistry.cs`，修改 GatewayOptions / GatewayEndpoints / GatewayTransport；先以测试证明随机凭据、精确白名单、容量 / 取消 / dispose 拒绝，再跑网关回归。
- [x] 统一模型执行与内部入口：新增 ModelCaller / ModelChatExecutor / ModelDelegationEndpoints；BFF Chat 与内部 Chat 共用执行器，当前会话 / 模型配置检查保持；测试缺失 / 伪造 / 已结束能力与撤权，运行既有 ModelGatewayTests。
- [x] 持久化来源：迁移 7、ModelStore / PostgresModelStore、readiness / migration runner；测试来源完整性、只读审计范围、直接 BFF 来源为空及真实委托来源归属。
- [x] Python 服务端接入：新增 `sample_backend/models.py` 及 `tests/backend/test_model_client.py`，接入 main 的明确受保护模型操作；验证严格消息 / 响应、有界流、断连及错误脱敏。
- [x] 真实联合测试：新增 PostgresModelFlowTests，OIDC → BFF → Gateway → 真实 FastAPI → 委托模型入口 → 本地协议供应商，实际 PG 配置 / 授权 / 审计；覆盖 JSON、SSE、取消、停用、撤权、失效能力；实际 DeepSeek JSON / SSE 已通过。
- [x] 跑 Core / 原生 PG（必要的已有浏览器回归），审查安全与跨语言边界；记录 SPEC / 文档与真实供应商结果。完整联合与最终凭据隔离复验通过，记录见 docs/development.md；按既有授权提交推送当前分支。

## Review Focus

- 持有其他业务 JWT 或浏览器传同名头是否能越过精确白名单；须零供应商请求。
- 原调用结束 / 客户端断开后，已开始和迟到委托都须取消 / 拒绝。
- 数据库、完成审计或当前权限复核失败不能发送成功 complete。
- 原调用超过 30 秒与模型执行期限分离；委托仍受实际原调用、会话和模型预算限制，不扩展 JWT 本身。
- 模型密钥、能力、提示词、回复不得出现在审计或异常日志；真实模型验证只用显式配置。
