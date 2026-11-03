# T7 · 子应用接入模板

样例前端与 FastAPI 后端演示受保护 JSON / SSE 调用。它使用真实 BFF、授权、运行租约和网关；计数器仍可脱离平台做界面验证。制造业业务实现继续按 T11 / T12 推进。

## 前端接入

`packages/app-sdk` 提供 `AppProps`：`context` 仅含应用 / 发布版本 / 主题 / 模式，`api` 提供固定运行实例的请求能力。qiankun 生命周期见 `apps/sample/frontend/src/main.ts`；保持 bootstrap / mount / update / unmount，使用 props.container 内的根节点，卸载时释放 Vue 实例、监听与在途工作。

```typescript
import type { ApplicationApi } from '@assembleai/app-sdk';

async function verify(api: ApplicationApi, signal: AbortSignal) {
  const result = await api.json('probe', {
    method: 'POST', body: { message: '验证调用' }, signal,
  });
  for await (const part of api.stream('events', {
    query: { count: 3, delay_ms: 150 }, signal,
  })) {
    if (part.event === 'delta') console.log(part.data);
    if (part.event === 'complete') return result;
  }
}
```

方法仅 GET / POST。操作是无编码、点段、query、URL 的相对路径，例如 `reports/query`；query 独立传数据。SDK 从复制的运行绑定生成 `/api/apps/{appId}/releases/{releaseVersion}/`，固定 X-Run-ID / X-Tenant-ID；不接受身份头、目标 URL 或任意 fetch options。Cookie 为 same-origin，每次 POST 先取得 CSRF；禁止跟随重定向，不重试。SDK 没有持久保存身份、CSRF、运行或 Token。

JSON 请求 / 响应各有 1 MiB 上限，响应须为 JSON / 有效 UTF-8；非成功 HTTP 响应只暴露稳定错误代码与状态。SSE 逐字节解析 UTF-8，单帧上限 64 KiB，支持 LF / CRLF / CR 和跨 chunk 字符。只接受带 JSON data 的 delta / complete / error；只有 complete 表示成功，error、非法帧和未完成断流抛错。退出迭代、AbortSignal 或客户端 dispose 会关闭 reader / fetch；JSON 请求 15 秒，流 600 秒，运行租约可能更早终止。后端预算仍有最终约束。

宿主每次运行创建一个 client，主题 update 复用同一 api。离开、到期、加载 / 生命周期失败立刻 dispose；15 秒生命周期超时及卸载失败按 V30 锁住文档、恢复详情。SDK 是便于正确使用的接口，qiankun 与同源 JS 均不是敌对代码安全沙箱；可信发布审核和服务端授权仍是边界。

## 独立访问

同一 UMD 发布物可直接打开 `/apps/{appId}/{frontendVersion}/`，无扩展深链刷新回到该版本入口。未由 qiankun 挂载时，样例显示“平台连接”：读取同源 BFF 会话、成员租户和选择，用户显式选择租户，再点“连接平台”创建运行。版本与当前加载发布物不匹配时引导去门户详情，不能用新运行驱动旧前端。刷新只读取状态，不自动再创建运行。

独立页面的 Cookie / OIDC 回调仍来自同一 BFF origin；它不是另起匿名后端。焦点恢复检查账号 / 选择变化，失败、变化或到期释放旧能力；服务端在每次调用和流中检查授权。原工程 PoC 没有 api 时仅保留界面验证，不伪造业务结果。

## 构建、摘要与部署

构建标识在构建期确定；应用发布组合 version 可与 frontendArtifact.version 不同。版本只接受无前导零的 x.y.z，最多 64 字符。以下从仓库根执行，在独立终端中设置构建环境：

```powershell
$env:ASSEMBLEAI_APP_ID = 'sample'
$env:ASSEMBLEAI_FRONTEND_VERSION = '0.1.0'
npm run build --workspace @assembleai/sample
$artifact = node tools/frontend-artifacts.mjs sample 0.1.0 apps/sample/frontend/dist | ConvertFrom-Json
New-Item -ItemType Directory -Force .local-data | Out-Null
@{ artifacts = @(@{ appId = $artifact.appId; version = $artifact.version; root = $artifact.root; sha256 = $artifact.sha256 }) } |
  ConvertTo-Json -Depth 4 | Set-Content -Encoding utf8 .local-data/frontend-artifacts.json
$env:ASSEMBLEAI_FRONTEND_ARTIFACTS = (Resolve-Path .local-data/frontend-artifacts.json).Path
$env:ASSEMBLEAI_PLATFORM_ORIGIN = 'http://127.0.0.1:5100'
npm run preview
```

预览前还需构建门户，首次可直接 `npm run build`。上述工具输出真实摘要和 registry 字段，不写入 dist；部署配置只接受 appId / version / root / sha256 四个字段。root 可为配置文件目录下相对路径；配置文件可放在本机数据目录，不能放进发布物目录。

摘要算法 v1：对发布物内文件按 `/` 分隔相对路径的 ordinal 顺序排序；生成 `[{path,size,sha256}]`，size 为字节数、sha256 为文件内容小写 SHA256；以紧凑 JSON 编码，加 UTF-8 前缀 `assembleai-frontend-artifact-v1\n`（末尾是一个换行），再次 SHA256。目录名和源码路径不进入摘要。

Vite / 预览启动时验证摘要并持有文件字节快照，进程运行期间磁盘变化不会替换已验证内容。更新需要新前端版本、构建、重新注册及重启对应静态服务。拒绝链接 / junction、隐藏文件、源码与 sourcemap、大小写冲突、Windows 特殊路径等。上限为 128 个发布物、每发布物 2000 个目录项 / 64 MiB、单文件 16 MiB、总计 256 MiB、目录 16 层。未知版本和缺失资产返回 404，不能回退成门户 HTML。未设置部署配置时只有开发 sample dist 快照，此默认值不构成正式注册部署证明。

将 CLI 输出的 registry 字段加入平台 `Registry:FrontendArtifacts`，并将同一 sha256 写进 Manifest.frontendArtifact。配置 `Registry:BackendBindings` 后按 [注册流程](registry.md) 保存草稿 → owner 提交 → 独立 reviewer 发布 → 有权租户管理员启用，再给业务用户 application/appId 的完整入口权限。两边的版本、摘要必须一致；没有默认管理员或演示授权。

新应用可以复制模板工程，或用 ASSEMBLEAI_APP_ID / ASSEMBLEAI_FRONTEND_VERSION 从模板构建另一份独立输出目录，再添加部署 / 注册 / 授权记录。无需修改门户路由或核心应用名单。后端复用哪个逻辑服务由 Manifest 的 backendService / backendVersion 确定；替换服务代码时仍需遵守健康、操作和内部授权契约。

## FastAPI 与平台验证

平台在既有 [BFF](t3-bff.md)、[签名证书](t3-internal-calls.md)、[网关](gateway.md) 配置上启用 `InternalCalls:ValidationEndpointEnabled=true`。验证端点默认关闭；InternalCalls 关闭而单独开启验证端点会拒绝启动。

样例的受控后端绑定与操作表：

```json
{
  "Registry": {
    "BackendBindings": [{ "Service": "sample", "Version": "0.1.0", "Audience": "assembleai:sample", "Destination": "http://127.0.0.1:8201/" }]
  },
  "Gateway": {
    "Enabled": true,
    "Operations": [
      { "Service": "sample", "Version": "0.1.0", "Method": "POST", "Path": "/probe", "Permission": "sample:use" },
      { "Service": "sample", "Version": "0.1.0", "Method": "GET", "Path": "/events", "Permission": "sample:use" }
    ]
  },
  "InternalCalls": { "ValidationEndpointEnabled": true }
}
```

这是现有配置的补充；还需真实前端摘要、BFF / 签名 / 数据库配置，不能独立启动完整平台。Manifest.permissions 应包含 sample:use，用户获该 application/appId 的 tenantWide grant。

启动后端（根目录，另一个终端）：

```powershell
$env:ASSEMBLEAI_ENVIRONMENT = 'Development'
$env:ASSEMBLEAI_VALIDATION_URL = 'http://127.0.0.1:5100/internal/calls/validate'
$env:ASSEMBLEAI_SERVICE = 'sample'
$env:ASSEMBLEAI_SERVICE_VERSION = '0.1.0'
& apps/sample/backend/.venv/Scripts/python.exe -m uvicorn sample_backend.main:app --app-dir apps/sample/backend --host 127.0.0.1 --port 8201 --no-access-log
```

验证 URL 必须固定 HTTPS；仅 Development 允许 loopback HTTP。拒绝 userinfo / query / fragment 和非固定路径，不读系统代理，不跟随重定向。Python 只发送原 Bearer、X-Request-ID 和 `{service,version,method,path}`；不能发送自选 tenant / user / permission / audience。端点不依赖 Cookie / CSRF，也不在本机浏览器代理的路径列表中；生产部署应仅向服务网络暴露，TLS 与生产拓扑见 T14。

平台从 JWT 的 app / release / tenant 候选索引查询已发布且租户启用的 Manifest，从受控部署与操作表取得 audience / permission；随后验证签名、issuer、kid、typ、全部目标字段、30 秒期限与最新授权版本，复核启用 / 发布状态，才返回 `{userId,tenantId,authorizationVersion,requestId,appId,releaseVersion}`。候选查询不将 token 内容变成可信身份。旧发布版本仍按 T5 排空，不要求 token release 等于当前租户版本指针；运行租约与会话有效性由网关持续检查。

验证请求最多 2 KiB、3 秒预算；Python 也有 3 秒整体预算，最多读取 4 KiB 严格 JSON 响应。未配置或服务不可用为 503，凭据无效 401 / 403，错误不回显凭据、目标或原始异常。内部 token 非单次使用，退出后已签发 token 的最多 30 秒窗口沿用 T3；当前策略撤销立即影响新验证查询。

样例 POST `/probe` 只接 `{message}`，1–200 个字符、最多 4 KiB，返回验证身份与关联 requestId。GET `/events` 的 count=1–20、delay_ms=10–1000；首个 delta 即时发送，每 2 秒复验原 token，遇到撤权 / 故障发 error 后结束，取消时释放验证请求和流状态。流约 20 秒内，不刷新或延长内部凭据。新增实际业务操作必须从已验证上下文继续读取并核验业务资源范围，不能把样例入口权限当成全业务数据权限。

`/health/ready` 返回 healthy / service / version / protocolVersion=1 表示后端进程生命周期就绪，不保证验证服务可达；保护接口始终失败关闭。Development `/stream` 仅为已有隔离 PoC 保留，正式 Gateway 操作表不登记它。

## 验证入口

```powershell
powershell -NoProfile -File tools/verify.ps1 -Scope Core
powershell -NoProfile -File tools/verify.ps1 -Scope Frontend
& apps/sample/backend/.venv/Scripts/python.exe tools/verify-postgres.py --portal-browser --application-browser
```

Core 包含严格内部验证 HTTP、Python 真实 loopback / uvicorn、SDK / 静态部署 Node 回归。Frontend 包含真实 qiankun 生命周期与浏览器状态测试。原生 PG 的 application-browser 选项在自有临时目录构建两个应用并计算真实摘要，经注册 / 发布 / 授权，用真实 OIDC 协议 fixture、YARP、FastAPI 与 Chromium 验证完整链路。它不替代真实 Keycloak、PG 加密会话及生产 TLS 各自的验收；具体本轮执行结果见 [开发记录](../development.md)。
