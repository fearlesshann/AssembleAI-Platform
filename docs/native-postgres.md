# 原生 PostgreSQL 验收

本入口在 Windows x64 上启动一次性 PostgreSQL，验证真实控制面迁移、授权查询、加密 BFF 会话、注册发布事务及正式网关链路，不需要 Docker。数据库仅用于本次测试；已有开发数据库不会被复用或停止。

T13模型与数据源管理可用`--resource-browser`加入真实页面验收；`--resource-only --resource-browser`只运行本批合同与readiness，不代表完整平台回归。前者可随完整核心实库测试运行，后者不得与其他专项模式组合。使用真实OIDC协议、BFF、PostgreSQL、本地模型协议服务和独立只读数据库角色，覆盖配置、冲突、锁等待中的撤权 / 切租户 / 退出、调用和审计、权限及主题 / 窄屏。前端需提前构建；不调用真实模型供应商。合同见[资源管理](architecture/resource-management.md)。

## 准备与运行

从仓库根目录执行：

```powershell
# 一次性准备：下载官方 ZIP，校验并解压至项目 .local-data
powershell -NoProfile -File tools/install-native-postgres.ps1

# 使用项目锁定的 .NET 依赖构建 Debug 验收程序集
dotnet restore AssembleAI.slnx --locked-mode
dotnet build AssembleAI.slnx --no-restore --configuration Debug

# 创建独立数据库、执行实库验收、停止集群并删除临时凭据
& '.\apps\sample\backend\.venv\Scripts\python.exe' tools/verify-postgres.py

# 另需门户构建产物、已安装的锁定前端依赖与 Chromium：真实浏览器登录 / 目录链
& '.\apps\sample\backend\.venv\Scripts\python.exe' tools/verify-postgres.py --portal-browser

# 组合门户、真实子应用与三账号注册管理浏览器验收
& '.\apps\sample\backend\.venv\Scripts\python.exe' tools/verify-postgres.py --portal-browser --application-browser --registry-browser

# 身份管理专项：迁移、正式管理员初始化、权限 / 容量 / 事务及真实浏览器
& '.\apps\sample\backend\.venv\Scripts\python.exe' tools/verify-postgres.py --identity-only --identity-browser
# 完整控制面回归同时验身份管理浏览器（最终集成验收）
& '.\apps\sample\backend\.venv\Scripts\python.exe' tools/verify-postgres.py --identity-browser

# OIDC / PG / YARP / Python 的真实模型委托链（本地协议供应商）
& '.\apps\sample\backend\.venv\Scripts\python.exe' tools/verify-postgres.py --model-application
# 另设 ASSEMBLEAI_MODEL_ACCEPTANCE_CONFIG 及其引用的凭据环境变量后可加 --real-model
```

运行器仅使用 Python 标准库，可换成已有 Python 3.13 解释器；它不下载依赖。另需 .NET 10 SDK、Node（用于正式管理员初始化入口）和已构建的 `tests/platform/bin/Debug/net10.0/AssembleAI.Platform.SmokeTests.dll`。普通 `tools/verify.ps1 -Scope Core` / `All` 不启动数据库，本入口需单独运行。`--identity-only` 明确只验迁移、身份模块及 readiness，不将专项通过称为完整控制面回归。

## 锁定安装

`tools/native-postgres.lock.json` 固定 EDB PostgreSQL **17.11，Windows x64 build 5**。ZIP 为 `postgresql-17.11-5-windows-x64-binaries.zip`，大小 381,531,864 字节（约 364 MiB）；解压还需额外空间。[PostgreSQL Windows 官方页面](https://www.postgresql.org/download/windows/) 指向 [EDB 二进制下载页](https://www.enterprisedb.com/download-postgresql-binaries)。锁文件同时记录下载入口及其重定向后的固定 HTTPS 包地址。

安装脚本校验长度和 SHA256：`80379b2c04d51c30225532e0ae04509899141e9957ed096fe749d7fd9df8f82f`。该值来自 [EDB 发布的 SHA256 文件](https://get.enterprisedb.com/postgresql/postgresql-17.11-5-windows-x64-binaries.zip.sha256)，已与实际下载包比对；本机检查的 `postgres.exe` / `psql.exe` 没有 Authenticode 签名，因此验证依据是官方 HTTPS 下载及发布方校验和。

安装位置为 `.local-data/native-postgres/postgresql/pgsql`，`installed.json` 记录位置和版本。脚本检查解压路径、拒绝覆盖不认识的目录，重复运行先核验缓存归属和归档哈希，再执行两个程序的 `--version`。安装过程不初始化数据库、不设置账号密码、不注册服务，也不修改系统 PATH。首次解压约 22,000 个条目可能持续数分钟。

运行器按锁文件中的 `relativeHome` 查找安装；`--dependencies-root` 可指定另一依赖根目录，但仍要求相同目录结构和版本。默认安装包没有 pgvector，本次实库验收专门覆盖这一边界。

## 隔离与清理

每次运行创建 `.local-data/postgres-tests/run-*`，执行真实 `initdb`，生成独立管理员和应用密码，仅监听 `127.0.0.1` 的临时端口，认证使用 SCRAM-SHA-256。应用角色 `assembleai_platform` 没有 SUPERUSER、CREATEDB、CREATEROLE 或 BYPASSRLS 权限；迁移和测试数据只写入本次新建的同名数据库。

运行器清除继承的 PostgreSQL、Npgsql、认证和连接串配置。密码通过本次临时文件、标准输入或子进程环境传入，不放在命令行。实库测试先校验目录、随机运行标记和数据库内标记，确认连接的是本次实例后才写入测试数据。测试使用的 RSA PFX、Data Protection keyring 和连接配置也保存在本次临时目录。

结束时先用 `pg_ctl` 停止本次集群，确认停止后再核对绝对路径并删除本次目录。停止或删除失败会返回非零，并保留尚未安全清理的数据。安装包缓存保留供下次使用。日志与 `result.json` 位于 `.artifacts/postgres/run-*`，其中 `passed` 只有在验收和清理都完成后才为 true；这些目录均被 Git 忽略。

## 控制面与 vector 初始化

| 入口 | 行为 |
| --- | --- |
| `deploy/postgres/migrations/apply.sql` | 执行真实控制面迁移 1–12：迁移记录、授权关系、BFF 会话、注册 / 租约、模型及连接器、知识文档 / 索引元数据、身份管理员与审计；不安装 vector |
| `deploy/postgres/migrations/enable-vector.sql` | 独立安装并核验 pgvector **0.8.7**；缺少扩展文件或已有版本不符都会失败 |
| `deploy/postgres/migrations/apply-with-vector.sql` | 先执行控制面迁移，再执行 vector 初始化 |
| `deploy/postgres/migrate.ps1` | 默认执行控制面；加 `-IncludeVector` 执行两者；`-UseCompose` 仅切换执行环境 |

控制面迁移与 vector 初始化都限定 `assembleai_platform` 数据库，并使用事务及 advisory lock。控制面迁移不预置用户、租户或会话；vector 初始化不伪造控制面迁移记录，也不会自动升级已有的其他版本。组合入口按顺序执行各事务，vector 失败不会回滚已经提交的控制面迁移，可在补齐扩展后重试。Compose 空数据卷初始化使用 `apply-with-vector.sql`，继续要求完整扩展。

操作已有数据库时，需先准备数据库和角色，并为迁移账号配置对应权限；这些准备由本验收器自动完成的范围仅限一次性测试集群。手动迁移示例见 [本地后端与基础设施](development-services.md#原生-postgresql-迁移)。

`Readiness:RequireVector` 默认 **true**（环境变量 `Readiness__RequireVector`），保持完整平台对 vector 0.8.7 的要求。只有明确运行控制面时才配置 `false`；该选项不代表知识库功能已就绪，也不忽略 Redis、MinIO、RabbitMQ 或 OIDC 的健康检查。无论 BFF 或 Models 是否开启，当前控制面均要求迁移 1–12 完整；缺少11或12均有独立拒绝回归。

## 验证内容与边界

- 真实迁移 1–12 连续执行两遍，检查迁移记录完整且无重复、没有预置业务数据；误连其他数据库必须失败。
- 身份管理验证正式Node / psql中文与引号初始化、重放和禁用 / 绑定保护、显式全局 / 租户管理隔离、严格JSON / CSRF、修订冲突、审计故障回滚、锁等待期间退出 / 切租户拒写、数量与完整响应预算；`--identity-browser`以真实页面操作验证用户 / 租户 / 成员 / 组织 / 角色和双主题 / 390px。详见[身份管理合同](architecture/identity-management.md)。
- `--chatbi-browser` 验证真实 ChatBI 浏览器 / Python / PostgreSQL 链与本地模型协议夹具；`--chatbi-real-model` 再运行三道合成题的真实模型验收。参数、权限与题集见 [ChatBI 契约](architecture/chatbi.md)。
- T10 始终验证连接配置 / 审计、真实 OIDC/BFF 数据查询和独立只读源角色；制造业测试表覆盖跨工厂 / 租户、聚合、权限交集、撤权 / 取消 / 超时、类型与结果大小限制。运行器创建专用非特权 reader，只授权测试表 SELECT；测试完成清理集群和凭据。基础验收整体窗口 240 秒，业务查询截止保持各自配置。
- 独立 vector 初始化在缺少扩展文件时返回 PostgreSQL `0A000`，不会将缺失扩展报告为成功。
- 授权仓储覆盖 issuer / subject、租户与工厂组织隔离、租户复合外键、事务内版本一致性、撤权生效、取消，以及连接真实仓储的 HTTP 授权边界。
- 会话仓储覆盖数据库中的真实密文、独立重建的密钥提供器与重新加载 PFX 后恢复、密文篡改拒绝、UTC 到期、删除与续期并发、取消和数据库连接失败。
- 联合链路使用 OIDC HTTP 协议服务、真实 PG 会话 / 授权、服务端租户选择及仅公钥 HTTP 接收端，验证已签发凭据的策略撤权和退出重放。
- 注册测试使用 OIDC 协议登录和实际 HTTP API，覆盖所有者 / 独立审核、Manifest 负例、租户授权与撤权、状态转换、并发 CAS、版本回滚、发布后不可变及审计失败回滚。
- 管理读取测试覆盖无租户全局操作者、租户单独管理员、普通 / 缺失用户、草稿与审计范围、角色 / 成员撤销，以及应用 500 / 501、版本 100 / 101、审计 50 + hasMore 边界。
- 模型仓储覆盖修订号、原子配置审计、调用准入 / 完成、未知 / 精确用量和租户范围；迁移 7 增加完整应用 / 运行 / 网关请求来源约束。`--model-application` 进一步串联真实 uvicorn 与平台模型入口，覆盖增量与 31 秒活动委托、断连 / 撤权 / 停用、伪造与重放拒绝。`--real-model` 仅用显式供应商做 JSON / SSE 两次实际调用，配置与结果边界见 [模型网关](architecture/model-gateway.md)。这两个选项需要锁定 Python 环境，单独运行时不要求浏览器；Node用于身份初始化验收。
- `--registry-browser` 增加三个独立 OIDC 浏览器账号，真实注册 / 修改 / 提交 / 审核、两版本启用 / 切换 / 回滚 / 停用，并验证固定运行、在途流排空与停止、SQL 审计主体及关联编号；检查浅暗 / 390px。需要 Node、已构建门户和 Chromium；使用真实签名 HTTP 下游，Python 消费端由 application-browser 覆盖。可同时启用三个浏览器参数。
- 目录测试验证服务端选中租户、全部入口授权、发布启用与可信部署过滤、隐藏分类隔离、5 字段 DTO、实时撤权 / 版本切换、500 项完整成功与 501 项明确 503；目录准入与网关对照。
- `--portal-browser` 在测试 host 提供真实构建静态页面，通过 Chromium 实际 OIDC 登录、选择租户、读取 PG 清单 / 详情、主题 / 390px 检查与退出，不拦截 API；不指定时明确跳过该浏览器项。原生启动器会解析 Node 绝对路径后再隔离 PATH，仍不下载依赖。
- `--application-browser` 在本次临时目录从同一模板构建 sample / sample-clone 两个独立发布物，计算真实摘要并经注册 / 独立审核 / 租户启用；启动正式 Node 预览、BFF / YARP 和真实 uvicorn，经平台内部验证查询当前 PG 授权。验证 JSON / SSE 首段 / 取消、消费端撤权 / 启停、独立深链、同文档重挂与刷新不自动创建运行；不拦截 API。可与 portal-browser 同时使用；需已构建门户、锁定 Python 环境及浏览器依赖，可用 ASSEMBLEAI_NODE / ASSEMBLEAI_PYTHON 显式指定可执行文件。
- 网关仓储验证会话哈希绑定、全部入口权限、租约到期 / 排空、版本来回切换不延长、停用后不复活、并发容量及调用审计故障。
- `PostgresGatewayFlowTests` 不替换生产网关或 PG 仓储，串联 OIDC 协议服务、开发内存 BFF 会话、真实注册 / 授权 / 租约 / 审计数据库及仅公钥 HTTP 下游；验证签名 JSON、SSE 首段与完成、持久化结果，以及数据库撤权或注册停用取消在途流。
- readiness 的 PostgreSQL 子检查在 `RequireVector=false` 时为 `ready`、为 true 时为 `unavailable`；由于其他依赖未配置，整体 HTTP 状态在两种情况下都保持 503。

基础授权 HTTP 测试使用测试身份认证器，会话测试直接调用真实仓储；`PostgresTrustTests` 进一步通过真实 OIDC 协议交换串联 PG 会话、租户选择和签名 HTTP 下游。它使用 `OidcFixture`，尚未串联真实 Keycloak、生产 HTTPS Cookie 和 PostgreSQL 完整部署，也不证明跨机器故障恢复、pgvector 安装或知识库查询已通过。[原生 Keycloak 浏览器验收](native-identity.md) 独立覆盖兼容性，平台仍使用开发内存会话。

## 2026-11-03 验收记录

T8 最终组合运行 `--portal-browser --application-browser --registry-browser` 返回 0；`.artifacts/postgres/run-33442f6f67e842ed959eadd401e972e2/result.json` 的 passed / cleanupComplete 与三个 browserRequested 均 true。包含本页全部管理查询与三账号流程、已有门户和真实 Python 应用链路；临时集群与凭据已清理。详细结果见 [T8 开发记录](development.md#t8-应用管理验收--2026-11-03)。

安装和重复运行均返回 0，`postgres --version` / `psql --version` 均输出 PostgreSQL 17.11。

原迁移的实库红测记录在 `.artifacts/postgres/run-15d4adb453d543819d728b362fade72d`：迁移 1 在 `CREATE EXTENSION vector` 处失败，明确报告缺少 `share/extension/vector.control`。随后将控制面迁移与知识库扩展初始化显式拆分，没有跳过控制面 SQL 或补写迁移版本。

拆分后的完整验收位于 `.artifacts/postgres/run-0bc82b0de23c456d8d6bc91bb9f0deaf`，入口返回 0，`result.json` 中 `passed=true`、`cleanupComplete=true`、`postgresVersion=17.11`。上述控制面、授权、会话及 readiness 边界全部通过；本次集群及临时凭据已清理。pgvector 正向安装与完整基础设施验收仍待后续环境完成。

加入信任链后的完整验收为 `.artifacts/postgres/run-4423450349cc4c1a9ba6da9851c4dbf8`，同样返回 0，passed / cleanupComplete 均 true。撤权测试固定 JWT 时钟，确认旧凭据拒绝源于策略变化，而非恰好自然过期。

## T4 注册控制面验收 · 2026-11-03

[注册与部署契约](architecture/registry.md) 和 [Manifest schema](../contracts/manifests/README.md) 已提供。最终原生 PostgreSQL 运行 `.artifacts/postgres/run-fcdfc3e3870247f8b438919660eb20b1/result.json` 返回 0，passed / cleanupComplete 均 true。迁移 1–4 及重放、严格 HTTP 命令、独立审核 / 自审拒绝、发布后不可变、租户选择 / 授权撤销、并发 CAS、回滚和审计故障回滚全部通过。T4 测试使用 OIDC 协议 fixture、开发内存会话与真实注册数据库，生产部署仍归 T14。

Core 回归通过：锁定还原、.NET 构建 0 警告 / 0 错误、原有身份及内部凭据测试、新 Manifest / 匿名读写测试、Node 3 项、Python 9 项与真实 FastAPI → YARP。已有 FastAPI TestClient 的 httpx 弃用提示不影响通过。新增 bug 及回归见 SPEC B23–B25；数据库空 JSON 负测先失败，修正 CHECK 的 NULL 处理后通过。独立助手额度限制由主线程接手复核，未声称取得新的独立复核结论。

T4 为注册控制面 API 结项。资源实际部署 / 健康探测、正式动态路由 / 运行授权、门户清单与管理界面分别按 T5 / T6 / T8 继续；测试未启动 Docker。

## T5 正式网关验收 · 2026-11-03

最终运行 `.artifacts/postgres/run-dd8b27ca47c845ca9ce24e6d7702c8c8/result.json` 返回 0，`passed=true`、`cleanupComplete=true`、`postgresVersion=17.11`。迁移 1–5 执行及重放、既有授权 / 密文会话 / 注册回归、网关租约与调用仓储、生产网关和真实 PG / HTTP 联合链路全部通过；本次临时数据库、凭据和进程清理完成。

该组合直接使用生产 Gateway、PostgresGatewayStore、授权与注册仓储，验证 JSON / SSE 首段与完成、签名下游和持久化调用结果。实际撤销数据库授权、通过注册仓储停用应用后，在途流均被取消；新请求拒绝。租约切换 / 回滚的非延长与停用不复活、容量竞争及审计故障由实库仓储测试覆盖。

身份服务仍为 OidcFixture，本组合使用开发内存会话；独立 PG 密文会话链路继续由 T3 测试覆盖。没有据此声称真实 Keycloak + PG + 生产 HTTPS 整体部署或向量查询完成。完整 Core 与最后期限回归的执行记录见[开发记录](development.md#t5-正式网关验收--2026-11-03)。

## T6 授权目录与浏览器验收 · 2026-11-03

最终执行 `tools/verify-postgres.py --portal-browser` 返回 0，记录 `.artifacts/postgres/run-6e31c712c5fc469f89c3251ab8fe7dd3/result.json` 的 `passed=true`、`cleanupComplete=true`、`portalBrowserRequested=true`。迁移 1–5、既有 T3–T5 实库合同、新目录权限 / 过滤 / 版本 / 容量测试全部通过。

浏览器实际访问测试 host 上的门户构建，完成 OIDC 登录、选择租户、读取授权清单和详情、浅暗主题 / 390px 检查及退出后 401；没有 API mock。此浏览器链使用开发内存会话，单独的 PG 密文会话测试同时继续执行。截图与完整代码回归结果见[开发记录](development.md#t6-授权门户验收--2026-11-03)。运行器清理了本次集群与凭据，截图和脱敏日志留在被 Git 忽略的 `.artifacts`。
