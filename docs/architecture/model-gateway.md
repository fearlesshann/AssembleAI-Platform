# T9：OpenAI 兼容模型网关

2026-11-03 用户选定 OpenAI 兼容协议，也支持本地推理服务。当前交付文本 Chat Completions 的后端配置、租户授权、JSON / SSE 调用和元数据审计。真实供应商地址、模型名和凭据尚未指定；本地协议测试服务不代表真实模型验收，T9 保持进行中。模型管理界面归 T13，子应用服务端的委托调用入口仍待接入。

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

先执行统一 PostgreSQL 迁移（1–6），再按 [身份与授权规则](t3-authorization.md) 授权。`model:manage` 和 `model:invoke` 相互独立，资源类型为 `model`、资源 ID 为目录别名（示例 `assistant`）。当前模型为租户级资源，需要 tenant-wide 范围授权。管理员通过下述配置 API 为租户启用模型；没有租户配置即禁用。

## 平台接口

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
- 非流式上游响应最多 1 MiB；SSE 单行最多 64 KiB、单事件最多 256 KiB，逐段解析 UTF-8，不缓存整条生成。完整协议依据 [Chat Completions 官方说明](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create)。

## 验证入口与剩余验收

```powershell
dotnet build AssembleAI.slnx --no-restore
dotnet run --project tests/platform/AssembleAI.Platform.SmokeTests.csproj --no-build -- --model-provider-tests
dotnet run --project tests/platform/AssembleAI.Platform.SmokeTests.csproj --no-build -- --model-gateway-tests
python tools/verify-postgres.py
```

协议与 HTTP 测试使用明确的本地测试供应商；HTTP 路径经过真实 OIDC Code / PKCE、BFF Cookie / CSRF，授权与模型存储使用内存测试替身。数据库测试由原生 PostgreSQL 启动器创建临时隔离集群，验证迁移重放、租户隔离、并发修订和审计持久化，不使用业务数据库。两组验证不能代替真实供应商联调，也不代表 PostgreSQL 与模型 HTTP 的完整联合验收。

T9 剩余：实际端点 / 模型 / 凭据配置后的 JSON 与 SSE 验收、子应用服务端委托调用接入，以及选定部署环境的网络出口和额度策略确认。模型界面、审计查询过滤和长期故障对账随 T13 完善。
