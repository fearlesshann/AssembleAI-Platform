# T1 · 开工实施决策

日期：2026-11-02。对应 [SPEC T1](../../SPEC.md#t-实施任务)。

后续状态以 [开发记录](../development.md) 为准。2026-11-02 的 `cf92c57` 按用户要求让原样模板成为当前演示首页的构建输入，工程壳层移至 `/workspace/integration`；下文关于模板不参与构建、待安装及 Git 限制为 T1 时的历史记录，已被后续实现替代。

状态：T1 设计与文档核验完成；运行实现与集成验收在 T2–T7 完成。**用户已确认**：先使用 Keycloak，保留 OIDC 接口。下文其余“采用”项为本轮实施决定，可按后续证据调整，不追溯为原始 Word 中的确认事项。

## 1. 旧 UI 与工程边界（D3）

已读取 GitHub `main` 提交 `f99202e32269ffcb411b36077ed329ea3b9d6f7e`；原样保存 29 个跟踪文件及哈希。它是静态样例，无真实登录、业务后端或微前端接入。详见 [复用评估](ui-reuse-audit.md)。

采用单仓组织源码与契约；平台控制面采用模块化单体，应用前后端独立构建与发布。单仓不要求应用共用运行进程或发布版本。

规划目录如下；当前仅文档与 references 已落盘，其余由 T2 生成：

```text
apps/
  portal/                   Vue 主应用：门户与平台管理
  sample/frontend/          qiankun 接入验证用例（不计入两个 AI 用例）
  sample/backend/           最小 FastAPI / 流式接入验证
  chatbi/frontend/          ChatBI 独立前端
  chatbi/backend/           ChatBI FastAPI
  knowledge/frontend/       知识库独立前端
  knowledge/backend/        知识库 FastAPI
services/
  platform/                 ASP.NET Core：BFF、YARP 与控制面模块
packages/
  app-sdk/                  生命周期 / 平台上下文类型与客户端适配
contracts/
  manifests/                JSON Schema、合法与非法样例
  api/                      平台 API、错误与 SSE 契约
deploy/
  compose/                  本地依赖及配置模板
  keycloak/                 开发 realm / client 模板，无真实凭据
tests/                      跨组件契约、集成与端到端验证
docs/architecture/          决策与核验依据
references/ui-v1/           原样 UI 样例，不进入生产构建
```

前端使用 npm workspaces 与单一 lockfile；.NET 使用 solution + 集中包版本；Python 应用各有 pyproject / uv.lock 和隔离环境。共享包限于接入契约与技术适配，不引入应用业务代码。

## 2. 身份与租户信任链（D1）

### 登录与会话

- 采用 Keycloak 作为开发身份服务；平台只依赖标准 OIDC discovery、授权码、JWKS 和退出协议。生产可通过配置接其他 OIDC 提供方。
- ASP.NET Core BFF 使用 confidential client、Authorization Code + PKCE。浏览器只持 HttpOnly 会话 Cookie；访问 / 刷新令牌保留在服务端会话存储，不放入 localStorage、子应用 props 或浏览器可读 Cookie。
- 会话使用服务端存储与可撤销标识；生产 Cookie 使用 Secure、适当 SameSite、限定 Path / Host。变更请求使用防伪令牌并验证来源；GET 不承担启停、登出或权限变更等写操作。
- Keycloak 负责认证与身份凭据；平台数据库负责租户、组织、成员、角色、策略。用户主键绑定 `(issuer, subject)`，不能用可变的 email 作为唯一身份依据。
- 开发先采用一个 realm 承载测试身份，平台显式建立两个租户、每租户至少两个组织的授权测试数据；不把 realm 简单等同租户。

依据：[Microsoft OIDC / BFF](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-oidc-web-authentication?view=aspnetcore-10.0)、[防伪请求](https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0)、[Keycloak OIDC 端点](https://www.keycloak.org/securing-apps/oidc-layers)。服务端会话、租户模型与后续内部授权上下文为本项目设计决定。

### 请求授权

1. BFF 验证会话；API 认证失败返回 401，交互登录入口才发起 OIDC 跳转。
2. 浏览器提交租户选择作为请求意图；平台用当前用户成员关系验证。缓存包含用户、租户和授权版本；缺少明确租户上下文时拒绝租户数据请求。
3. Gateway 验证应用可用性、租户启用及入口权限；移除外部传入的内部身份 / 权限请求头。
4. 平台为本次调用生成短期、签名、限定目标应用 audience 的内部调用授权令牌，内容包含主体、租户、应用发布版本、权限范围、策略版本、requestId 和有效期。它是服务端内部授权凭据，不代替 OIDC 登录，也不是把 ID token 用作业务 API 凭据。
5. 应用后端校验平台 issuer / JWKS、签名、audience、有效期，再校验业务操作权限。连接器根据可信上下文重新约束数据集与组织范围；不接受 Prompt 提供的授权事实。
6. 下游工具需要独立 audience 或通过受控能力接口调用，不把应用 audience 的令牌任意透传。过期或策略版本失效时拒绝并重新授权。

内部签名密钥由部署密钥设施管理，公钥通过受控 JWKS 发布并支持轮换；私钥不进入源码。具体 claim schema、签名算法、有效期、缓存撤权上限和轮换窗口在 T3 固化；未经验证的上下文永不作为授权依据。

2026-11-03 T3 已固化并验收：[内部凭据契约](t3-internal-calls.md) 采用 RS256、独立 typ、30 秒零宽限、目标 / requestId 绑定和双向实时授权；受控证书文件加载公钥，JWKS 提供对外分发格式，暂不自动远程刷新。签发与消费不缓存授权，策略版本不一致即拒绝。租户选择见 [BFF 接口](t3-bff.md)，正式网关映射仍按 T4 / T5 完成。

### 独立前端访问

同一子应用发布物支持独立模式和 qiankun 模式。独立域名也通过本域受控入口接平台 BFF / API，以 Keycloak SSO 建立该域会话；不复制其他域 Cookie，不开放携带凭据的通配 CORS。各域回调地址显式登记。

子应用仅调用注入的请求适配器，或独立模式下的同契约适配器。任何来自浏览器的 tenantId 与权限显示状态都需要后端重新验证。UI 共享主题与路由上下文，不共享密钥或访问令牌。

## 3. 应用注册、版本与发布（D2）

### 记录与权限语义

采用五类记录：

| 记录 | 内容 / 归属 |
| --- | --- |
| Application | 全局稳定 appId、名称、分类、所有者；不存动态实例 |
| ApplicationRelease | appId + releaseVersion；绑定前端发布物版本 / 摘要、后端逻辑服务与版本、Manifest / 权限版本；发布后不可变 |
| ServiceBinding | 受平台管理的逻辑服务与版本 → 受信任服务发现目标；不接受普通应用调用者提供 URL |
| TenantApplication | tenantId + appId；租户是否启用、当前活动发布版本、并发控制版本 |
| Review / Audit | 申请者、审核者、动作、版本、时间、结果、requestId |

保留原 Manifest 七个字段的含义。`version` 定义为发布组合版本，前端与后端可有不同版本，但由一个不可变发布记录绑定。`permissions` 采用“全部满足”的应用入口要求；可选功能权限另列声明，不能全部塞进入口权限。

`status` 是服务端管理状态；提交 Manifest 不直接获得 published 状态或权限。权限声明只描述所需权限，实际授权仍由平台策略决定。JSON Schema、版本兼容与具体 API 在 T4 固化。

### 审核与租户启用

- 应用开发者：创建应用、编辑草稿、提交审核。
- 平台发布审核者：审核、发布、全局禁用；发布者不能自行批准自己的申请。
- 租户管理员：在平台已发布版本中选择启用 / 停用与活动版本，管理本租户授权；不能修改全局发布物。
- 普通用户：仅能查看和使用当前租户对其授权的应用。

状态采用 `draft → pending_review → published → disabled`；驳回返回 draft。已发布内容修改必须新建版本。未改发布物的 disabled 版本可经发布权限检查及审计重新启用；租户启停是独立字段，不改全局发布状态。

平台管理权限与业务数据权限分离；平台审核者不因管理应用而自动获得租户制造数据权限。

### 发布与回滚

1. 上传不可变前端资源，部署对应后端版本，配置服务寻址并完成健康 / 兼容检查。
2. 审核通过后发布该组合；不能先切门户入口再异步补后端。
3. 租户启用或升级通过带并发控制的事务更新活动版本指针；失败保留原指针，记录结果。
4. 门户一次解析 releaseVersion、frontendEntry 和 API base；同次运行固定版本，避免旧前端调用新后端。
5. 版本化调用入口拟为 `/api/apps/{appId}/releases/{releaseVersion}/`；服务端重新验证租户、发布与运行授权。稳定入口若保留，只用于解析活动版本，不能让每次业务请求隐式漂移。
6. 升级前启动的会话可在明确的排空窗口内访问被固定的旧版本；普通用户不能靠猜旧版本地址新建旧版运行。运行授权与排空期限由服务端记录，T4 / T5 定义具体协议。
7. 显式停用拒绝后续新请求；在途流按预定取消策略处理。回滚切回仍可用的兼容发布记录，必须审计；旧发布物保留到排空和回滚窗口结束。

数据库变更采用兼容升级；破坏性迁移不能靠切换应用指针撤销。T14 单独验证数据库迁移与恢复。实例扩缩容只改变服务发现，不改变 Manifest 或业务版本。

## 4. 运行时与微前端构建（D3）

| 项 | 决定 / 证据 | T2 剩余工作 |
| --- | --- | --- |
| .NET | 目标 net10.0；本地 SDK 10.0.102 | 生成 global.json，固定经验证补丁；restore / build |
| YARP | 基线 2.3.0，官方支持 .NET 8+ | 实际包还原与最小代理测试 |
| Node / npm | 本地 v24.18.0 / 11.16.0，可作为开发基线 | 固定 engines / packageManager，生成 lockfile |
| Vue / Element Plus | Vue 3 + Element Plus；正式工程不沿用静态样例 package.json | 核验相互兼容版本后锁定，不虚构未查询到的包版本 |
| Vite | 主应用正常 SPA 构建；子应用需验证 UMD 输出路线 | 固定 Vite / plugin-vue 组合并做浏览器验证 |
| qiankun | 采用稳定线 2.10.16；不默认采用 3.x RC | 核验以下双模式 PoC 后再批量建立业务应用 |
| Python | 开发目标 3.13；已验证本地 3.13.13；3.14 暂不作为首期基线 | 使用 uv 建隔离环境，固定 FastAPI 等依赖并运行测试 |
| 本地依赖 | 规划容器编排；目前未发现 Docker 命令或常见安装路径 | 生成配置后需在可用 Docker 环境中验证；不能声明服务已启动 |
| 基础设施版本 | PostgreSQL / pgvector、Redis、MinIO、RabbitMQ、Keycloak 仍待镜像级核验 | T2 固定 tag / digest 与兼容矩阵，禁止 latest 漂移 |

Python 解释器实测：`E:\Users\Mryeh\miniconda3\python.exe` 为 3.13.13；`C:\Users\Mryeh\AppData\Local\Python\bin\python.exe` 为 3.14.3。默认 `python` 命令指向不可用 WindowsApps 别名；后续通过 uv / 隔离环境显式选择。uv 版本为 0.10.2；缓存须放在允许写入的工作区或临时目录。

qiankun 官方 npm dist-tags 本轮返回 latest=2.10.16、rc=3.0.0-rc.22。v3 官网的原生 ESM / Vite 能力不能推定适用于 v2。本项目采用 **Vite library mode 输出 UMD + HTML 入口 + 显式生命周期导出** 作为待验证稳定路线；不把“编译成功”当成挂载兼容证据。

T2 必须完成以下 PoC：

- 子应用构建产物可独立启动，也可由 qiankun 主应用加载；导出 bootstrap / mount / unmount。
- 同一发布物的 JS / CSS / 图片在两种模式下路径正确；入口与运行路由分离，深链刷新可恢复。
- 卸载再挂载没有重复监听、残留样式污染或请求；生命周期失败有可观察错误。
- UMD 所需单 bundle / 内联动态导入限制被记录；嵌入模式优先验证生产构建，不能假设 Vite dev HMR 可用。
- 若 PoC 失败，先定位构建 / 运行问题；变更构建路线需记录决定，不静默改用 qiankun 3 RC 或换掉已确认的 Vite。

官方依据：[.NET 支持政策](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)、[YARP 入门](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/yarp/getting-started?view=aspnetcore-10.0)、[YARP 2.3.0](https://www.nuget.org/packages/Yarp.ReverseProxy/2.3.0)、[Vue 起步](https://vuejs.org/guide/quick-start.html)、[Vite 构建](https://vite.dev/guide/build#library-mode)、[Element Plus 安装](https://element-plus.org/en-US/guide/installation.html)、[qiankun 2 接入](https://qiankun.umijs.org/guide/getting-started/)、[qiankun 发布记录](https://github.com/umijs/qiankun/releases)、[qiankun 3 Vite 指南](https://www.qiankunjs.com/cookbook/prepare-a-vite-app)。UMD 组合路线是基于这些文档的工程推论，尚未经本项目运行验证。

## 5. 补充建议采纳记录

“采用”指纳入后续实现约束，不表示功能已实现或测试通过。

| 建议 | 决定 | 后续任务 |
| --- | --- | --- |
| V14 身份与上下文校验 | 采用；BFF + OIDC、内部调用签名上下文 | T3 |
| V15 应用状态与授权一致 | 采用；全局发布、租户启用、用户授权均需成立 | T4 / T5 |
| V16 版本组合与回滚一致 | 采用；不可变发布记录 + 租户活动指针 + 固定版本运行 | T4 / T5 / T8 |
| V17 入口与代理目标约束 | 采用；受控发布物来源与 ServiceBinding | T4 / T5 |
| V18 流取消与错误 | 采用目标；协议、超时与取消上限仍由 D8 固化 | T5 |
| V19 生命周期与深链 | 采用；T2 PoC，T7 完整接入验收 | T2 / T7 |
| V20 密钥边界 | 采用；浏览器不持模型 / 数据连接密钥，内部凭据不入源码 | T3 / T9 / T10 |
| V21 ChatBI 只读与查询约束 | 待 D5 数据源与用例决策；保持建议，不据此承诺已有实现 | T10 / T11 |
| V22 来源引用 | 待 D6 文档与问答契约决策 | T12 |
| V23 删除 / 撤权 | 采用隔离目标；生效时限与清理策略待 D6 / D8 | T3 / T12 |
| V24 消息幂等与失败 | 采用原则；任务键、重试上限、确认时点待摄取设计 | T12 |
| V25 缓存与撤权 | 采用；策略版本与失效上限在 T3 固化 | T3 |
| V26 本地依赖与无隐式外网 | 待 D4 / D7 确定部署验收条件；开发 UI 先不引入外网字体 | T9 / T14 |

## 6. T1 核验与当前限制

已核验：参考提交与 29 个文件快照逐项 SHA-256 一致；UI 复用证据；本地 Node / npm、.NET SDK、Python / uv；12 处本地文档链接有效；15 项任务的接口 / 验收引用和依赖有效；26 条验收条目、原 Manifest JSON 均保留。D1–D3 设计记录及实施 / 验证状态经独立只读复核无实质矛盾。核验命令退出码为 0。

尚未执行：npm install、dotnet restore / build、FastAPI 启动、qiankun 浏览器 PoC、Keycloak 登录、数据库和容器服务启动。T1 设计完成不代表 V1–V26 的运行行为已通过。

环境限制：

- 当前工作区 `.git` 初始化成功，但写 `.git/config` 被操作系统拒绝；尚未配置 origin、导入远端 Git 历史或创建提交。参考快照从临时克隆导入，文件哈希可追溯；后续恢复 Git 写权限后再关联远端并提交。
- 部分 npm 元数据查询返回 EACCES；未推测尚未核验的依赖版本。完整依赖安装与锁定需在 T2 的可用网络 / 缓存条件下验证。
- 未发现可用 Docker；Python 已通过实际解释器路径确认可用。容器验证有独立环境前置条件，不阻塞文档与纯代码骨架准备。

T2 交付目标：可复现目录 / 构建配置、依赖锁定、服务健康入口、基础设施配置与迁移入口、qiankun 双模式 PoC。T3–T7 再完成“登录 → 注册 → 门户 → 子应用 → YARP 流 → 审计”的真实链路。
