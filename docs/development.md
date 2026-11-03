# 本地工程与验证

2026-11-03：T1–T6 已验收。正式门户默认接 BFF、租户与授权目录，支持分类搜索、详情和运行入口；Core、前端 21 项浏览器回归及真实 PG / OIDC / 浏览器目录联合通过。开发采用本机进程，不要求 Docker。

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

T3 身份与授权基础已完成：登录、服务端租户选择、RBAC / ABAC、内部签名凭据及撤权已验证。T4 提供注册管理 API，T5 提供正式网关，T6 已接授权门户；下一步 T7 完整子应用接入，两个 AI 用例按后续任务交付。样例路由只用于本地接入验证，不可作为生产业务入口。

## 前端

### 正式门户与参考入口

默认首页 `/` 已接正式 BFF 授权门户；详情路径 `/applications/{appId}`，包含浅暗主题、租户选择、分类搜索与运行入口。[配置与目录契约](architecture/portal.md) 说明同源代理、PublicOrigin 和实际身份 / 数据前提。只启动静态预览而未配置后端时，会显示服务错误，不模拟登录。

`references/ui-v1` 原始模板保留在 `/reference-ui/`，继续使用其原始 CSS / 经典脚本、品牌素材和许可。模板登录、工作台及成员数据仍是演示，浏览器标记不会认证正式门户。工程验证壳层在 `/workspace/integration`，样例深链在 `/workspace/sample`；各入口通过完整文档导航隔离样式。模板仍是参考入口构建输入，不应删除。

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

`/health/live` 只说明进程存活。`/health/ready` 实际检查 PostgreSQL 迁移标记（统一要求迁移 5）、默认要求的 vector 0.8.7、Redis PING、MinIO、RabbitMQ、OIDC discovery；任何必需依赖缺失或配置不完整时为 503。`Readiness:RequireVector=false` 可显式限定为控制面数据库检查。

项目独立 Python 环境的复核命令：

```powershell
& '.\apps\sample\backend\.venv\Scripts\python.exe' -m pytest -q -c apps/sample/backend/pyproject.toml tests/backend
& '.\apps\sample\backend\.venv\Scripts\python.exe' tests/platform/smoke_fastapi_proxy.py
```

跨服务烟测自动启动自己的临时进程和端口，并在结束时清理。这里只验证开发代理与流传输，不宣称已完成 T3 权限或 T5 正式应用网关。

T3 已完成 [身份与权限契约](architecture/t3-authorization.md)、[OIDC / BFF 与租户选择](architecture/t3-bff.md) 和 [内部签名调用](architecture/t3-internal-calls.md)。登录和内部签名均默认关闭，外部配置齐全才启用。真实 Keycloak 浏览器验收覆盖身份服务兼容性；原生 PostgreSQL 联合验收串起协议身份服务、数据库会话 / 成员、租户选择、签名 HTTP 下游及撤权。两个验收相互补充，生产 HTTPS 和完整发布拓扑在 T14 验证。

## 最新验证记录（2026-11-03）

| 检查 | 结果 / 边界 |
| --- | --- |
| .NET locked restore | 通过；两个 packages.lock.json 已生成 |
| .NET build | 通过；0 警告 / 0 错误 |
| .NET HTTP smoke | 通过；生产禁用样例代理、请求头过滤、方法限制、SSE 分段、缺依赖 503 |
| T3 授权领域 / HTTP / ASP.NET 策略 | 通过；租户与工厂隔离、grant 不交叉拼接、停用组织、issuer 隔离、401 / 403 / 503、响应不泄漏内部组织目录、撤权及取消 |
| T3 PostgreSQL 核心迁移 1–3 | 原生 17.11 真实执行与幂等重放通过；无预置账号 / 租户 / 会话；错库拒绝，应用角色非超级用户 |
| T3 PostgreSQL 授权 | 实际 uuid[] / 参数化查询、双租户 / 双工厂、复合外键、策略版本、事务回滚和提交后撤权通过；HTTP 使用真实仓储验证 401 / 403 / 503 |
| 控制面 / vector 边界 | 原迁移缺 vector.control 红测后显式拆分；核心完整执行；扩展缺失 0A000；默认 readiness 数据库项 unavailable，显式控制面模式 ready，但缺其他依赖仍 503 |
| T3 BFF / OIDC HTTP | 通过；code + PKCE S256、RSA JWT、issuer / audience / signature / nonce / state / correlation 拒绝、固定回调、无 token Cookie、CSRF / Origin、退出重放与八小时到期 |
| T3 租户选择 | 通过；无默认租户、同源 / CSRF、双租户切换、明确目标匹配、旧 Cookie / 多标签页、撤权、503、原 issued / expiry / token 保留、退出 / Renew 竞争及取消 |
| T3 内部凭据 | 通过；RS256 / 仅公钥消费、30 秒零宽限、issuer / aud / typ / kid、重复字段 / 算法替换 / 错目标拒绝、当前策略版本、轮换 / 撤 key 及仅公钥 JWKS |
| T3 PG / HTTP 联合链路 | 通过；OIDC 协议服务 → 真实数据库会话与租户选择 → 签名 HTTP 下游；未选 / 错租户拒绝，撤权后新调用与尚未过期旧 token 拒绝，退出重放拒绝；无浏览器内部 token |
| T3 真实原生 Keycloak / Chromium | 通过；JDK 25.0.5+8、Keycloak 26.8.1，错误密码拒绝、真实登录 / 主体、Cookie / token 隔离、CSRF / Origin、重放拒绝、SSO 再进入；最终运行与凭据清理均返回 0 |
| T3 生产密钥持久化 | 通过；实际 RSA PFX 加密 keyring，重建应用后恢复，异证书 / 无私钥 / ECDSA / 缺文件拒绝，临时证书与 key 清理 |
| 原生身份依赖复现 | 官方 ZIP 长度和 SHA256 匹配；安装 / 重复使用 / 版本探针通过；冲突 JAVA、外部 DB、Production 与 Playwright 调试变量未干扰最终验收 |
| T3 会话存储 | 内存副本 / 容量通过；真实 PostgreSQL bytea 密文、独立 provider 与存储实例恢复、错误证书 / 篡改 / key 绑定、UTC 秒精度到期、撤销 / 续期竞争、取消和连接失败脱敏通过 |
| 原生 PostgreSQL 依赖与清理 | 官方 ZIP 长度 / SHA256、17.11 版本与重复安装通过；独立 SCRAM / loopback 实例验收返回 0，临时集群与凭据删除，无残留 postgres 进程 |
| T3 本轮 Core | 通过；锁定还原、.NET 构建 0 警告 / 0 错误、HTTP smoke、Node 3 项、Python 9 项及真实 FastAPI → YARP |
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
| Compose / realm | 已有配置与脚本；Compose 初始化仍执行 core + vector，本轮未运行容器；原生数据库与身份验收分别记录 |

已知提示：Starlette 测试客户端对 httpx 0.28.1 有弃用警告；工程壳层使用完整 Element Plus 时存在大于 500 kB 的构建分块警告；Playwright 有终端颜色环境变量提示。均非测试失败，组件按需加载与依赖升级留待对应实现任务。

## 后续验收边界

1. T3 身份与授权基础已验收，不代表全产品所有业务资源已完成 V4–V7 最终验收。Keycloak 浏览器使用 H2 / 内存会话；PG 联合测试使用协议身份服务、真实数据库和测试 host 下游；生产 HTTPS、跨机器和完整部署仍在 T14。
2. T7–T8：完整子应用 SDK / 受控请求能力和注册管理界面；T6 门户已接真实身份与目录，旧模板的演示登录仍不代表认证。
3. T14：Linux / 私有化部署、生产服务拓扑与备份恢复；Windows 可复现检查不代替这些验收。

向量扩展成功安装与知识库业务验收继续在 T12；本轮核心迁移成功不表示向量检索已就绪。原生数据库成功记录：`.artifacts/postgres/run-0bc82b0de23c456d8d6bc91bb9f0deaf/result.json`；`passed` 与 `cleanupComplete` 均为 true。启动脚本及验收判据修复见 SPEC B17–B19。

新增信任链联合验收记录：`.artifacts/postgres/run-4423450349cc4c1a9ba6da9851c4dbf8/result.json`，验收与清理均通过。租户切换、非法参数和 Windows CNG 私钥兼容修复见 SPEC B20–B22。独立复核未发现阻止提交的问题；T3 结项范围已对照原任务确认。

本轮完整 Core 再次退出 0；真实 Keycloak 浏览器回归 `.artifacts/keycloak/run-600e263855f044e098199c4447bdf4ae` 也退出 0 并清理完成。前端未改动，沿用之前的 8 项浏览器验收，不将工程模板视为已接真实业务登录。

Compose 镜像构建、digest 固定和容器拓扑验证是可选交付路径，选用时再执行。生产 SaaS / 私有化交付验收仍在 T14，不因本机开发检查通过而视作完成。

历史环境问题见 SPEC B5 / B7 / B9。2026-11-03 已恢复 Git 写入与依赖下载，Node 命令查找及 Playwright 回收问题通过子进程 PATH 处理解决；没有替身依赖或模拟微前端加载。

## T4 注册控制面验收 · 2026-11-03

[注册与部署契约](architecture/registry.md) 和 [Manifest schema](../contracts/manifests/README.md) 已提供。最终原生 PostgreSQL 运行 `.artifacts/postgres/run-fcdfc3e3870247f8b438919660eb20b1/result.json` 返回 0，passed / cleanupComplete 均 true。迁移 1–4 及重放、严格 HTTP 命令、独立审核 / 自审拒绝、发布后不可变、租户选择 / 授权撤销、并发 CAS、回滚和审计故障回滚全部通过。T4 测试使用 OIDC 协议 fixture、开发内存会话与真实注册数据库，生产部署仍归 T14。

Core 回归通过：锁定还原、.NET 构建 0 警告 / 0 错误、原有身份及内部凭据测试、新 Manifest / 匿名读写测试、Node 3 项、Python 9 项与真实 FastAPI → YARP。已有 FastAPI TestClient 的 httpx 弃用提示不影响通过。新增 bug 及回归见 SPEC B23–B25；数据库空 JSON 负测先失败，修正 CHECK 的 NULL 处理后通过。独立助手额度限制由主线程接手复核，未声称取得新的独立复核结论。

T4 为注册控制面 API 结项。资源实际部署 / 健康探测、正式动态路由 / 运行授权、门户清单与管理界面分别按 T5 / T6 / T8 继续；测试未启动 Docker。

## T5 正式网关验收 · 2026-11-03

[正式网关契约](architecture/gateway.md) 已交付。`powershell -NoProfile -File tools/verify.ps1 -Scope Core` 返回 0：锁定还原、.NET 构建 0 警告 / 0 错误、已有身份 / 注册 / 内部签名回归、新增会话实时复核与网关 HTTP 链路、Node 3 项、Python 9 项和真实 FastAPI → YARP 均通过。网关链路覆盖首段 SSE、签名验证、请求 / 响应头及日志隔离、编码负例、客户端断开、撤权 / 停用 / 租户切换 / 退出取消、首头 / 空闲 / 总时限、限流拒绝与恢复。

最终原生 PostgreSQL 运行 `.artifacts/postgres/run-dd8b27ca47c845ca9ce24e6d7702c8c8/result.json` 返回 0，`passed=true`、`cleanupComplete=true`。迁移 1–5 与重放、T3 / T4 实库回归、租约 / 排空 / 永久撤销 / 容量 / 审计故障，以及生产网关 → 真实 PG → 签名 HTTP 下游组合通过。组合测试实际修改数据库权限与注册状态，并确认正在传输的流取消和后续调用拒绝；不是以替身仓储推断实库行为。

独立复核发现并修正了 YARP 原始 URL 日志、错误响应清理后的安全头丢失、编码边界及期限延长问题，见 SPEC B26–B32 / V29。最后追加的延迟开始审计测试经重新构建及 `dotnet tests/platform/bin/Debug/net10.0/AssembleAI.Platform.SmokeTests.dll --gateway-tests` 验证返回 0：耗尽预算后下游请求计数不增加，已创建调用记录仍正常终结。最终独立代码复核未发现阻止提交的问题。

T5 限流为单实例；正式配置默认关闭，需显式配置 BFF、内部签名、可信服务版本与操作表。此次未改门户 UI，未运行 Docker；真实 Keycloak 浏览器、PG 加密会话各有独立验收，本轮组合使用 OidcFixture 和开发内存会话，完整生产 HTTPS / 多副本部署仍归 T14。下一阶段 T6 接入真实授权应用门户。

## T6 授权门户验收 · 2026-11-03

[目录与门户契约](architecture/portal.md) 已交付。默认首页接真实 BFF，支持显式租户选择、授权清单、分类 / 搜索、详情深链、固定版本运行入口、浅暗主题与 390px 布局。旧模板移到 `/reference-ui/`，工程 PoC 路径保持。Vite 和构建预览共用固定 loopback 同源代理；未配置后端时明确失败。

最终 `tools/verify.ps1 -Scope Frontend` 返回 0：Node 8 项、类型检查、两项 Vite 构建、Playwright 21 项通过（13 项正式门户 + 原有 5 项微前端 / 3 项模板）。正式门户用例覆盖 CSRF、运行刷新不自动创建、服务失败 / 撤权、切租户迟到响应、退出失败、同页二次运行、身份恢复故障，以及挂起生命周期 / 卸载失败后的整页恢复。独立复核发现的问题均先浏览器复现，再修复并回归，见 SPEC B33–B37 / V30。恢复前锁住后续挂载，避免以超时假装旧应用已清理。

完整 Core 同轮返回 0：锁定 .NET 还原、构建 0 警告 / 0 错误、身份 / 注册 / 网关 / 目录边界回归、Node 8 项、Python 9 项及真实 FastAPI → YARP。两种 scope 分别执行，未把它们写成一次 `-Scope All` 运行。已有工程 Element Plus 大分块、httpx 弃用及终端颜色提示仍为非失败警告；正式门户按钮 / 输入框已按需导入。

最终 `tools/verify-postgres.py --portal-browser` 返回 0；记录 `.artifacts/postgres/run-6e31c712c5fc469f89c3251ab8fe7dd3/result.json` 的 passed / cleanupComplete / portalBrowserRequested 均 true。真实目录 HTTP 验证完整权限、隐藏类别、跨租户、停用 / 撤权 / 版本一致性、500 / 501 容量边界；Chromium 实际完成 OIDC → BFF → PG 清单 / 详情、主题、窄屏与退出，不拦截 API。T3–T5 数据库与签名网关组合也全部回归；临时数据库和凭据清理完成。

浅色清单、暗色详情和窄屏截图位于 `.artifacts/portal-postgres/9c8169a2-b6df-4cf4-ae49-9214e6568132/`。已检查信息层级、主题、品牌标识及无水平溢出；独立权限与生命周期复核均无阻塞问题。本轮使用 OidcFixture / 开发内存会话，原生 Keycloak 和 PG 密文会话的独立验收继续保留；完整子应用请求 SDK 在 T7，生产拓扑在 T14，不把参考样例当成制造业 AI 用例完成。
