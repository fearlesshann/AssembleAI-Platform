# T6 · 授权应用门户

默认入口 `/` 是正式 Vue 门户，使用企业 OIDC 登录、服务端 BFF 会话与租户选择。浏览器中的演示登录标记不参与认证。`/applications/{appId}` 可刷新进入详情；`/applications/{appId}/run` 是本次固定版本运行入口。

## 目录契约

| 接口 | 响应 |
| --- | --- |
| `GET /api/platform/v1/tenants/{tenantId}/catalog` | `{items: CatalogApplication[], categories: string[]}` |
| `GET /api/platform/v1/tenants/{tenantId}/catalog/{appId}` | 单个 `CatalogApplication` |

展示对象只有 `appId`、`name`、`releaseVersion`、`category`、`description`；名称与描述来自当前不可变发布组合。没有后端地址、权限清单、内部凭据或注册管理元数据。

每次读取都验证真实 Cookie 会话、服务端所选租户和当前成员关系；权限来自当前 PostgreSQL 查询。应用须为租户当前启用版本、全局 published、Manifest 全部入口权限满足，且前后端声明仍匹配可信部署目录。入口资源是当前租户的 `application/appId`，没有组织标识，因而需要对应权限的 tenantWide grant；不能把不同资源 / 范围条目拼接授权。

未登录 401；租户不匹配或失去成员关系 403；不存在和隐藏详情同为 404；非法参数 400；依赖故障或完整清单超过 500 个可见应用 503。响应 no-store；已认证目录响应附 nosniff。类别只由可见项生成。服务端先限定具有应用范围授权的 ID，再逐行检查全部入口权限与部署声明，整体预算 3 秒；超过上限不返回截断结果。搜索名称 / 描述 / 标识与分类组合在这份有界完整清单上执行；API 不接受查询参数。

目录不逐个探测后端存活。点击“打开应用”调用 [T5 创建运行接口](gateway.md)，重新核验权限、当前版本与后端健康；因此一个刚显示的应用仍可能在打开时被拒绝。权限事实不能由页面可见性替代。

## 会话、导航与运行

门户通过 `/bff/session`、`/api/platform/v1/tenants`、`/bff/tenant` 读取身份和选择，租户选择 / 退出 / 新建运行前获取 `/bff/csrf`。请求使用 same-origin Cookie、no-store 与禁止 fetch 自动跟随重定向；OIDC 登录由显式导航到 `/bff/login` 发起。CSRF 令牌、身份、目录和运行对象不写入浏览器持久存储；只有主题偏好保存。

切租户、退出、主体变化和失效都会清除旧目录、详情与运行，取消旧请求；异步响应按代次检查，迟到的旧租户结果不能恢复内容。浏览器恢复焦点 / 可见时重新读取会话、租户与目录。非 401 服务故障显示错误；退出失败明确提示未完成，不假装注销。页面长期可见时不宣称持续推送撤权，正式网关仍独立检查每次调用及在途请求。

运行响应仅留内存，刷新运行页不会自动 POST 新建，用户需返回详情再次打开。qiankun 仅加载运行返回的精确同源版本化入口，资源也限定在该发布物目录；SDK context 只有 appId、releaseVersion、theme 和 mode，另注入固定运行绑定的 api 能力，离开 / 到期 / 生命周期失败立即撤销。升级后仍可见的应用不会仅因目录版本变化立即卸载旧运行，其业务调用继续受网关排空期限约束。配置化资源部署、JSON / SSE 客户端和跨语言验证见 [T7 子应用模板](application-template.md)。

生命周期串行执行；资源加载 15 秒取消，mount / update / unmount 等待最多 15 秒。浏览器正常调度下，生命周期不结束或已进入挂载后的卸载失败会锁住同文档后续挂载，再完整导航回详情，显示恢复提示。这样销毁旧文档，而非用 Promise 超时假装终止任意应用 JavaScript；不会自动创建新运行。宿主导航区分同路径通知，避免 single-spa 的合成 popstate 清除刚创建的运行。

## 本机启动

前提是已配置 [BFF / 身份提供方](t3-bff.md)、真实 PostgreSQL 连接与迁移 1–7，并存在成员、完整入口授权、已发布 / 租户启用应用和受控部署声明。数据库迁移不预置用户或业务应用。运行入口还需 [网关配置](gateway.md)；仅目录展示不要求打开网关。

在平台进程的终端，将 PublicOrigin 设置为实际浏览器入口，例如构建预览的 `http://127.0.0.1:4173`。身份提供方客户端必须登记精确回调 `http://127.0.0.1:4173/signin-oidc`，其余 BFF 参数按现有说明配置，然后启动本机平台：

```powershell
$env:Authentication__Bff__PublicOrigin = 'http://127.0.0.1:4173'
dotnet run --project services/platform/AssembleAI.Platform.csproj --no-build
```

另一个终端构建并启动门户：

```powershell
$env:ASSEMBLEAI_PLATFORM_ORIGIN = 'http://127.0.0.1:5100'
npm run build
npm run preview
```

Vite 开发入口 `npm run dev` 使用同一环境变量，浏览器端口为 5173；此时 PublicOrigin 和 OIDC 回调也要同步改为 5173。两种入口的同源代理都来自 `tools/local-platform-target.mjs`。目标仅允许固定 plain HTTP loopback origin，不接受请求选择目标；保留 Cookie / Origin / 重定向 / 多 Set-Cookie 和流，剔除调用方 Forwarded，拒绝升级协议，断开时取消上游。不配置目标时 API 返回安全 503，不返回 SPA HTML。该工具仅本机开发，生产 TLS 与代理拓扑仍归 T14。

## 视觉与参考入口

默认浅色，可切换暗色；延续 SPEC 的 220px 侧栏、密集列表与详情区。按需导入 Element Plus 按钮 / 输入框，映射背景、文字、边框、焦点和状态 token。390px 下详情移到列表前，支持键盘焦点与 reduced-motion。品牌素材继续复用项目既有资产。

`/workspace/integration` 与 `/workspace/sample` 保留工程接入验证。`/reference-ui/` 保留原模板交互与许可，模板的登录和数据仍是演示；它不作为正式认证或目录结果。正式入口不加载模板 CSS / 经典脚本。

## 验证

```powershell
powershell -NoProfile -File tools/verify.ps1 -Scope Core
powershell -NoProfile -File tools/verify.ps1 -Scope Frontend
& '.\apps\sample\backend\.venv\Scripts\python.exe' tools/verify-postgres.py --portal-browser
```

Frontend 的门户用例使用明确 UI fixtures 验证状态、竞态与真实 qiankun 生命周期；原有工程和参考模板用例继续回归。`--portal-browser` 则启动真实 OIDC 协议 fixture、BFF、PostgreSQL 目录与真实构建页面，通过 Chromium 完成登录、选择、清单、详情、主题、窄屏和退出，没有拦截 API。它使用开发内存会话；真实 Keycloak 和 PostgreSQL 密文会话另有验收，不据此宣称完整生产部署。结果与截图位置见 [开发记录](../development.md)。
