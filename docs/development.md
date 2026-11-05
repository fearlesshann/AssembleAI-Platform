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

T3 身份与授权基础已完成：登录、服务端租户选择、RBAC / ABAC、内部签名凭据及撤权已验证。T4 提供注册管理 API，T5 提供正式网关，T6 已接授权门户，T7 提供完整子应用接入，T8 提供注册管理工作区；下一步 T9 模型接入，两个 AI 用例按后续任务交付。样例路由只用于本地接入验证，不可作为生产业务入口。

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

`/health/live` 只说明进程存活。`/health/ready` 实际检查 PostgreSQL 迁移标记（统一要求迁移 10）、默认要求的 vector 0.8.7、Redis PING、MinIO、RabbitMQ、OIDC discovery；任何必需依赖缺失或配置不完整时为 503。`Readiness:RequireVector=false` 可显式限定为控制面数据库检查。

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
2. T9–T12：模型配置、数据连接与两个实际 AI 用例；T6–T8 的门户、子应用模板和注册管理已接真实身份与服务，旧模板的演示登录仍不代表认证。
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

## T7 子应用模板验收 · 2026-11-03

[子应用接入契约](architecture/application-template.md) 已交付：固定运行 SDK、JSON / 有界 SSE、取消 / dispose，独立页面同源 BFF 显式连接，FastAPI 远程复用平台签名 / 最新授权验证，以及按真实 SHA256 启动核验的版本化静态快照。样例使用入口权限与只读探针，实际制造业数据权限继续由后续业务实现。

最终 Core 返回 0：34 项 Node、锁定 .NET 还原、构建 0 警告 / 0 错误、完整身份 / 注册 / 网关 / 目录回归及新增内部验证 HTTP 边界、51 项 Python 和原有真实 FastAPI → YARP。内部验证覆盖签名篡改、目标 / requestId、版本撤销、启停、故障拒绝、默认关闭以及最后状态查询跨过 token 到期；Python 使用真实 loopback 验证服务与 uvicorn 流，覆盖超时、重定向拒绝、严格响应、周期撤权与检查中断连。httpx 通过 uv 正式从开发依赖移到运行依赖，锁定版本与现有哈希保持；uv lock / check / sync --offline 均通过。

最终 Frontend 返回 0：34 项 Node、vue-tsc、两项 Vite 构建、23 项浏览器回归。新增用例先复现主题 update 拒绝与 mount 挂起期间保留 api 仍能请求，再验证失败时立即 dispose，延迟整页恢复期间也不发新网关请求；SDK 还覆盖同 chunk 的 delta 后取消不能输出缓存 complete。最终版本类型防护也经 Node / 类型检查 / 构建 / 浏览器验证。已有 Element Plus 工程分块体积、httpx 弃用及终端颜色提示仍不影响结果。

最终 `tools/verify-postgres.py --portal-browser --application-browser` 返回 0，记录 `.artifacts/postgres/run-97be21fdd868416487cafe7071de438d/result.json` 的 passed / cleanupComplete / portalBrowserRequested / applicationBrowserRequested 均 true。迁移 1–5 与 T3–T6 实库全部回归；本次临时目录从同一模板构建 sample / sample-clone 两份真实发布物，经真实注册 / 独立审核 / 租户启用，前端版本 / 发布组合版本 / 后端版本分别验证。Chromium 经正式 Node 预览 → OIDC / BFF → YARP → uvicorn → 平台验证 → PG，覆盖独立深链 / 嵌入、JSON / SSE 首段 / 完成 / 取消、同文档跨应用重挂、刷新不自动新建运行、退出清理与无浏览器 Bearer；消费端直接使用真实签名凭据验证数据库撤权、租户 / 全局停用。

首轮组合的取消测试曾在短流结束后才完成按钮稳定等待，失败记录 `.artifacts/postgres/run-4beac675c8364e199e5eb49a688554c5`，清理成功。改为真实首段 DOM 更新时同步点击取消，不拦截网络或改变生产流时间，最终组合通过。已看过独立 / 嵌入截图，最终截图位于 `.artifacts/application-postgres/be0d90ff-9bdf-4be1-9d42-20912165f776/`；技术模板以调用结果和明确状态为主。

两个独立复核覆盖 SDK / 生命周期 / 平台验证，以及静态发布物 / Python / 联合夹具，修正与回归见 SPEC B38–B47，无剩余阻塞。组合仍使用 OidcFixture 与开发内存会话；真实 Keycloak、PG 加密会话已有独立验收，未在本轮声称生产 HTTPS / 完整多副本 / Docker 已验证。T7 完成，下一任务为 T8 注册与生命周期管理界面。

## T8 应用管理验收 · 2026-11-03

[管理工作区](architecture/registry-management.md) 接入门户侧栏及可刷新的 `/manage/applications/{appId}`。研发保存草稿 / 提交，独立审核员发布 / 驳回 / 全局启停，租户管理员启用 / 切换 / 回滚 / 停用，并查看自己权限范围内的操作记录。全局操作者无租户成员关系也能管理；租户权限不扩展为草稿或其他租户记录访问。完整列表有明确容量边界，写入使用 CSRF / CAS，冲突与未知结果不自动重试。

最终 `tools/verify.ps1 -Scope All` 返回 0，完整日志 `.artifacts/t8-final-all.log`：36 项 Node、锁定 .NET 还原与构建（0 警告 / 0 错误）、完整 HTTP 回归、51 项 Python、真实 FastAPI → YARP、Vue 类型检查、两项构建及 31 项浏览器全部通过。新增 8 项浏览器覆盖无租户管理、自审限制、409 保留输入、焦点刷新后的过期修订号、同路由确认放弃清草稿、租户迟到响应 / 退出、全局不可用时仍可停租户以及浅暗窄屏；实际 HTTP 正文阻塞测试验证退出后拒绝写入与 3 秒整体预算。

前后端交叉复核修正草稿名称泄露、旧版本列表静默截断、会话正文竞争、操作确认与草稿生命周期问题；测试夹具修正与根因见 SPEC B48–B55。已有 Element Plus 体积、httpx 弃用和终端颜色提示不改变通过结果。

最终 `tools/verify-postgres.py --portal-browser --application-browser --registry-browser` 返回 0。`.artifacts/postgres/run-33442f6f67e842ed959eadd401e972e2/result.json` 中 passed、cleanupComplete 以及三个 browserRequested 均 true。迁移 1–5、T3–T7 实库和真实 Python 子应用链路全部回归；新增管理查询验证多身份 / 草稿 / 审计隔离、撤权、500 / 501 应用、100 / 101 版本和 50 + hasMore 审计。三独立浏览器会话实际执行两版本注册 / 编辑 / 提交 / 独立发布、租户切换 / 回滚 / 停用，全局停用 / 恢复；同 BFF 会话中的网关运行验证不可变绑定、旧运行不复活、真实 SSE 排空 / 取消，SQL 检查审计主体、scope 和唯一服务器关联编号。临时集群与凭据均已清理。

浅色、暗色和 390px 界面已人工检查，无全页水平溢出；窄屏审计在独立表格容器横滑。最终截图位于 `.artifacts/registry-postgres/d18a02ca-061d-4cc7-a939-139a3ef7aa36/`。前后端独立交叉复核无剩余阻塞。组合登录使用测试 OidcFixture 的真实 Code / PKCE / JWT 交换及开发内存会话，账号选择页仅存在于测试工程；真实 Keycloak、PG 加密会话仍以此前独立验收为准。T8 完成，下一任务 T9 模型配置与 Model Gateway；两个制造业 AI 用例与生产交付按后续任务推进。

## 2026-11-03 T9 首期后端：OpenAI 兼容模型网关

本机已拉取并继续 `codex/t3-bff` 的 T8 提交 `f68a206`。用户选定 OpenAI 兼容协议；新增部署者控制的供应商 / 模型目录、环境变量凭据引用、租户启停与输出上限、`model:manage` / `model:invoke` 精确资源权限、文本 JSON / SSE 和元数据审计。配置 API 使用修订号避免覆盖并发修改，默认网关关闭且不预置模型或授权。协议和实际配置见 [模型网关契约](architecture/model-gateway.md)。本轮未改门户界面。

完整 `tools/verify.ps1 -Scope Core` 返回 0，日志 `.artifacts/t9-core.log`：锁定还原、.NET 构建 0 警告 / 0 错误、完整身份 / 注册 / 网关及新增模型 HTTP 回归、36 项 Node、51 项 Python、真实 FastAPI → YARP 全部通过。模型测试覆盖增量 UTF-8 SSE、真实 OIDC / CSRF、资源隔离、输入大小 / 重复字段 / 非法字符、用量缺失与精确计数、并发拒绝、总超时、断连、撤权 / 停用 / 退出、会话硬到期、依赖故障及审计失败。审计等待期间发生晚撤权时，不返回生成内容；已持久化的 completed 仅表示上游生成完成。模型 HTTP 的仓储为内存替身，上游为本地协议测试服务。

本机按固定校验值安装原生 PostgreSQL 17.11，`tools/verify-postgres.py` 返回 0。记录 `.artifacts/postgres/run-c925148adf66461a826bc7d895b9fccc/result.json` 中 `passed=true`、`cleanupComplete=true`。迁移 1–6 连续执行与重放、已有真实数据库 / OIDC / 网关合同均通过；新增模型测试覆盖租户隔离、并发创建 / 修改、配置与审计原子提交、调用开始 / 完成持久化、终态不可覆盖、未知 / 零 / 非加和用量、审计故障回滚、最近 50 条历史以及缺少迁移 6 时 readiness 拒绝。临时数据库、测试凭据及进程已清理。

本轮未重跑浏览器验收，未将 PostgreSQL 仓储测试与模型 HTTP 测试声称为同一条联合链路。T9 保持进行中：实际模型端点 / 模型名 / 凭据尚待配置，子应用服务端委托调用仍待接入；模型管理 UI 和审计留存 / 故障对账归 T13。现有 Starlette / httpx 弃用提示仍为非失败警告。

## 2026-11-04 T9 应用委托与真实模型联合验收

已从 GitHub 快进拉取 `10b8118`，在既有模型后端上补齐 [子应用委托](architecture/model-gateway.md#子应用服务端委托)。部署操作的 ModelIds 明确允许模型；网关签发活动调用专属能力，回调恢复真实主体 / 租户 / app / release / run，再检查当前模型授权和配置。原调用退出或取消会立即撤销能力，不能把普通业务 JWT 或浏览器伪造头变成模型权限。BFF 和委托请求共用执行器、限流、JSON / SSE、预算与审计；迁移 7 原子保存应用来源。Python 新增严格服务端客户端及 `/model-chat`，现有门户外观保持。

用户指定 DeepSeek 官方 `deepseek-flash` 并提供凭据；按 [官方 Chat Completions 文档](https://api-docs.deepseek.com/api/create-chat-completion/) 核对接口。密钥保存为本机用户环境变量 `ASSEMBLEAI_DEEPSEEK_KEY`，不在仓库；忽略文件 `.local-data/deepseek-acceptance.local.json` 只记录端点、模型、变量名与 reasoningEffort=none。思考配置仅由部署者选择，不能从调用消息覆写。供应商只收到无业务数据的两次简短验收请求。

`tools/verify.ps1 -Scope Core` 返回 0，日志 `.artifacts/t9-delegation-core.log`：36 项 Node、锁定 .NET 还原 / 构建（0 警告 / 0 错误）、全部既有与新增 HTTP 测试、91 项 Python、真实 FastAPI → YARP 回归通过。随后补充 2 项非法 finishReason 的 JSON / SSE 回归，通过独立进程旧实现副本验证先失败、当前实现通过；合计 93 项 Python 用例已验证。委托测试覆盖模型白名单、随机能力、固定身份、到期、取消、正常结束、并发撤销和容量恢复。完成前的交叉复核无身份 / 来源 / 取消边界阻塞。

完整组合 `tools/verify-postgres.py --portal-browser --application-browser --registry-browser --model-application --real-model` 返回 0。`.artifacts/postgres/run-17061489c10345a18ee6bbc48158a6f8/result.json` 的 passed / cleanupComplete 和五个 Requested 均 true。迁移 1–7 重放、全部旧实库合同、门户 / 两应用 / 注册管理浏览器回归通过；新链路为真实 OIDC / BFF → YARP → uvicorn → 模型委托入口 → 协议供应商及实际 DeepSeek，均查询真实 PG 授权、配置和审计。

本地协议阶段验证首段在供应商完成前到达、31 秒后原业务 JWT 到期但活动委托仍有效、断连、模型撤权、停用、伪造 / 已结束能力与普通 JWT 拒绝；SQL 核对每条模型调用的用户 / 租户 / 模型 / 应用 / 发布 / 运行 / 原网关请求，只有 JSON 和完整 SSE 两次为 completed。实际 DeepSeek 阶段只发一次 JSON 与一次 SSE，均返回非空内容、明确完成和可关联审计，不输出模型内容或密钥。首轮失败是夹具混用了内部 content 与 SDK text，未触达真实供应商；修正与其他回溯见 SPEC B56–B60。

最终补跑 `tools/verify-postgres.py --model-application` 返回 0，记录 `.artifacts/postgres/run-b47ff26946524832bcc3b05e89470b4a/result.json` 的 passed / cleanupComplete / modelApplicationRequested 均 true、realModelRequested=false。此轮仅本地协议供应商，在真实凭据变量名下放入测试哨兵，验证每次 Python 子进程启动均剥除该变量及验收配置路径；没有追加实际模型请求。2 项 finishReason 回归以 `-c apps/sample/backend/pyproject.toml` 独立复验通过，日志 `.artifacts/t9-python-finish-green.log`。

T9 完成，下一任务 T10 只读数据连接器。模型管理 UI、审计留存 / 故障对账继续归 T13；委托注册表要求回调路由至签发实例，多副本、分布式额度和私有化完整部署归 T14。联合登录使用 OidcFixture 的真实协议交换，既有 Keycloak 和 PG 加密会话仍由各自独立验收证明；未将此结果扩展为生产完整部署或制造业用例效果验收。

## 2026-11-04 T10 只读数据连接器验收

用户确认先使用制造业测试数据。新增[连接器接口与查询契约](architecture/connectors.md)：部署受控 PostgreSQL 源 / 数据集 / 字段和环境变量凭据引用；租户管理员使用 CAS 启停与收紧限额。真实 BFF / CSRF 保护配置、连接测试、历史与查询；每个当前启用工厂的 datasource:read / dataset:read / tool:invoke 交集在 SQL 聚合前过滤。查询只接收结构化投影、类型化 AND 筛选及五种聚合，禁止任意 SQL。专用源账号、只读事务、类型 / 行数 / 字节 / 时间上限和撤权复核共同约束执行；迁移 8 原子保存配置审计与执行元数据，不保存业务值或凭据。

最终 `tools/verify.ps1 -Scope Core` 返回 0，日志 `.artifacts/t10-core.log`。36 项 Node、锁定 .NET 还原 / 构建（0 警告 / 0 错误）、全部既有与新 HTTP 合同、93 项 Python 及实际 FastAPI → YARP 通过。新增测试验证授权交集、严格编译、审计失败零源调用或不交付数据、完成审计等待期间撤权 / 退出 / 切租户、活动取消及真实 30 秒外层截止的 timeout 审计。现有 Starlette/httpx 弃用提示不影响通过结果。

最终 `tools/verify-postgres.py` 返回 0；`.artifacts/postgres/run-54c2bac2a5584f24886cd0c540f98006/result.json` 的 passed / cleanupComplete 均 true。迁移 1–8 连续执行及重放、旧授权 / 会话 / 注册 / 模型 / 网关合同全部回归；新增配置 CAS 与原子审计、真实独立只读账号和 OIDC/BFF → PostgreSQL 查询链通过。工厂 A 的两行产量合计为 300，工厂 B 与异租户行不会参与聚合；工具权限交集为空、伪造头、SQL 注入文本、未知字段均不能扩大数据范围。持有真实表锁时，撤权、停用、3 秒超时、客户端取消均终止正在等待的数据库操作并留下终态审计。

实库边界验证 date infinity 拒绝、有限极值日期投影与筛选保持原值、numeric NaN / 超精度拒绝、UTF-8 单元格与 JSON 转义后总字节限制、列级 UPDATE 权限拒绝。先复现再修复的日期映射、外层超时分类与 JSON 小数舍入，以及验收时限 / reader 释放修正记入 SPEC B61–B65；初次 120 秒工具超时和日期红测的临时数据库均已清理。最终提交前独立代码复核无阻塞，密钥 / 私钥模式扫描未发现命中。

本轮无产品前端变更，未重跑 Chromium 或实际模型调用；组合登录使用 OidcFixture 的真实协议交换，Keycloak 保持既有独立验收。测试数据和只读角色只在隔离集群创建；没有把测试数据或连接器完成写成 ChatBI 业务问答完成。T10 完成，下一任务 T11；管理 UI / 审计对账归 T13，多实例和完整生产部署归 T14。

## 2026-11-04：T11 ChatBI 独立应用与真实模型查询

交付 `apps/chatbi/frontend` 与 `apps/chatbi/backend`，首发 `0.1.0`，服务 / 应用标识 `chatbi`。同一前端发布物通过真实注册、独立审核、租户启用后，可在门户内或独立页运行；独立页明确连接后才创建运行。数据分析工作区提供当前授权数据集、问题、制造业测试问题、执行阶段、结果表和折叠查询依据，支持浅暗主题与 390px 视口。协议及本机启动见 [ChatBI 契约](architecture/chatbi.md)。

网关签发与模型能力隔离的数据能力，内部 metadata / query / check 从活动父调用恢复可信上下文。查询复用 T10 的三类工厂权限交集、只读账号与 SQL 强制范围；原业务 JWT、模型能力和浏览器伪造头均不能替代连接器能力。迁移 9 添加连接器调用的完整应用 / 发布 / 运行 / 网关请求来源，可关联同一次提问的模型和查询审计；不保存问题、计划、业务结果或密钥。控制面 readiness 与迁移入口更新至 9。

FastAPI 一问只生成一次计划，拒绝任意 SQL、额外字段、越权列、截断生成与精度损失；原始 JSON 数字字面量送平台，结果数字以精确十进制字符串交付浏览器。模型只收到问题与语义字段定义，不收到业务行。SSE 先发送阶段，等待期间心跳，行数据为临时结果；query 后、行批次间隔达到 1 秒及 complete 前串行验证授权 / 配置版本。错误、断流、取消或变更身份不能恢复旧结果。独立 Python `uv lock --check --offline`、`uv sync --locked --offline --dev` 通过，锁定依赖与自己的 Windows venv 快照一致。

`tools/verify.ps1 -Scope All` 自然返回 0，日志 `.artifacts/t11-all.log`：41 项 Node、当时的 172 项 Python、36 项 Playwright、Vue 类型检查、三个前端构建、.NET 无警告 / 错误构建与完整 HTTP、真实 FastAPI → YARP 通过。后续交付边界修正新增 6 项回归，最终完整 Python 测试为 **178 passed**（`.artifacts/t11-python-all-final.log`），其中独立 ChatBI **85 passed**（`.artifacts/t11-python-serial-final.log`）。最终平台构建 0 警告 / 0 错误，四组 `--connector-tests` 通过（`.artifacts/t11-check-green-build.log`、`.artifacts/t11-check-green.log`）。前端接口未随收尾修正变化。

完整原生 PostgreSQL 合同加 ChatBI 浏览器运行返回 0：`.artifacts/postgres/run-24cf47dd28294168ab64555df52ce78f/result.json`。覆盖迁移 1–9 及重放、原控制面合同、真实 OIDC / BFF / YARP / Python / 数据源读取、来源审计，以及无 API 拦截的 Chromium 独立与嵌入问答。题集核对 300、200、fixture-line-a=300，未授权工厂 900 与异租户 9999 不进入结果；非法计划不访问源，撤权与断连不能完成回答，过期 / 伪造 / 业务 JWT 委托重放被拒绝。

真实供应商首轮未通过，未将失败视为成功；补充仅终态、结构、HTTP 状态及时长的诊断后，确认一次委托查询耗尽夹具的 5 秒总预算。控制回归另复现后台与最终检查并发、客户端 / 服务端同截止竞争、最终检查断连收尾，已分别修正。数据查询的短截止拒绝测试保留，ChatBI 集成夹具改用包含实时身份读取与持久审计的 15 秒预算；父状态去除重复检查，仍保留边界入口 / 出口核验与两轮配置 / 权限版本校验。相关红绿与设计边界记录在 SPEC B66–B71，不推定旧失败日志中未记录的原因。

最终运行 `tools/verify-postgres.py --chatbi-real-model --chatbi-only` 返回 0，使用 ChatBI 自己的 Python venv；`.artifacts/postgres/run-4d21207574674d2a95edb8fbdd915d9f/result.json` 的 `passed / cleanupComplete / chatbiRealModelRequested` 均 true。该次先通过本地协议、全部 ChatBI 负例和真实浏览器，再用已配置的 **DeepSeek deepseek-flash** 每题一次验证三道合成问题，结果均符合真实 PostgreSQL 基准，双调用审计关联通过。focused 模式明确只覆盖实例安全、迁移及 ChatBI 链，不冒充完整平台套件。全部失败和成功轮次的临时集群、凭据文件及子进程均已清理。

浅暗 / 移动端与门户嵌入截图已目视检查，最终浏览器产物位于 `.artifacts/chatbi-postgres/79464797c2ad46b5b4632c59394dbc1b/`。独立前后端与平台代码交叉复核无阻塞；提交前密钥 / 私钥模式扫描无命中。仍为明确标注的制造业测试数据，准确性结论限定于题集；未在本轮重跑独立 Keycloak 产品验收或部署生产服务。T11 完成，下一任务 T12 企业知识库问答；管理 UI 与审计治理继续归 T13，多实例回调 / 配额及生产交付归 T14。

## 2026-11-04 T12 文档摄取首期

同步 `39acc72` 后，按用户“首期必须包含 PDF / Word”的要求实现 DOCX、文本层 PDF、UTF-8 TXT / Markdown，单文件最多 5 MiB。提供真实 BFF / CSRF 文档上传、按租户组织过滤、受保护附件下载、带修订号删除；官方 Minio SDK 7.0.0 使用私有对象路径与环境变量凭据。独立 Python 解析服务使用 pypdf 6.21.4 和有界 OOXML 读取，每次解析在限时 / 限内存子进程中执行，保留页、段落和行号。扫描件及加密文件的安全原因保留到文档 failureCode。

迁移 10 引入文档、文本分块、持久任务和审计。当前 worker 轮询数据库任务；重复领取、60 秒过期租约、3 次失败上限、30 秒重试、当前上传者权限及删除修订围栏均覆盖。删除先写墓碑、清分块、取消旧任务，再清理对象；旧 worker 不得复活内容。文档提取成功的 extracted 不等于已创建向量或可以问答。完整配置、下载授权边界、未关联对象对账及后续范围见 [摄取契约](architecture/knowledge-ingestion.md)。

最终 `tools/verify.ps1 -Scope Core` 返回 0，日志 `.artifacts/t12-core.log`：锁定还原、.NET 构建 0 警告 / 0 错误、完整 HTTP 回归（含知识库对象 / worker / OIDC HTTP）、41 项 Node、201 项 Python及真实 FastAPI → YARP 均通过。新增解析测试包含实际生成的 PDF / DOCX、异常 ZIP/XML、扫描 / 加密文档、分块定位、私有接口鉴权，以及真实受限子进程完成 / 超时 / 取消。

实库首轮发现 BFF 禁用时 DELETE 处理器缺少 Antiforgery 服务注册，已修复。最终 `tools/verify-postgres.py` 返回 0，`.artifacts/postgres/run-ec0f062eaeaa4d17abab801a49b4a9c5/result.json` 的 passed / cleanupComplete 均 true，PostgreSQL 17.11；迁移 1–10、知识库任务与删除竞争、失败原因分类、原子审计及既有数据库合同全部通过，临时数据库与进程已清理。

验证分别覆盖受控 S3 HTTP fixture、内存仓储 HTTP / worker、真实 PostgreSQL 仓储和真实解析子进程；没有将其声称为真实 MinIO 联合部署。未新增或重验浏览器界面，未调用真实模型。T12 保持进行中：真实 MinIO 联合验收、RabbitMQ、embedding / pgvector、知识库前端与问答引用待继续。已有 Starlette / httpx 弃用提示为非失败警告。

## 2026-11-05 T12 知识库索引与问答

拉取 `bbda377` 后补齐独立 `knowledge` 前后端 0.1.0、固定 multilingual-e5-small CPU embedding、pgvector 精确检索与 RabbitMQ 持久通知。迁移 11 保存受约束向量和检索审计；PostgreSQL 继续作为任务事实源。索引分批持久化，完整 revision/profile 才发布 ready；授权在排名前过滤，精确文档权限不能与另一 grant 的组织范围交叉组合。删除墓碑同事务清分块 / 向量并取消旧任务。独立活动知识库能力与模型、连接器能力隔离，全部模型上下文在调用前和答案交付前重新授权。详见[问答契约](architecture/knowledge-qa.md)。

界面提供文档上传、范围、处理状态、删除确认、原文附件和问答引用。同一发布物支持独立与门户运行、浅暗主题及 390px 视口；取消、切身份 / 租户和焦点复核失败清除旧内容。答案只在完整 SSE 通过后显示，引用 quote 必须为实际命中原文的连续子串；不持久化答案历史。旧 extracted 文档仍需启用索引后重新上传，当前没有重建索引接口。模型引用校验不等于证明答案全部自由文本结论，效果必须以实际题集为准。

`tools/verify.ps1 -Scope All` 返回 0，日志 `.artifacts/t12-all.log`：48 项 Node、201 项原有 Python、109 项独立知识库 Python、44 项 Playwright、Vue 类型检查、四个前端构建、.NET 全部 HTTP 及真实 FastAPI → YARP 通过。知识库 Python 测试明确配置了本机固定模型目录，真实 ONNX CPU 测试没有跳过。后续父片段 Unicode / 控制字符边界红绿完成，最新 .NET 构建 0 警告 / 0 错误、知识库六组通过，日志 `.artifacts/t12-parent-green-build.log` 与 `t12-parent-green.log`。浏览器 maxlength 的 UTF-16 截断经实际输入复现后修复，类型检查、知识库重建和最终 9 项专项通过（`.artifacts/t12-question-green.log`）。这使当前浏览器用例总数为 45；完整44项与新增后9项专项分别执行，不冒充一次45项全跑。

核心原生 PostgreSQL 串行复验返回 0，`.artifacts/postgres/run-29be6c581eea402497587dbb8cd7f6af/result.json` 的 passed / cleanupComplete 均 true。覆盖迁移1–11及重放、全部既有授权 / BFF / 注册 / 网关 / 模型 / 连接器合同和知识库任务；原无 vector 安装继续验证扩展缺失负例。首轮与全套浏览器并行时触发旧240秒外层预算，未将中止视为通过；外层预算600秒不改变任何业务请求截止。手动浏览器红测的服务收尾另触发既有B9长PATH问题，仅回收已核实的自有进程树；最终专项采用验证器相同的短进程PATH并自然退出。

真实 MinIO 对象字节往返、RabbitMQ 持久队列发布 / 消费以及 AMQP 端口握手通过；`.artifacts/knowledge/run-c9fc6761d24840dd8cb2bc8a0a13cd21/result.json` 的四项结果均 true，含 cleanup。服务以普通本机进程运行，不依赖 Docker。原生工具、固定官方依赖、隔离目录及本机容量说明见[原生验收](native-knowledge.md)。此独立烟测仍不替代平台 worker 与实际 pgvector / 模型的联合验收。

固定官方 MSVC / Windows SDK 以只读解包方式补齐后，pgvector 0.8.7 从锁定源码原生编译成功（`.artifacts/t12-native-vector-linked.log`），生成独立 PostgreSQL 17.11 目录，原无 vector 安装未改。`--knowledge-index-only` 返回0；`.artifacts/postgres/run-0195ecf6f9304e1690f8c014b4672bbd/result.json` 的 passed / cleanupComplete 均 true。真实扩展验证可恢复分批索引、重复提交、精确向量排名前ACL、授权版本、精确资源 / 组织范围、删除与撤权围栏，readiness 要求真实vector和迁移11。

真实原生浏览器联合验收最终返回0：`.artifacts/postgres/run-069cdb60fea84521b098cae52125f8ff/result.json` 的 passed / cleanupComplete / knowledgeBrowserRequested 均 true。实际 DOCX / PDF 原件经 MinIO、生产 RabbitMQ 客户端、私有解析与CPU模型、pgvector、BFF / YARP / 独立业务服务完成问答；检验重复通知、跨工厂及异租户排除、全部上下文撤权 / 删除复核、依据不足拒答、关联检索 / 模型审计。无 API 拦截的 Chromium 验证独立 / 门户、真实上传到ready、原件下载逐字节一致、删除、浅暗 / 移动端及跨标签退出清理。截图 `.artifacts/knowledge-postgres/d4a49fb81e1f435198e632a4e4906114/` 已目视检查。

首轮浏览器夹具误把 Chromium 未暴露 File 正文当成上传丢失；改为真实下载逐字节验证。后续删除负例揭示本地开发代理剥离传输头后丢弃无显式长度的 DELETE 正文；已按实际分帧协议修复，保留删除负例，新增HTTP红绿与完整 **49项Node** 通过（`.artifacts/t12-proxy-framing-green.log`）。另一次初始化失败来自夹具未等租户提交便打开独立页，实际页面加延迟HTTP复现后按权威提交 / 读取同步，不放宽原断言时限。上述失败轮次均未记为通过，隔离资源均清理；根因与边界记录于SPEC B72–B81。原生 Windows shell 在工作区产生的本机缓存已精确忽略，不提交数据库缓存或依赖二进制。

最终 `tools/verify-postgres.py --knowledge-real-model` 返回0：`.artifacts/postgres/run-e868293794b24e83ab32619f4a01d81c/result.json` 的 passed / cleanupComplete / knowledgeRealModelRequested 均 true。该轮再次通过本地完整链路与浏览器，然后使用已配置 **DeepSeek deepseek-flash** 每题一次验证三题：轴承过热先停机（DOCX）、切削前冷却液压力0.35 MPa（PDF）、资料未包含的火星班车票价明确拒答。两种原文引用、全部授权上下文和同一网关调用的审计关联均核对。凭据仅由平台环境读取，不传给业务 / 解析服务或浏览器，不记录问题、模型正文或密钥。

T12完成，下一任务T13管理与审计。本轮为普通原生进程，无Docker；效果结论只覆盖合成题集，不等同于真实制造业务准确率或生产部署。未重跑独立Keycloak产品验收；联合登录使用既有真实OidcFixture协议交换。大规模向量性能、组织管理体验、历史治理、多实例回调与完整生产交付继续归后续任务。原文提取状态extracted不自动回填索引，启用后需重新上传；缓存 / 编译包保留情况见原生依赖说明。

## 2026-11-05 T13 第一批：身份管理功能与页面

按用户确认的平台优先方向，保留 ChatBI 与知识库两个用例，不新增业务应用。新增用户、租户、成员、组织、角色权限五类正式管理页，提供列表 / 搜索 / 深链详情、创建 / 编辑 / 启停、成员角色分配、精确资源与平面组织范围及最近操作。全局管理无需业务成员关系；页头显示实际管理租户，业务租户选择保持独立。新增授权默认无范围，须明确选择组织或整个租户；双主题、窄屏横滚导航与键盘可达均检查。详见[身份管理合同](architecture/identity-management.md)。

迁移12增加显式平台 / 租户身份管理员、独立修订号与审计。既有developer / reviewer与业务角色不取得身份管理权；全局身份管理员也不自动获得业务数据。严格请求类型、CSRF、真实BFF、锁后会话复核、当前授权及CAS保护写入；最后管理员不可停用，OIDC绑定不可改绑。变更 / 授权版本 / 审计同事务，失败不留下成功审计。正式初始化入口使用Node内置模块和原生psql，安全传入中文 / 引号，精确重跑不覆盖身份，不种默认账号。

管理集合上限500，完整响应上限4MiB。新增在事务内检查数量，所有新增与编辑还检查含Unicode转义、UUID关系及审计预留的保守响应预算；超限返回明确容量错误并回滚。页面对容量拒绝保留可编辑输入，用户可缩减后手动保存；修订冲突保留输入并要求重新读取。保存成功但回读失败明确提示已提交，不自动重试。独立复核发现并修正类型自动转换、数量与聚合预算问题；锁等待竞态、深链、checkbox及Windows参数编码根因记录在SPEC B82–B88。

`tools/verify.ps1 -Scope All`返回0，日志`.artifacts/t13-all.log`：53项Node、201项原有Python、109项知识库Python、56项Playwright、Vue类型检查、四个前端构建、.NET完整HTTP及真实FastAPI→YARP通过；真实本机ONNX测试显式设置模型目录，没有跳过。随后聚合预算修正后的完整Node再验**56项通过**（`.artifacts/t13-final-node.log`），类型检查 / 门户构建和身份管理最终**12项浏览器专项**通过。56项全套浏览器与新增后12项专项是分别执行，不声称一次57项全跑；后续改动限定身份管理预算与容量错误交互，既有两个用例代码未修改。

原生专项`--identity-only --identity-browser`最终返回0：`.artifacts/postgres/run-90a91b706be64acb80015c0fa2e1766c/result.json`的passed / cleanupComplete / identityBrowserRequested / identityOnly均true。真实OIDC / BFF / PostgreSQL / Chromium覆盖五类管理操作、普通 / 开发审核 / 租户 / 全局角色隔离、CSRF与错误输入、并发CAS、审计故障原子回滚、同会话撤权，以及在pg_locks确认等待后切租户 / 退出均拒写。数量边界和低于单请求64KiB但聚合超4MiB的长授权、Unicode资料及已有角色扩容回滚均有实库红绿；引导执行实际Node / psql，没有替换生产接口或拦截浏览器API。

最终完整`--identity-browser`返回0：`.artifacts/postgres/run-922273e9fab941c1ba3f80992f4c9cb8/result.json`中passed / cleanupComplete / identityBrowserRequested为true，identityOnly为false。该轮使用最终代码和构建产物，包含上述身份 / 数量 / 聚合预算及浏览器验收，同时通过全部既有PG授权、会话、注册、目录、模型、连接器、知识仓储及正式网关链路；迁移1–12重放和缺迁移readiness拒绝均通过。隔离数据库和临时凭据清理完成。

浅 / 暗 / 390px的正式页面截图已目视：`.artifacts/t13-identity-ui/`及真实链路`.artifacts/identity-postgres/f4876e37-4cd5-4ffb-b5e5-5cc2a5339ef2/`；最终完整复验截图在`.artifacts/identity-postgres/3d7dd7b7-43e1-4cbc-8973-466fce30f50f/`。本批完成独立代码复核；数量与响应预算修复后无审查阻塞项。迁移不包含默认身份，现有ChatBI临时体验启动器也不自动授予身份管理员；真实部署需按合同显式初始化。

T13保持进行中，下一批为模型 / 数据连接管理页，再推进统一调用 / 审计 / 异常、任务诊断和D9留存 / 导出。最近50条身份操作记录不等于统一审计已交付。本轮未重跑真实模型问答或独立Keycloak产品验收，未部署生产服务；原生进程开发仍不要求Docker。


## 2026-11-05 T13 第二批：模型、数据源与数据集管理

正式门户新增`/manage/models[/modelId]`和`/manage/data-sources[/sourceId]`，提供授权列表 / 搜索 / 深链详情、租户配置、受控数据集与逻辑字段、主动模型调用 / 连接测试、取消测试及近期50条调用记录。管理、模型调用、连接测试、业务查询权限分别校验；身份管理员不隐式获得资源权限。部署停用与租户配置分开显示，端点、物理表 / 列、凭据及环境变量引用不进入页面。合同与初始化说明见[资源管理](architecture/resource-management.md)。没有新增AI用例、依赖或迁移，继续使用迁移1–12与原生进程。

列表增加独立能力、部署最大限额和实际生效限额的安全投影。配置CAS和原子审计沿用既有表，修订号保持浏览器安全整数范围。配置事务按固定顺序锁定身份与配置表、目标配置行及外键身份行，再检查当前授权版本、精确权限和真实BFF会话；审计写后、提交前再检查一次。等待期间的退出、切租户或撤权须使配置、修订与成功审计一并回滚。模型未配置时明确返回JSON null，避免合法空配置被有界客户端误判。

页面覆盖未配置 / 空数据 / 无权 / 模块故障、未保存离开、修订冲突保留输入、已提交但回读失败、401清除、换租户及迟到响应。测试没有自动重试；取消后要求重新读取，调用结果只在页面内存。可访问字段名与部署范围说明分开，使用真实label定位回归；浅色、暗色与390px截图已目视检查。独立代码复核检查锁序、权限、会话与DTO边界，最终未发现新增P1/P2。

最终代码回归`tools/verify.ps1 -Scope All`返回0，日志`.artifacts/t13-resources-all.log`：**61项Node、201项原有Python、109项知识库Python、67项Playwright**全部通过；Vue类型检查、四个前端构建、.NET完整HTTP及真实FastAPI→YARP通过。显式设置本机ONNX模型目录，知识库真实embedding测试没有跳过。.NET构建0警告 / 0错误；已有Vite包体积和Starlette测试库弃用提示保留，不通过增加依赖扩大本批范围。

首轮原生专项`--resource-only --resource-browser`在`.artifacts/postgres/run-70575e7ef75a49c49df1bbea14756f67/result.json`记录passed / cleanupComplete为true；该轮验证真实OIDC协议/BFF/PostgreSQL、浏览器配置/CAS、一次loopback模型调用、独立只读角色连接测试、调用与配置审计、身份管理员与业务权限分离、租户隔离、退出及主题 / 窄屏。此专项不代表全量PG回归。随后增加外键父行及审计锁等待测试，旧实现于`run-f3767731e20943b889a00b55cbba3e82`失败，退出后返回并非预期403，已据此完善事务前后复核；失败轮次清理完成，不计作通过。

模型空配置HTTP红证据为`.artifacts/t13-resources-backend-red.log`；资源标识含点导致静态深链404、表单帮助说明混入可访问名称、测试夹具Assert命名冲突等根因与修正见SPEC B89–B93。浏览器null协议夹具另修为真实JSON正文；不把空body夹具误判为后端再次失效。

最终完整`tools/verify-postgres.py --resource-browser`返回0：`.artifacts/postgres/run-41a13bc7eba34e1cabf3634e87ea2d71/result.json`的passed / cleanupComplete / resourceBrowserRequested为true，resourceOnly为false。该轮使用最终程序集与门户构建，迁移1–12及重放、身份初始化 / 容量 / 聚合预算、授权、PG会话、注册、目录、网关、模型、连接器和知识仓储基线全部通过。模型 / 数据源各6项真实HTTP竞争测试（表锁退出、切租户、权限撤销、配置行锁退出、外键父行锁退出、审计锁退出），合计**12项**均403且配置 / 修订 / 审计无越权提交；最终资源页面的持久化、CAS、实际调用、权限、主题 / 390px / 深链 / 退出也通过。测试数据库、凭据与进程清理完成。

最终截图在`.artifacts/resource-postgres/74b349dd-1021-4388-8623-3a6dd028b059/`（model-light / source-dark / source-mobile），另有前端状态专项`.artifacts/t13-resources/`。本批实际模型调用使用loopback协议夹具，数据源使用独立PostgreSQL只读角色；未重跑实际DeepSeek问答、真实Keycloak产品或生产部署验收。既有两个用例通过代码回归，未新增业务用例。T13仍保持进行中，下一批统一调用 / 审计 / 异常、任务诊断及D9留存 / 导出；T14 / T15生产交付与性能验收仍在后续范围。


## 2026-11-05 T13 第三批：统一审计与用户示例设计校准

后续纠正：本节记录的token / 部分尺寸对照未覆盖原登录页与完整控件，不能视为用户要求的严格复用通过。相关视觉结论按SPEC B100撤回，完整组件纠正与后续验收见下方记录；本节后端审计功能的实际验收记录保持有效。

正式门户新增`/manage/audit[/source/id]`及`/manage/audit/global[/source/id]`。统一10类调用 / 操作 / 导出记录，支持固定时间窗、来源 / 结果 / 应用 / 请求号筛选、保护游标分页、详情和网关请求关联。原有精确资源与身份管理边界继续生效，新增网关观察、知识审计和导出权均显式授予；身份管理员不隐式获得业务记录。合同见[统一审计](architecture/audit-console.md)。迁移13增加历史查询索引及导出审计，readiness要求1–13。

JSON导出最多1000条，保留truncated与exportedAt；记录仍逐条按查询权限过滤。持久化导出审计前后及响应前复核权限和实时BFF会话，故障或失效时不交付数据；提交后的最后检查若拒绝，审计可能已经存在。页面明确取消、403和不确定失败不能推断审计未写，不自动重试。不自动清理历史；单次31天窗口不等于生产留存期，生产容量、归档与备份恢复仍待后续。

用户本轮明确要求严格复用原示例的底层设计。新增[设计合同](architecture/reference-design-contract.md)、共享reference-tokens及本地Inter 4.1字体，将正式门户和身份 / 应用 / 模型 / 数据源 / 审计管理的颜色、字重、外壳、导航、按钮、输入、表格密度与状态对齐到原HTML/CSS。232px侧栏、44px顶栏、28px导航 / 按钮、40px应用行及40/44px表头 / 行以实际浏览器对照验证；业务内容不照搬。保留原始快照不改，没有外部运行时字体请求。

独立复核检查权限交集、global / tenant范围、历史资源、游标与导出时序，发现提交后403文案遗漏，已修复并加浏览器回归。实库浏览器还发现390px页面被表格sr-only绝对定位内容撑宽；滚动容器建立定位上下文后修复，保留键盘聚焦与局部横滚。测试选择器、约束夹具、导出提示和窄屏根因见SPEC B94–B99。未增加AI用例；ChatBI和知识库继续兼容回归。

最终`tools/verify.ps1 -Scope All`返回0，日志`.artifacts/t13-audit-all.log`：**66项Node、201项原有Python、109项知识库Python、79项Playwright**通过；包含8项新审计交互及4项原示例实际样式对照，窄屏溢出回归并入审计交互测试。Vue类型检查、四个前端构建、.NET完整HTTP及真实FastAPI→YARP均通过。.NET构建0警告 / 0错误；本机ONNX模型目录显式配置，真实embedding测试未跳过。保留既有Vite包体积、Starlette及测试颜色环境提示，不将提示隐藏或描述为全部依赖验收。

早期纯HTTP专项`.artifacts/postgres/run-e6669b17624e4137a0a99bd429ea145e/result.json`的passed / cleanupComplete为true。后续浏览器轮次分别暴露标签定位、390px辅助文本溢出，以及全量轮`run-70de80d38e0e4aac803a70a0af0e0b16`中权限选项尚未加载就断言的测试同步问题；这些失败轮不计作验收通过，均已清理。最后一项修为所有账号等待工作区加载完成与真实记录表出现后再检查权限，保留拒绝和空态的专门测试。

最终完整`tools/verify-postgres.py --audit-browser`返回0：`.artifacts/postgres/run-e253529ced0845e59bdbda09b9d4a2aa/result.json`的passed / cleanupComplete / auditBrowserRequested为true，auditOnly为false。包含迁移1–13及重放、缺13的readiness拒绝、身份初始化 / 容量 / 授权、PG会话、注册 / 目录 / 网关、模型、连接器和知识仓储基线；本批多来源稳定分页、各来源精确资源 / tenant / global隔离、参数 / 时间 / 来源 / 结果筛选、篡改及跨主体游标拒绝、请求关联、安全元数据、未完成与窗外记录、1000条截断及导出审计故障均通过。4类真实锁等待后的退出 / 切租户 / 撤权及导出审计回滚通过。浏览器直接经过OIDC协议服务、BFF及PostgreSQL完成查询、翻页、详情、关联、JSON下载、范围权限、深链、主题 / 390px和退出；没有拦截业务API。数据库、临时凭据与进程清理完成。

最终浅色 / 暗色 / 390px截图在`.artifacts/audit-postgres/9dc886a5-912f-4536-a2cb-2cf668149d86/`，原示例与正式外壳对照在`.artifacts/reference-design/`，均已目视检查。本轮未重跑真实Keycloak产品或DeepSeek效果验收，也未部署生产服务。T13保持进行中：下一批为任务、运行与依赖诊断；T14 / T15继续稳定交付、生产留存、备份恢复和性能验收。

## 2026-11-05 原示例完整组件纠正

用户再次明确要求先查示例，已有实现必须复用，不能只用相近token替换。本轮按SPEC V37 / B100撤回此前严格一致的结论，调整C4 / C6、AGENTS、PRODUCT和[设计合同](architecture/reference-design-contract.md)。原始`references/ui-v1`快照保持不改；没有新增AI用例，也不将本轮UI纠正视为T13剩余功能完成。

正式登录复用原双栏页面、三张叠图、字体、输入 / remember / forgot、按钮亮边、波纹和背景效果，实际表单交身份服务验证。Keycloak登录、只需密码的重认证、找回密码、更新密码、信息 / 错误页使用同一原壳。门户401和本地退出请求OIDC `prompt=login`；BFF只接受该可选单值，不声称退出其他系统的SSO。隔离预览也使用同一表单并验证测试凭据，密码不交Portal处理、不保存在浏览器。

原shell CSS按作用域生成，原自定义下拉、日期 / 月 / 年面板、42日格、小时分钟惯性滚轮、输入 / 按钮、分页直接移植；保留来源与MIT许可。应用 / 身份 / 资源 / 审计管理使用完整data-grid工具栏、滚动表体、页脚和原drawer / dialog。保留真实权限、修订冲突、草稿保护、取消和迟到响应约束。两个AI用例及工程sample的可见控件也使用同一实现；各独立发布物内含原版字体，控件运行时支持qiankun卸载重挂。

实际复查修复了源控件接入中的问题：关闭草稿确认时的状态校验、下拉父值拒绝回滚、表格 / 抽屉中的弹层裁切、跨bundle重复事件、手机根box-sizing及导航可达、长应用滚动、骨架加载类、全局reset优先级、overlay继承色及登录canvas未绘制。SPEC B101–B112记录对应根因与回归。对照范围扩展到实际展开态、对话框、字体特征及三个已绘制canvas的逐字节像素，未通过放宽样式值迁就实现。

验证结果：

- Vue类型与四个应用构建通过，日志`.artifacts/reference-ui-typecheck.log`、`reference-ui-build-{sample,chatbi,knowledge,portal}.log`。最终完整浏览器**90项通过 / exit 0**，日志`.artifacts/reference-ui-frontend.log`；源 / 实际双主题table、下拉展开、日期滚轮、drawer、confirm截图位于`.artifacts/reference-components/`，已目视检查。此前overlay对照失败记录保留在`reference-ui-frontend-pre-overlay-fix.log`。
- **66项Node、201项平台 / 用例Python、109项知识库Python**通过；.NET完整HTTP smoke、0警告 / 0错误构建及真实FastAPI→YARP通过。日志分别为`.artifacts/reference-ui-{node,python,knowledge-python,smoke,proxy,dotnet-build}.log`；保留既有Vite体积和Starlette提示。
- 原登录独立浏览器对照在深浅主题 × 1440 / 1366 / 390六组通过，覆盖26个区域、焦点、勾选、三个canvas像素、原生POST与无凭据存储。真实Keycloak `--startup-timeout 300` 验收通过，`.artifacts/keycloak/run-aea27c5dd5334fa88d2b5df8450a9974/result.json`为passed / cleanupComplete=true：包括错误密码、remember Cookie、强制重认证、真实loopback SMTP邮件与action token、新密码和错误页面。详见[原生身份验收](native-identity.md)。
- 完整PostgreSQL组合`--registry-browser --identity-browser --resource-browser --audit-browser --chatbi-browser`通过，`.artifacts/postgres/run-c21c4f34ebb34c62aad7533cbb5ff81d/result.json`为passed / cleanupComplete=true，非focus模式。覆盖完整核心合同和五类真实浏览器链路，权限 / 401 / CAS / 撤权 / 迟到响应断言保留。前一轮并行负载下connector的5秒请求504，不计通过；未修改业务超时，停止其他重负载后的完整重跑通过。
- Knowledge原生专项通过，`.artifacts/postgres/run-2090f14e2e024084b5ac922669c89c94/result.json`为passed / cleanupComplete=true。覆盖真实DOCX / PDF、MinIO / RabbitMQ、本地CPU E5 / pgvector、上传 / 索引 / 下载原文 / 删除、独立 / 嵌入问答、引用 / 拒答 / 撤权 / 审计。截图在`.artifacts/knowledge-postgres/c37a92b76003457d8b39b70487650f32/`。

本轮未调用外部真实模型、未部署生产环境；Keycloak使用隔离H2和BFF开发内存会话，PostgreSQL浏览器使用真实OIDC协议夹具，二者不合并宣称生产HTTPS / PG联合身份验收。全部验收服务、数据库、邮件令牌和临时凭据均按所属运行清理。

最后单独重启4180本机展示预览时，实际浏览器发现测试身份服务的`form-action 'self'`拦截跨回环端口的BFF重定向（B112）；仅允许已校验callback origin后，重新构建0警告 / 0错误、`--bff-tests`全部通过。新预览`run-35b8f17d80a44bd5b0403e1d040bc327`实际浏览器验证原表单账号密码登录、显式初始化管理员、选择租户、10个平台页面、日期面板及退出后401通过，日志`.artifacts/reference-preview-verify.log`，截图`.artifacts/reference-preview/`。没有拦截业务API。此展示进程另外保留最多四小时，使用隔离制造业测试数据与固定3个查询规划；管理员和业务授权在该次临时数据库显式配置，启动器不自动授予正式管理权。

## 2026-11-05 原示例动效与整页滚轮补齐

用户指出仍缺动效和平滑滚动。复查确认上一轮90项测试主要验证静态 / 减少动效及控件状态，不能证明整页运动行为完整：原全局滚轮脚本只在参考路由加载；正式视图 / 刷新未触发原动画类，modal被立即移除，门户和登录还有无条件OS减少动效覆盖。SPEC B114 / V37补充正常动效验收，不再把静态像素相同视作动效已接入。

`sync-reference-wheel.mjs`把原`inertialWheelScroll.js`生成ES模块，仅改变导出边界；速度、阻尼、反向、输入识别及嵌套边界算法保持。门户、管理表格 / 抽屉、两个AI和sample通过共享文档运行时接入，卸载停止RAF并释放最后一个所有者的监听。切页、实际请求完成后的内容淡入、用户菜单按原类名和时序触发；抽屉 / 确认用Vue leave保留220ms退出阶段，父组件改用open状态。无新动效库、无模拟加载等待。登录和门户统一原示例的默认full，显式system跟随系统、reduced禁用；三个登录Canvas恢复原动态判断，Keycloak资源同步。

本轮验证：

- 正常滚轮先失败、修复后与原页面逐帧轨迹比较通过；新增7项浏览器测试覆盖真实鼠标、连续减速、嵌套边界、行 / 页单位、反向、停止、精细输入 / 横向 / 缩放 / 时间轮所有权、系统偏好、切页 / 刷新 / 菜单及弹层实际进出。已有qiankun测试增加独立bundle只响应一次及微应用卸载后主界面继续滚动。最终完整浏览器**97项通过，exit 0**，`.artifacts/reference-motion-frontend.log`。
- Vue类型检查、四个Vite构建及**66项Node**通过，日志`.artifacts/reference-motion-typecheck.log`、`reference-motion-build-{portal,sample,chatbi,knowledge}.log`、`reference-motion-node.log`。保留既有Vite体积提示。未改后端合同，本轮未重复无关后端 / 模型验收。
- 登录新增正常 / system / reduced连续帧验证，OS减少动效下默认三个Canvas均持续变化，明确减少动效则静止；六组原版浅暗 / 视口 / 几何 / 焦点 / 像素及真实表单POST验证仍通过，`.artifacts/reference-motion-login.log`。资源同步不等同本轮重新运行完整Keycloak产品验收。
- 旧4180预览已按所属run清理，新`run-2c6e371c77644140bc5f8fc78c6195f7`使用最新构建，真实BFF / 测试OIDC / PostgreSQL登录、10个平台页面、日期面板和退出401通过。另在OS减少动效环境实测登录Canvas、切页、真实数据源抽屉入场 / 退出与鼠标连续滚动，无业务API拦截、无浏览器运行错误。日志`.artifacts/reference-motion-preview-bootstrap.log`、`reference-motion-preview.log`；截图`.artifacts/reference-motion-preview/`已目视检查。脚本同步误判记B116，未改变产品并发保护。

预览保留至2026-11-05约21:25（上海时间），仍为隔离制造业测试数据与固定查询规划；测试账号demo / AssembleAI-2026!，管理员与业务授权只在该临时实例显式配置。原始快照不改，T13继续进行中，未增加AI用例或部署生产。
