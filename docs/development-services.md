# 本地后端与基础设施

本页对应 T2。样例后端用于验证接入和流式传输，没有模型调用或业务数据；正式认证与租户授权在 T3 接入。

当前开发采用本机 .NET / FastAPI 进程，不要求 Docker。可用 `tools/dev-backend.py` 统一启动；后续 PostgreSQL、Redis、RabbitMQ、MinIO、Keycloak 可分别使用本机安装或已有服务，通过下方配置键接入。本文 Compose 部分仅是可选启动方案。

## 样例 FastAPI

工作目录：`apps/sample/backend`。目标 Python 3.13；入口 `sample_backend.main:app`。已在本机 Python 3.13.13、FastAPI 0.140.0、Uvicorn 0.51.0 上通过测试。

安装已入库的锁定依赖：

```powershell
cd apps/sample/backend
uv sync --locked --python 3.13
$env:ASSEMBLEAI_ENVIRONMENT = 'Development'
uv run --locked uvicorn sample_backend.main:app --host 127.0.0.1 --port 8101
```

2026-11-03 已生成真实 `uv.lock`：23 条解析记录（含项目自身），22 个第三方包及分发哈希；支持 Python `>=3.13,<3.14`。已在 Windows / CPython 3.13.13 的独立 `.venv` 中执行 `uv sync --locked`、`uv lock --check` 与 9 项 pytest，未启用系统 site-packages。Linux 安装尚未实测。依赖升级时才运行 `uv lock`，日常以 `uv sync --locked` 复现。

`requirements.windows.lock` 保留为早期预装环境的历史快照；新的项目环境以 `uv.lock` 为准。当前 Starlette 测试客户端对 httpx 0.28.1 提示弃用警告，测试仍通过，后续依赖升级再一并处理。

本机预装环境的复核命令（不安装或修改该环境）：

```powershell
cd apps/sample/backend
& 'E:\Users\Mryeh\miniconda3\python.exe' -m pytest -q
$env:ASSEMBLEAI_ENVIRONMENT = 'Development'
& 'E:\Users\Mryeh\miniconda3\python.exe' -m uvicorn sample_backend.main:app --host 127.0.0.1 --port 8101
```

| 路径 | 行为 |
| --- | --- |
| `/health/live` | 进程存活；不代表平台依赖已就绪 |
| `/health/ready` | 样例 lifespan 已启动；明确声明该样例无外部依赖 |
| `/stream?count=5&delay_ms=150` | 仅 `ASSEMBLEAI_ENVIRONMENT=Development` 时注册；其他环境为 404 |
| `/docs`、`/openapi.json` | 仅 Development 可用 |

SSE 协议：`delta` 数据 `{index,text,requestId}`；成功以 `complete` 数据 `{count,requestId}` 结束；失败以 `error` 数据 `{code,requestId}` 结束。`count` 限 1–20，`delay_ms` 限 10–1000；首段立即发送，随后按间隔发送。客户端断开或任务取消会释放流状态，不吞掉取消异常；服务端错误不返回内部异常信息。

可通过 .NET 开发网关访问 `/dev/sample/health`、`/dev/sample/stream`；平台开发端口为 5100。该代理与本样例均不构成正式认证路径，不用于生产业务。

## 原生 PostgreSQL 迁移

Windows 原生安装及一次性实库测试见 [原生 PostgreSQL 验收](native-postgres.md)。已锁定并安装 PostgreSQL 17.11，安装脚本只准备二进制；验收器每次独立创建、测试和清理集群。

对已有数据库执行控制面迁移，默认不使用 Docker：

```powershell
powershell -NoProfile -File deploy/postgres/migrate.ps1 -Psql '.\.local-data\native-postgres\postgresql\pgsql\bin\psql.exe' -DatabaseHost 127.0.0.1 -Port 5432 -Username postgres
```

将示例路径与地址换成实际环境。迁移前需已有 `assembleai_platform` 数据库及同名角色，迁移账号需有 `SET ROLE assembleai_platform` 和创建对应对象的权限。密码使用 PostgreSQL 密码文件或进程环境 `PGPASSWORD`，不放入命令行。脚本限定目标数据库，SQL 会再次检查数据库名，使用事务及 advisory lock。

默认执行 `apply.sql` 的控制面迁移 1–7，不需要 pgvector。需要知识库扩展时加 `-IncludeVector`，执行 `apply-with-vector.sql`：控制面迁移完成后，再由 `enable-vector.sql` 安装并严格核验 pgvector 0.8.7。此时服务器必须已安装扩展文件，迁移账号也需具备安装扩展的权限；缺失或版本不符都会失败。该组合按各迁移事务顺序提交，vector 失败时已提交的控制面迁移仍保留。

平台 `Readiness:RequireVector` 默认 true；仅控制面环境需明确配置 `Readiness__RequireVector=false`。这不会声明知识库已就绪，也不会绕过其他基础设施检查。开发流式样例仍不依赖数据库。

## Compose 开发依赖（可选）

需要可用的 Docker Engine 与 Compose v2。本轮没有发现 Docker，以下流程尚未实际启动或拉取镜像。

从仓库根目录：

```powershell
powershell -NoProfile -File deploy/compose/initialize-env.ps1
docker compose --env-file deploy/compose/.env -f deploy/compose/compose.yaml config --quiet
docker compose --env-file deploy/compose/.env -f deploy/compose/compose.yaml up -d --build --wait
powershell -NoProfile -File deploy/postgres/migrate.ps1 -UseCompose -IncludeVector
docker compose --env-file deploy/compose/.env -f deploy/compose/compose.yaml ps
```

初始化脚本为每个开发凭据生成独立的 32 字节随机值，仅写入忽略的 `.env`，不输出密钥；文件已存在时拒绝覆盖。`.env.example` 的空值会使 Compose 失败，避免意外沿用共享默认密码。不要把 `compose config` 的完整输出保存进版本库，因为它包含环境插值后的凭据。

所有宿主机映射只绑定 `127.0.0.1`；此文件只适用于本地开发，不包含生产 TLS、备份、HA 或密钥管理。

| 服务 | 固定标签 / 来源 | 本机地址 |
| --- | --- | --- |
| PostgreSQL + pgvector | `pgvector/pgvector:0.8.7-pg17-bookworm`；[官方支持标签](https://github.com/pgvector/pgvector#docker) | `127.0.0.1:5432` |
| Redis | `redis:8.2.11-bookworm`；[官方镜像清单](https://raw.githubusercontent.com/docker-library/official-images/master/library/redis) | `127.0.0.1:6379` |
| RabbitMQ | `rabbitmq:4.4.0-management`；[官方镜像清单](https://raw.githubusercontent.com/docker-library/official-images/master/library/rabbitmq) | AMQP `5672`；管理 `15672` |
| Keycloak | `quay.io/keycloak/keycloak:26.8.1`；[官方下载](https://www.keycloak.org/downloads) | `http://localhost:8080` |
| MinIO | 本地构建 `assembleai/minio:RELEASE.2025-10-15T17-29-55Z-local`；[官方安全发布](https://github.com/minio/minio/releases/tag/RELEASE.2025-10-15T17-29-55Z) | API `9000`；控制台 `9001` |

上述标签有官方来源；尚未拉取验证、锁定镜像 digest 或验证整套兼容性。pgvector 标签固定扩展版本与 PostgreSQL 主版本，实际 PostgreSQL 补丁版本需拉取后记录。

MinIO 官方仓库于 2026-04-25 归档，最后的安全发布说明要求从源码构建容器。本项目保留已定 MinIO 选型，提供该安全 tag 的多阶段构建，未引用不存在的新官方二进制标签。构建使用 [官方列出的](https://raw.githubusercontent.com/docker-library/official-images/master/library/golang) `golang:1.26.10-bookworm`；成品使用无 shell 的 scratch 镜像、非 root 用户与独立健康检查程序。**该构建尚未在 Docker 中验证**；生产维护与后续替代评估留待 D7，不把归档项目当成仍有上游维护承诺。

## 数据库与迁移

- 首次初始化创建 `assembleai_platform` 与 `keycloak` 两个数据库和独立账号；撤销 PUBLIC 的跨库连接权限。
- `assembleai_platform` 账号无 SUPERUSER / CREATEDB / CREATEROLE / BYPASSRLS；当前是本地开发数据库所有者，生产迁移账号与运行账号分离在交付阶段落实。
- Compose 数据库初次创建时运行 `/migrations/apply-with-vector.sql`，初始化控制面及 pgvector；重复执行 `migrate.ps1 -UseCompose -IncludeVector` 重放同一套可幂等入口。原生模式也使用这些 SQL。
- `0001_bootstrap.sql` 在事务和 advisory lock 下验证目标数据库名，创建迁移记录表并写入 `public.platform_schema_migrations` 的 `version=1`。误连其他数据库会失败；该迁移不安装 vector。
- `0002_authorization.sql` 增加用户、租户、组织、成员、角色和授权关系，使用租户复合外键，并记录 `version=2`。迁移不预置账号或权限，已通过真实数据库验收。
- `0003_bff_sessions.sql` 增加服务端加密 ticket、随机会话引用与绝对到期时间，记录 `version=3`；不包含保护密钥或预置会话。
- `enable-vector.sql` 独立安装并核验 pgvector 0.8.7；已有其他版本时拒绝自动升级。`apply.sql` 包含控制面 1–7，`apply-with-vector.sql` 在其后执行此扩展步骤。
- 平台 readiness 统一检查迁移 7；`Readiness:RequireVector` 默认 true，只有显式设为 false 才允许无 vector 的控制面数据库。没有预建业务租户、模型授权或制造业演示数据。
- 迁移入口没有隐含重置数据或 `down -v`；初始化脚本只在空数据卷执行，修改 `.env` 不会自动轮换已有数据库角色密码。

## Keycloak 开发接入

Realm：`assembleai`；confidential client：`assembleai-platform`；Authorization Code + PKCE S256；关闭 implicit、密码直授与 service account 登录。客户端 secret 由环境占位符在导入时解析，realm 文件无默认业务用户。[Keycloak 导入文档](https://www.keycloak.org/server/importExport#_using_environment_variables_within_the_realm_configuration_files)

BFF 服务端接口已实现，配置和会话约束见 [T3 BFF](architecture/t3-bff.md)。[原生 Keycloak 验收入口](native-identity.md) 已实跑通过，自动准备隔离测试 realm 与浏览器流程。正常平台默认关闭认证；配置后可直接访问 `/bff/login?returnUrl=/bff/session`，不依赖首页模板演示登录。

Discovery：`http://localhost:8080/realms/assembleai/.well-known/openid-configuration`。回调固定为 `http://localhost:5100/signin-oidc` 和 `http://127.0.0.1:5100/signin-oidc`；其他域名需明确登记。使用 `localhost:8080` 作为 issuer，避免在 localhost 与 IP 之间随意切换身份中心地址。

首次启动按 `.env` 中的独立 bootstrap 管理员密码进入管理界面；没有在代码中预置租户成员、业务账号或默认密码。`--import-realm` 不覆盖已存在 realm；后续配置变更需显式迁移或管理 API 更新。

## 平台连接配置

开发凭据从 `deploy/compose/.env` 获取，不能直接提交进 appsettings：

| 配置键 | 非敏感部分 / 对应凭据 |
| --- | --- |
| `ConnectionStrings__Platform` | `Host=127.0.0.1;Port=5432;Database=assembleai_platform;Username=assembleai_platform;Password=` 后接 `PLATFORM_DB_PASSWORD` |
| `Readiness__RequireVector` | 默认 `true`；仅控制面环境可显式设为 `false`，不影响其他依赖检查 |
| `Readiness__RedisHost` / `RedisPort` / `RedisPassword` | `127.0.0.1` / `6379` / `REDIS_PASSWORD` |
| `Readiness__MinioEndpoint` | `http://127.0.0.1:9000/minio/health/ready` |
| `Readiness__RabbitMqEndpoint` / `User` / `Password` | `http://127.0.0.1:15672/api/health/checks/ready-to-serve-clients` / `assembleai` / `RABBITMQ_PASSWORD` |
| `Readiness__OidcDiscoveryEndpoint` | `http://localhost:8080/realms/assembleai/.well-known/openid-configuration` |

## 本轮验证记录

- FastAPI pytest：9 项通过，覆盖生产禁用开发接口、就绪生命周期、SSE 格式、输入边界、取消 / 断开清理与错误事件。
- 真实 HTTP 烟测：启动临时 Uvicorn，配置每段间隔 1000ms，首段实测 18.3ms；主动断开后健康端点仍可访问。临时进程已停止。
- 本地依赖快照：22 个已安装版本，传递依赖声明全部满足。
- 已验证：官方 JDK / Keycloak ZIP 锁定安装、真实 Keycloak + Chromium 登录 / 退出 / CSRF / Cookie 重放 / SSO，以及测试进程和凭据清理。平台使用开发内存会话，Keycloak 使用 H2。
- 已验证：PostgreSQL 17.11 原生锁定安装、控制面迁移 1–3 重放及错库拒绝、真实授权仓储 / HTTP 边界、真实加密会话存储 / 独立密钥提供器恢复，以及缺少 vector 的 readiness 边界。验收返回 0，临时集群与凭据已清理，详见 [实库验收记录](native-postgres.md#2026-11-03-验收记录)。
- 已验证：受保护租户切换、原期限与退出竞争、RS256 内部凭据 / 仅公钥消费 / 静态密钥轮换；协议身份服务 + PG 会话 / 授权 + HTTP 下游联合链路及提交后撤权通过。具体边界见 [内部调用契约](architecture/t3-internal-calls.md)。
- 尚未验证：Linux 隔离安装、Docker 镜像构建 / 拉取、Compose 运行、pgvector 正向安装、真实 Keycloak + 生产 HTTPS Cookie + PostgreSQL 的完整登录链路、多进程故障恢复及证书轮换。这些项目不会由本机代码验证替代。
