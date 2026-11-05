# T9：OpenAI 兼容模型网关

用户选定 OpenAI 兼容协议，也支持显式配置的本地推理服务。当前实现文本 Chat Completions 的后端配置、租户授权、JSON / SSE、应用服务端委托和元数据审计。2026-11-04 用户提供 DeepSeek 官方 `deepseek-flash` 用于真实验收；本地协议夹具与实际供应商结果分开记录。T13正式管理页为`/manage/models`，配置、调用验证及记录的页面合同见[资源管理](resource-management.md)。

## 部署配置

默认 `Models.Enabled=false`，没有预置外部供应商、模型、租户授权或密钥。模型地址和上游模型名由部署者配置；请求正文只能指定消息、输出上限、温度和流式模式。更改部署目录配置后需重启平台。

以下为**待填实际模型名**的本地配置示例。只在目标服务已启动且可以调用时启用：

```json
{
  "Models": {
    "Enabled": true,
    "CallSeconds": 120,
    "RecheckMilliseconds": 1000,
    "PerUserConcurrency": 2,
    "GlobalConcurrency": 32,
    "RequestsPerMinute": 30,
    "Providers": {
      "local": {
        "Endpoint": "http://127.0.0.1:8000/v1/chat/completions",
        "AllowInsecureHttp": true,
        "CredentialEnvironmentVariable": "ASSEMBLEAI_MODEL_KEY"
      }
    },
    "Catalog": {
      "assistant": {
        "Provider": "local",
        "UpstreamModel": "your-model",
        "DisplayName": "制造助手",
        "Enabled": true,
        "MaximumOutputTokens": 4096
      }
    }
  }
}
```

`Endpoint` 是完整 Chat Completions 地址，不自动补路径。生产供应商使用 HTTPS；HTTP 只接受显式开启的 loopback 地址。端点不得包含用户信息、查询或片段，不跟随重定向。凭据变量由启动平台的进程环境提供，每次调用重新读取；缺失或非法时拒绝调用。无认证的本地服务可省略 `CredentialEnvironmentVariable`，此时不发送 Authorization。密钥不能放入浏览器、调用正文或提交到仓库；本接口不提供读取密钥的能力。供应商须接受文本 `messages`、`max_tokens`、`stream` 及流式 `stream_options.include_usage`；不支持的扩展能力需另行适配，不自动降级重试。

先执行统一 PostgreSQL 迁移（1–8），再按 [身份与授权规则](t3-authorization.md) 授权。`model:manage` 和 `model:invoke` 相互独立，资源类型为 `model`、资源 ID 为目录别名（示例 `assistant`）。当前模型为租户级资源，需要 tenant-wide 范围授权。管理员通过下述配置 API 为租户启用模型；没有租户配置即禁用。

## 平台接口

部署模型还可设置可选的 `ReasoningEffort`（none / minimal / low / medium / high / xhigh / max），由服务端映射为 `reasoning_effort`；调用正文不能覆盖。留空时不发送，实际支持值以所选供应商为准，不自动重试其他值。[DeepSeek 官方协议](https://api-docs.deepseek.com/api/create-chat-completion/) 当前默认开启思考模式；本轮简短验收显式设置 `none`，使用 `https://api.deepseek.com/chat/completions` 和 `deepseek-flash`。上游推理过程不会作为回答返回或存入审计。

公共前缀：`/api/platform/v1/tenants/{tenantId}/models`。所有请求需要有效 BFF 登录，且路由租户必须是服务端会话当前选择的租户。写请求要求现有 Origin / CSRF 验证。模型列表与配置只返回别名、展示名、启用状态和输出上限，绝不返回供应商地址、上游模型名或凭据引用。

| 方法与后缀 | 权限 | 行为 |
| --- | --- | --- |
| `GET /` | invoke 或 manage | 仅列出有对应资源权限的模型 |
| `GET /{modelId}/configuration` | manage | 读取租户配置；未创建时为空 |
| `PUT /{modelId}/configuration` | manage | 按修订号创建或更新；冲突返回 409 |
| `GET /{modelId}/calls` | manage | 最近 50 条本租户、本模型调用记录 |
| `POST /{modelId}/chat` | invoke | 统一文本 JSON 或 SSE 调用 |

配置正文：

```json
{"enabled":true,"maxOutputTokens":1024,"expectedRevision":0}
```

首次创建使用 `expectedRevision:0`；修改时使用最近读取的 `revision`。租户上限不得超过部署目录上限。更新配置会终止使用旧修订号的在途调用，包括更新输出上限。

调用正文：

```json
{"messages":[{"role":"user","content":"请概括这段生产说明"}],"maxOutputTokens":512,"temperature":0.2,"stream":false}
```

仅接受 `system`、`user`、`assistant` 文本消息，1–64 条；请求最多 64 KiB。拒绝重复字段、未知字段、压缩正文、查询参数、非法类型与越限参数。`maxOutputTokens` 默认为租户与目录上限的较小值，`temperature` 可省略或为 0–2。暂不提供工具调用、图像、embedding 或 Responses API。

JSON 返回 `content`、`finishReason`、`usage`、服务器生成的 `requestId`。`usage` 为 `promptTokens`、`completionTokens`、`totalTokens`，没有上游用量时为 null；保留供应商原始非负计数，不根据文本估算或改写总数。

`stream:true` 返回 `text/event-stream`，事件为 `delta`、`usage`、`complete`，各事件 JSON 都带 `requestId`。只有读到上游 `[DONE]`、完成审计且最终授权复核通过后，才发送 `complete`。已发送部分内容之后发生故障，连接中止而不发送成功事件；调用方必须将缺少 `complete` 视为未完成。首段前的错误使用安全的 Problem JSON（400 输入错误、403 拒绝、409 配置冲突、429 限流、502 上游失败、503 依赖故障或超时）。不透传上游错误正文。

## 运行和审计约束

- 调用前先持久化主体、租户、模型别名与请求编号，存储故障时不联系上游。配置更新与配置审计在同一事务中提交。
- 不转发浏览器 Cookie、Origin、Authorization 或内部身份头；仅发送服务端解析的供应商凭据。专用 HTTP 客户端关闭请求日志、Cookie 和重定向。
- 默认每租户 / 用户并发 2、全实例并发 32、每租户 / 用户每分钟 30 次。超限直接拒绝，不排队。额度为实例内限制及单次输出上限；没有承诺跨实例计费或每日 token 预算。
- 调用总预算默认 120 秒（包括读取正文），连接预算 10 秒，会话检查和存储操作另有 3 秒上限。会话到期直接取消；默认每秒复核登录、当前租户、权限和模型配置，故障即停止。退出、切租户、撤权、停用以及客户端断开会取消上游。已经传给浏览器的内容无法撤回。
- 调用结束写入耗时、状态和可得用量。状态有 `completed`、`cancelled`、`revoked`、`timeout`、`upstream_error`、`failed`。审计写入期间发生晚撤权时，已完成的供应商调用可记录为 `completed`，但响应仍被拒绝；状态不代表浏览器已收到全部内容。
- 完成审计失败时不报告成功，保留可追踪的 `running` 记录，重启后的对账与留存策略归 T13 / D9。审计不存提示词、回复、原始错误、端点或凭据；未提供费用估算。没有自动重试，避免重复生成或收费。
- 非流式上游响应最多 1 MiB；SSE 单行内容最多 64 KiB、单事件按 UTF-8 行内容加单字节换行计的解析预算最多 256 KiB，逐段解析 UTF-8，不缓存整条生成。完整协议依据 [Chat Completions 官方说明](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create)。

## 子应用服务端委托

部署者给需要模型的精确后端操作配置白名单，例如：

```json
{"Service":"sample","Version":"0.1.0","Method":"POST","Path":"/model-chat","Permission":"sample:use","ModelIds":["assistant"]}
```

默认 ModelIds 为空；注册 Manifest 本身不能授予委托权限。网关清除浏览器传入的头，向受控服务发送原业务 JWT 和独立的 `X-Model-Delegation` 能力。能力只在原网关调用活动期间有效，随机 256 位，最多登记 4096 个，只保存摘要；原调用取消或结束时立即删除并取消已启动的模型调用，早于写网关完成审计。普通业务 JWT 无法直接调用模型接口。

Python 服务端使用如下显式配置，生产 origin 要求 HTTPS，开发允许 loopback HTTP：

```powershell
$env:ASSEMBLEAI_MODEL_GATEWAY_ORIGIN = 'http://127.0.0.1:5080'
$env:ASSEMBLEAI_MODEL_ID = 'assistant'
$env:ASSEMBLEAI_MODEL_MAX_OUTPUT_TOKENS = '128'
```

原来的 `ASSEMBLEAI_VALIDATION_URL` / SERVICE / SERVICE_VERSION 仍须按 [应用接入文档](application-template.md) 配置。`POST /model-chat` 接受 `{message,stream?}`（最多 4 KiB），先远程验证业务 JWT，再将能力作为 Bearer 调用固定平台 origin 的 `/internal/models/assistant/chat`；浏览器从既有应用 SDK 访问这个业务操作，不接收该能力或供应商凭据。客户端不重试、不跟随重定向、不沿用 Cookie，缺少配置或能力即拒绝。

内部接口仅从能力恢复主体、租户、应用与固定运行，正文仍是本页的模型消息格式，不能指定身份 / 来源 / 端点。每次调用同时要求白名单、原会话和运行有效、当前 model:invoke 权限及租户模型配置启用；直接 BFF 与委托请求共用限流、预算、审计及协议执行器。接收业务请求时的 JWT 可以自然到期，已开始的委托继续按真实活动调用检查，不延长或刷新原 JWT。

委托总时长取模型、会话、租约及外层网关实际剩余预算的较小值。外层默认首响应头 10 秒、空闲 30 秒仍生效；较慢模型的部署需显式配置合适的 Gateway.ResponseHeadersSeconds / IdleSeconds，不能只提高 Models.CallSeconds。验收部署使用首头 / 空闲 60 秒、外层总计 180 秒、模型总计 120 秒，生产默认值没有放宽。

注册表位于签发网关实例内，Python 回调必须路由回该实例，重启后能力失效。当前适合本机或单实例部署；多副本共享能力或定向路由需在 T14 交付拓扑中实现。能力不支持后台任务脱离原请求继续运行。

迁移 7 给每条模型调用增加 `source`：`{appId,releaseVersion,runId,gatewayRequestId}`，直接 BFF 调用为 null。来源由网关捕获，与调用准入同事务存储，不引用可删除业务行；四字段必须全空或全部有效。授权模型管理员可通过 calls 查询关联原业务调用，审计不保存 capability、JWT、提示词、回复或密钥。

Python 将模型 delta.content 转为 SDK 的 delta.text，大增量按 4096 字符拆帧；usage 保留至 complete，避免给既有 SDK 增加未知事件。发生协议错误、取消或没有 complete 时不能报告成功。

## 验证入口与剩余验收

```powershell
dotnet build AssembleAI.slnx --no-restore
dotnet run --project tests/platform/AssembleAI.Platform.SmokeTests.csproj --no-build -- --model-provider-tests
dotnet run --project tests/platform/AssembleAI.Platform.SmokeTests.csproj --no-build -- --model-gateway-tests
python tools/verify-postgres.py
# 真实 OIDC / PostgreSQL / YARP / Python 与协议供应商，包含 31 秒活动委托
python tools/verify-postgres.py --model-application
# 额外真实供应商 JSON + SSE；只接受显式的本机配置和环境变量凭据
$env:ASSEMBLEAI_MODEL_ACCEPTANCE_CONFIG = 'D:\path\provider.local.json'
python tools/verify-postgres.py --model-application --real-model
```

独立协议 / HTTP 测试使用本地供应商与内存授权替身；默认实库测试验证迁移、并发配置和审计。`--model-application` 进一步在同一实际 PostgreSQL 集群串联 OIDC / BFF / YARP / uvicorn / 内部模型入口，检查 JSON / 增量 SSE / 原 JWT 30 秒后仍活动的委托、断连、撤权、停用、能力重放拒绝和来源审计。测试只使用新建临时集群，不写业务数据库。

`--real-model` 先跑上述本地组合，再对显式供应商执行一次简短 JSON 和一次 SSE，无业务数据，不做真实供应商负例。配置文件仅含 `endpoint`、`upstreamModel`、`credentialEnvironmentVariable` 和可选 `reasoningEffort`；值为环境变量名，不存密钥。运行器不打印提示词、回答或凭据，Python 子应用进程也不接收供应商密钥。与三个浏览器参数可组合；具体验证记录见 [开发记录](../development.md)。

模型界面已由T13第二批交付；统一审计查询过滤和长期故障对账继续随T13完善。多副本委托路由、分布式额度、私有化出口与网络部署验收归T14。当前不默认联系任何外部模型。
