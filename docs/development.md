# T2 · 本地工程与验证

2026-11-03：T2 工程骨架验收完成。已同步 `cf92c57` 的模板入口与前端锁文件，完成 `npm ci`、真实 `uv.lock`、项目独立 `.venv` 及统一 All 验证。开发采用本机进程；容器运行不是本阶段完成条件。真实登录与数据库联调仍在 T3。

## 不用 Docker 启动

已在当前机器实际验证以下方式：

```powershell
dotnet build AssembleAI.slnx --no-restore
& '.\apps\sample\backend\.venv\Scripts\python.exe' tools/dev-backend.py
```

启动器运行本机进程：平台 `http://127.0.0.1:5100`，FastAPI 文档 `http://127.0.0.1:8101/docs`；样例流通过 `http://127.0.0.1:5100/dev/sample/stream?count=3&delay_ms=200` 访问。Ctrl+C 结束自己启动的两个进程；端口已占用时拒绝启动，不终止其他服务。

首次使用先按下方命令安装锁定依赖。添加 `--check` 可启动、检查后自动退出。此启动方式不要求 PostgreSQL、Redis 等已经运行：`/health/live` 可用，而平台 `/health/ready` 会按真实依赖状态返回 503，不能把存活当成所有能力就绪。

后续接入真实基础服务时，使用本机安装或现有开发服务地址即可；连接配置见 [后端与基础设施说明](development-services.md)。Compose 是可选交付路径。

## 统一验证入口

```powershell
# Node、.NET、FastAPI 与实际跨服务流，自动选择项目 .venv
powershell -NoProfile -File tools/verify.ps1 -Scope Core

# 前端依赖安装后：类型检查、两项构建、浏览器测试
powershell -NoProfile -File tools/verify.ps1 -Scope Frontend

# 完整代码检查：要求真实 npm / uv 锁文件和项目 .venv
powershell -NoProfile -File tools/verify.ps1 -Scope All
```

验证器不安装依赖、不联网、不自动启动 Docker；以绝对 Node / CLI 路径执行，避免依赖 shell 命令查找。All 要求项目 `.venv`，不要传入全局 Python。缺前置条件返回非零，不能把跳过视为通过。All 仅表示代码验证，不代表数据库迁移、真实 Keycloak 登录或后续平台验收完成。

## 工程入口

| 路径 | 职责 |
| --- | --- |
| `apps/portal` | Vue 3 / Element Plus 门户开发壳层；应用列表与子应用容器 |
| `apps/sample/frontend` | Vite UMD 子应用，导出 qiankun 生命周期；同一产物独立 / 嵌入运行 |
| `packages/app-sdk` | 显示上下文类型与串行生命周期队列；不承载授权事实 |
| `services/platform` | ASP.NET Core / YARP；liveness、依赖 readiness、开发样例代理 |
| `apps/sample/backend` | FastAPI 健康检查和开发限定 SSE，无模型 / 业务数据 |
| `deploy` | 原生 psql 迁移、Keycloak 配置，以及可选 Compose 与开发凭据初始化 |
| `tests` | Node、Playwright、Python 与 .NET 烟测 |

T3 租户授权基础已实现；真实登录、应用注册中心和两个 AI 用例尚未接通。样例路由只用于本地接入验证，不可作为生产业务入口。

## 前端

### 模板入口与接入验证入口

默认首页 `/` 按用户要求恢复为 `references/ui-v1` 原始模板：Vue 直接承载模板标记，复用原始 CSS、经典脚本、品牌素材和截图；模板来源及许可继续保留。模板是运行时构建输入，不应删除。门户不加载 Element Plus 的样式，以免改变模板外观；工程验证壳层仍在 `/workspace/integration`，样例深链仍为 `/workspace/sample`，两套页面通过完整文档导航隔离。

模板登录仍是页面中明确说明的本地演示：任意非空账号、密码进入样例，其浏览器标记不会认证后端请求。工作台、应用和成员数据均保留模板演示内容；真实 OIDC/BFF 与业务 API 的接入状态没有改变。

默认访问 `http://127.0.0.1:5173/`，构建预览访问 `http://127.0.0.1:4173/`。`tests/frontend/e2e/template-portal.spec.ts` 验证模板交互，原有微前端测试从 `/workspace/integration` 进入。

Node 24.18.0 / npm 11.16.0。版本固定在 package.json。2026-11-02 本机更新已安装依赖并生成根目录 `package-lock.json`；统一 Vue 为 3.5.43 后，类型检查、两项构建、3 项 Node 测试和 6 项浏览器测试通过。后续可使用 `npm ci` 复现。

在普通 PATH 环境，从仓库根目录运行（本机超长 PATH 的处理见下一节；验证优先使用上方统一入口）：

```powershell
npm ci
npm run check
npm run build
npm run test
npm run test:e2e
npm run preview
```

根目录 `package-lock.json` 已入库；日常安装使用 `npm ci`，依赖升级时才重新生成锁文件。`npm run test:e2e` 需要匹配版本的 Chromium；缺少时使用 `npx playwright install chromium` 安装。

预览地址：`http://127.0.0.1:4173`；独立子应用：`http://127.0.0.1:4173/apps/sample/0.1.0/`。`npm run dev` 启动 5173 主应用开发服务；子应用使用构建产物，由单独运行的 4173 预览服务提供，变更子应用后需重新构建并刷新。

子应用是 library mode UMD，不是 Vite 开发 ESM 入口。`bootstrap / mount / unmount / update` 采用 qiankun 2 生命周期，版本化路径与宿主运行路由分离。样例 CSS 均限定在应用类名下，宿主启用样式隔离。浏览器验收使用相同生产构建产物验证独立 / 嵌入模式。

加载失败后的“重新加载页面”会保留当前路由并创建新文档，清空实例与资源失败缓存。该选择基于 [qiankun 2.10.16 的实例缓存](https://github.com/umijs/qiankun/blob/v2.10.16/src/apis.ts) 和 [import-html-entry 的资源缓存](https://github.com/kuitos/import-html-entry/blob/v1.17.0/src/index.js)：单纯换实例名不能清除同 URL 的失败资源请求。浏览器测试会检查文档重新加载和资源重新请求。

不依赖第三方包的检查已可运行：

```powershell
node --test tests/frontend/*.test.mjs
```

静态预览服务只暴露两项应用的构建目录，监听 loopback；缺失资源不回退成 HTML，拒绝路径穿越与写方法。

### Windows PATH 过长时

本机继承 PATH 为 8,802 字符：PowerShell 能找到 Node，但 npm 安装脚本经 `cmd.exe` 运行时找不到 `node`。只缩短子进程的 PATH 后，Node 探针与 `npm ci` 均成功。若出现同一错误，可在当前 PowerShell 执行以下安装命令；结束后恢复 PATH，不修改系统配置：

```powershell
$nodeExe = (Get-Command node.exe -ErrorAction Stop).Source
$originalPath = $env:Path
try {
    $env:Path = (Split-Path -Parent $nodeExe) + ';' + $env:SystemRoot + '\System32;' + $env:SystemRoot
    & $nodeExe (Join-Path (Split-Path -Parent $nodeExe) 'node_modules\npm\bin\npm-cli.js') ci --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw 'npm ci failed' }
} finally { $env:Path = $originalPath }
```

当前工程的安装脚本仅需 Node；若以后增加需要编译工具或 Git 的安装脚本，应保留相应工具目录。统一验证入口直接调用可执行文件，并为 Windows 浏览器测试子进程单独设置短 PATH：Playwright 回收预览服务器时需通过 `cmd.exe` 调用 `taskkill`，否则也会因超长 PATH 找不到命令而挂起。该处理不改变调用者或系统 PATH。

## 后端

从根目录：

```powershell
dotnet restore AssembleAI.slnx --locked-mode
dotnet build AssembleAI.slnx --no-restore
dotnet run --project tests/platform/AssembleAI.Platform.SmokeTests.csproj --no-build
dotnet run --project services/platform/AssembleAI.Platform.csproj --launch-profile development
```

平台运行在 `http://127.0.0.1:5100`；FastAPI 运行在 8101，启动方法见 [后端与基础设施说明](development-services.md)。生产环境不注册 `/dev/sample/*`，开发代理仅允许明确的健康与流路径，且不转发调用者的认证请求头。

`/health/live` 只说明进程存活。`/health/ready` 实际检查 PostgreSQL 迁移 2 标记、Redis PING、MinIO、RabbitMQ、OIDC discovery；任何必需依赖缺失或配置不完整时为 503。

项目独立 Python 环境的复核命令：

```powershell
& '.\apps\sample\backend\.venv\Scripts\python.exe' -m pytest -q -c apps/sample/backend/pyproject.toml tests/backend
& '.\apps\sample\backend\.venv\Scripts\python.exe' tests/platform/smoke_fastapi_proxy.py
```

跨服务烟测自动启动自己的临时进程和端口，并在结束时清理。这里只验证开发代理与流传输，不宣称已完成 T3 权限或 T5 正式应用网关。

T3 授权基础已独立推进，见 [身份与权限契约](architecture/t3-authorization.md)。新增的 .NET 测试验证双租户 / 双工厂隔离、完整授权范围匹配、主体 issuer 隔离、伪造身份拒绝、撤权后重新读取、取消传播及错误响应。身份和仓储替身只在测试工程使用；正常运行的两个受保护租户接口在接通登录前返回 401。实际数据库迁移、Keycloak 登录、BFF 会话与签名上下文仍待完成，T3 未标记验收通过。

## 最新验证记录（2026-11-03）

| 检查 | 结果 / 边界 |
| --- | --- |
| .NET locked restore | 通过；两个 packages.lock.json 已生成 |
| .NET build | 通过；0 警告 / 0 错误 |
| .NET HTTP smoke | 通过；生产禁用样例代理、请求头过滤、方法限制、SSE 分段、缺依赖 503 |
| T3 授权领域 / HTTP / ASP.NET 策略 | 通过；租户与工厂隔离、grant 不交叉拼接、停用组织、issuer 隔离、401 / 403 / 503、响应不泄漏内部组织目录、撤权及取消 |
| T3 PostgreSQL 迁移 2 | 8 张表、复合外键与策略版本触发器已实现并复核；尚未在真实数据库执行 |
| FastAPI pytest | 9 项通过；参数边界、生产关闭演示、取消 / 断开清理、错误事件 |
| FastAPI → YARP | 真实 HTTP 通过；中文 SSE、参数与 requestId 可传递，首段早于结束 |
| 原生进程启动器 | `dev-backend.py --check` 通过；本机进程启动、代理连通与退出清理；无 Docker |
| 统一验证脚本 | `-Scope All` 自然退出并返回 0；错误解释器与非零退出码的 tooling 回归通过 |
| Node 服务与生命周期测试 | 3 项通过；版本资源 / 深链、源文件隔离、串行卸载重挂、错误后重试 |
| Vue 类型检查 / 两项 Vite 构建 | 通过；UMD 样例与包含原模板的门户构建均成功 |
| Playwright | 8 项通过（5 项微前端 + 3 项模板交互）；21.2 秒完成，自动回收自己启动的预览服务 |
| 截图 | 已检查嵌入暗色和 390px 工程列表；内容可见且无水平溢出，非最终产品视觉验收 |
| npm 锁定安装 | `npm ci --no-audit --no-fund` 成功，使用入库锁文件；安装脚本所需短 PATH 仅在进程中生效 |
| Python 隔离安装 / uv.lock | 真实锁含 22 个第三方包及分发哈希；独立 `.venv` 安装、锁一致性和 9 项 pytest 通过 |
| Compose / realm / 迁移 | 已有配置与脚本；此前静态检查通过，本轮未运行容器或执行真实数据库迁移 |

已知提示：Starlette 测试客户端对 httpx 0.28.1 有弃用警告；工程壳层使用完整 Element Plus 时存在大于 500 kB 的构建分块警告；Playwright 有终端颜色环境变量提示。均非测试失败，组件按需加载与依赖升级留待对应实现任务。

## 后续验收边界

1. T3：接通真实 PostgreSQL 迁移与授权仓储、Keycloak / BFF 登录、CSRF、会话及内部签名上下文。
2. T4–T8：注册与正式网关、授权应用清单、门户业务接口与完整子应用接入；模板的本地演示登录不等于真实身份认证。
3. T14：Linux / 私有化部署、生产服务拓扑与备份恢复；Windows 可复现检查不代替这些验收。

Compose 镜像构建、digest 固定和容器拓扑验证是可选交付路径，选用时再执行。生产 SaaS / 私有化交付验收仍在 T14，不因本机开发检查通过而视作完成。

历史环境问题见 SPEC B5 / B7 / B9。2026-11-03 已恢复 Git 写入与依赖下载，Node 命令查找及 Playwright 回收问题通过子进程 PATH 处理解决；没有替身依赖或模拟微前端加载。
