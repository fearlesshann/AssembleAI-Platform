# T2 · 本地工程与验证

当前：工程源码、本机后端启动与核心检查已完成；**T2 尚未验收完成**。剩余项为前端依赖安装 / 完整锁定 / qiankun 浏览器 PoC，以及 Python 干净环境锁定。用户已明确先不用 Docker；容器运行不是本阶段完成条件。

## 不用 Docker 启动

已在当前机器实际验证以下方式：

```powershell
dotnet build AssembleAI.slnx --no-restore
& 'E:\Users\Mryeh\miniconda3\python.exe' tools/dev-backend.py
```

启动器运行本机进程：平台 `http://127.0.0.1:5100`，FastAPI 文档 `http://127.0.0.1:8101/docs`；样例流通过 `http://127.0.0.1:5100/dev/sample/stream?count=3&delay_ms=200` 访问。Ctrl+C 结束自己启动的两个进程；端口已占用时拒绝启动，不终止其他服务。

其他机器用已安装依赖的 Python 3.13 解释器替换路径。添加 `--check` 可启动、检查后自动退出；本轮该模式已通过，检查后没有保留服务进程。此启动方式不要求 PostgreSQL、Redis 等已经运行：`/health/live` 可用，而平台 `/health/ready` 会按真实依赖状态返回 503，不能把存活当成所有能力就绪。

后续接入真实基础服务时，使用本机安装或现有开发服务地址即可；连接配置见 [后端与基础设施说明](development-services.md)。Compose 是可选交付路径。

## 统一验证入口

```powershell
# 当前机器已通过：Node、.NET、FastAPI 与实际跨服务流
powershell -NoProfile -File tools/verify.ps1 -Scope Core -Python E:\Users\Mryeh\miniconda3\python.exe

# 前端依赖安装后：类型检查、两项构建、浏览器测试
powershell -NoProfile -File tools/verify.ps1 -Scope Frontend

# 完整代码检查：要求真实 npm / uv 锁文件和项目 .venv
powershell -NoProfile -File tools/verify.ps1 -Scope All
```

验证器不安装依赖、不联网、不自动启动 Docker；以绝对 Node / CLI 路径执行，规避当前会话的 npm 子进程命令查找问题。Core 已返回 0；缺前置条件的 All / Frontend 已验证返回 2；错误解释器的非零退出码也能传递。All 仅表示代码验证，不代表数据库迁移、真实 Keycloak 登录或后续平台验收完成。

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

正式身份认证、租户授权、应用注册中心和两个 AI 用例尚未实现。样例路由只用于本地接入验证，不可作为生产业务入口。

## 前端

### 模板入口与接入验证入口

默认首页 `/` 按用户要求恢复为 `references/ui-v1` 原始模板：Vue 直接承载模板标记，复用原始 CSS、经典脚本、品牌素材和截图；模板来源及许可继续保留。模板是运行时构建输入，不应删除。门户不加载 Element Plus 的样式，以免改变模板外观；工程验证壳层仍在 `/workspace/integration`，样例深链仍为 `/workspace/sample`，两套页面通过完整文档导航隔离。

模板登录仍是页面中明确说明的本地演示：任意非空账号、密码进入样例，其浏览器标记不会认证后端请求。工作台、应用和成员数据均保留模板演示内容；真实 OIDC/BFF 与业务 API 的接入状态没有改变。

默认访问 `http://127.0.0.1:5173/`，构建预览访问 `http://127.0.0.1:4173/`。`tests/frontend/e2e/template-portal.spec.ts` 验证模板交互，原有微前端测试从 `/workspace/integration` 进入。

Node 24.18.0 / npm 11.16.0。版本固定在 package.json。2026-11-02 本机更新已安装依赖并生成根目录 `package-lock.json`；统一 Vue 为 3.5.43 后，类型检查、两项构建、3 项 Node 测试和 6 项浏览器测试通过。后续可使用 `npm ci` 复现。

在可访问 npm registry 的环境，从仓库根目录运行：

```powershell
npm install
npm run check
npm run build
npm run test
npm run test:e2e
npm run preview
```

安装成功后将生成的 lockfile 纳入工程，再使用 `npm ci` 复现。`npm run test:e2e` 需要匹配版本的 Chromium；缺少时使用 `npx playwright install chromium` 安装。

预览地址：`http://127.0.0.1:4173`；独立子应用：`http://127.0.0.1:4173/apps/sample/0.1.0/`。`npm run dev` 启动 5173 主应用开发服务；子应用使用构建产物，由单独运行的 4173 预览服务提供，变更子应用后需重新构建并刷新。

子应用是 library mode UMD，不是 Vite 开发 ESM 入口。`bootstrap / mount / unmount / update` 采用 qiankun 2 生命周期，版本化路径与宿主运行路由分离。样例 CSS 均限定在应用类名下，宿主启用样式隔离；这些源码选择仍须浏览器 PoC 验证。

加载失败后的“重新加载页面”会保留当前路由并创建新文档，清空实例与资源失败缓存。该选择基于 [qiankun 2.10.16 的实例缓存](https://github.com/umijs/qiankun/blob/v2.10.16/src/apis.ts) 和 [import-html-entry 的资源缓存](https://github.com/kuitos/import-html-entry/blob/v1.17.0/src/index.js)：单纯换实例名不能清除同 URL 的失败资源请求。相关浏览器测试会检查文档重新加载和资源重新请求，尚待依赖安装后执行。

不依赖第三方包的检查已可运行：

```powershell
node --test tests/frontend/*.test.mjs
```

当前受限会话中 npm 脚本子进程无法解析 `node`，但直接执行上述 Node 命令通过；未把 npm 脚本入口报告为成功。静态预览服务只暴露两项应用的构建目录，监听 loopback；缺失资源不回退成 HTML，拒绝路径穿越与写方法。

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

本机已安装 Python 环境的实测命令：

```powershell
& 'E:\Users\Mryeh\miniconda3\python.exe' -m pytest -q -c apps/sample/backend/pyproject.toml tests/backend
& 'E:\Users\Mryeh\miniconda3\python.exe' tests/platform/smoke_fastapi_proxy.py
```

跨服务烟测自动启动自己的临时进程和端口，并在结束时清理。这里只验证开发代理与流传输，不宣称已完成 T3 权限或 T5 正式应用网关。

T3 授权基础已独立推进，见 [身份与权限契约](architecture/t3-authorization.md)。新增的 .NET 测试验证双租户 / 双工厂隔离、完整授权范围匹配、主体 issuer 隔离、伪造身份拒绝、撤权后重新读取、取消传播及错误响应。身份和仓储替身只在测试工程使用；正常运行的两个受保护租户接口在接通登录前返回 401。实际数据库迁移、Keycloak 登录、BFF 会话与签名上下文仍待完成，T3 未标记验收通过。

## 初始工程验证记录（模板恢复前）

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
| 统一验证脚本 | Core 返回 0；缺前端依赖 / 锁文件时 Frontend / All 返回 2；错误路径检查通过 |
| Node 服务与生命周期测试 | 3 项通过；版本资源 / 深链、源文件隔离、串行卸载重挂、错误后重试 |
| TypeScript 语法 | 使用缓存 TS 5.9.3 的 transpileModule 检查，8 个 TS + 2 个 Vue script 共 10 项通过；不等于类型检查或 Vue 模板编译 |
| Compose / realm / 脚本 | YAML / JSON / PowerShell 语法与 shell LF 静态检查通过；未运行容器 |
| 前端完整构建 / vue-tsc / Playwright | 未执行成功；依赖安装受阻 |
| Python 隔离安装 / uv.lock | 未完成；只有已测试 Windows 环境的精确版本快照 |

## 未完成的 T2 验收

1. 恢复 npm 安装条件，生成 lockfile，执行 vue-tsc 与两项前端构建。
2. 执行 Playwright：独立访问、qiankun 加载、重复卸载重挂、深链刷新、加载失败重试、主题同步及小屏布局；检查截图。
3. 完成 `uv lock` 与干净隔离环境安装，不用本机预装环境代替可复现验证。
4. 当前阶段采用已验证的本机进程启动；基础服务可按后续任务需要接本机或现有开发环境。实际数据库迁移、Keycloak discovery 与全依赖 readiness 的集成检查按相应任务完成，不以安装 Docker 为前提。

Compose 镜像构建、digest 固定和容器拓扑验证是可选交付路径，选用时再执行。生产 SaaS / 私有化交付验收仍在 T14，不因本机开发检查通过而视作完成。

环境证据：npm 在线 EACCES；离线缓存存在部分真实 tarball，但缺少 qiankun / single-spa / import-html-entry，部分 packument 也不齐；PyPI OS 10013；uv 缓存没有所需包，真实离线锁定失败于 fastapi==0.140.0。现有 22 包 Windows 快照的 31 条适用依赖约束均满足，但不代替干净安装。Docker 已卸载，当前不需要恢复它。未用替身包或模拟加载代替 qiankun。工作区 `.git` 写入仍受限，本轮未提交。
