# 原生 PostgreSQL 验收

本入口在 Windows x64 上启动一次性 PostgreSQL，验证真实控制面迁移、授权查询和加密 BFF 会话存储，不需要 Docker。数据库仅用于本次测试；已有开发数据库不会被复用或停止。

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
```

运行器仅使用 Python 标准库，可换成已有 Python 3.13 解释器；它不下载依赖。另需 .NET 10 SDK 和已构建的 `tests/platform/bin/Debug/net10.0/AssembleAI.Platform.SmokeTests.dll`。普通 `tools/verify.ps1 -Scope Core` / `All` 不启动数据库，本入口需单独运行。

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
| `deploy/postgres/migrations/apply.sql` | 执行真实控制面迁移 1–3：迁移记录、授权关系、BFF 会话；不安装 vector |
| `deploy/postgres/migrations/enable-vector.sql` | 独立安装并核验 pgvector **0.8.7**；缺少扩展文件或已有版本不符都会失败 |
| `deploy/postgres/migrations/apply-with-vector.sql` | 先执行控制面迁移，再执行 vector 初始化 |
| `deploy/postgres/migrate.ps1` | 默认执行控制面；加 `-IncludeVector` 执行两者；`-UseCompose` 仅切换执行环境 |

控制面迁移与 vector 初始化都限定 `assembleai_platform` 数据库，并使用事务及 advisory lock。控制面迁移不预置用户、租户或会话；vector 初始化不伪造控制面迁移记录，也不会自动升级已有的其他版本。组合入口按顺序执行各事务，vector 失败不会回滚已经提交的控制面迁移，可在补齐扩展后重试。Compose 空数据卷初始化使用 `apply-with-vector.sql`，继续要求完整扩展。

操作已有数据库时，需先准备数据库和角色，并为迁移账号配置对应权限；这些准备由本验收器自动完成的范围仅限一次性测试集群。手动迁移示例见 [本地后端与基础设施](development-services.md#原生-postgresql-迁移)。

`Readiness:RequireVector` 默认 **true**（环境变量 `Readiness__RequireVector`），保持完整平台对 vector 0.8.7 的要求。只有明确运行控制面时才配置 `false`；该选项不代表知识库功能已就绪，也不忽略 Redis、MinIO、RabbitMQ 或 OIDC 的健康检查。BFF 开启时还要求迁移 3，否则要求迁移 2。

## 验证内容与边界

- 真实迁移 1–3 连续执行两遍，检查迁移记录完整且无重复、没有预置业务数据；误连其他数据库必须失败。
- 独立 vector 初始化在缺少扩展文件时返回 PostgreSQL `0A000`，不会将缺失扩展报告为成功。
- 授权仓储覆盖 issuer / subject、租户与工厂组织隔离、租户复合外键、事务内版本一致性、撤权生效、取消，以及连接真实仓储的 HTTP 授权边界。
- 会话仓储覆盖数据库中的真实密文、独立重建的密钥提供器与重新加载 PFX 后恢复、密文篡改拒绝、UTC 到期、删除与续期并发、取消和数据库连接失败。
- 联合链路使用 OIDC HTTP 协议服务、真实 PG 会话 / 授权、服务端租户选择及仅公钥 HTTP 接收端，验证已签发凭据的策略撤权和退出重放。
- readiness 的 PostgreSQL 子检查在 `RequireVector=false` 时为 `ready`、为 true 时为 `unavailable`；由于其他依赖未配置，整体 HTTP 状态在两种情况下都保持 503。

基础授权 HTTP 测试使用测试身份认证器，会话测试直接调用真实仓储；`PostgresTrustTests` 进一步通过真实 OIDC 协议交换串联 PG 会话、租户选择和签名 HTTP 下游。它使用 `OidcFixture`，尚未串联真实 Keycloak、生产 HTTPS Cookie 和 PostgreSQL 完整部署，也不证明跨机器故障恢复、pgvector 安装或知识库查询已通过。[原生 Keycloak 浏览器验收](native-identity.md) 独立覆盖兼容性，平台仍使用开发内存会话。

## 2026-11-03 验收记录

安装和重复运行均返回 0，`postgres --version` / `psql --version` 均输出 PostgreSQL 17.11。

原迁移的实库红测记录在 `.artifacts/postgres/run-15d4adb453d543819d728b362fade72d`：迁移 1 在 `CREATE EXTENSION vector` 处失败，明确报告缺少 `share/extension/vector.control`。随后将控制面迁移与知识库扩展初始化显式拆分，没有跳过控制面 SQL 或补写迁移版本。

拆分后的完整验收位于 `.artifacts/postgres/run-0bc82b0de23c456d8d6bc91bb9f0deaf`，入口返回 0，`result.json` 中 `passed=true`、`cleanupComplete=true`、`postgresVersion=17.11`。上述控制面、授权、会话及 readiness 边界全部通过；本次集群及临时凭据已清理。pgvector 正向安装与完整基础设施验收仍待后续环境完成。

加入信任链后的完整验收为 `.artifacts/postgres/run-4423450349cc4c1a9ba6da9851c4dbf8`，同样返回 0，passed / cleanupComplete 均 true。撤权测试固定 JWT 时钟，确认旧凭据拒绝源于策略变化，而非恰好自然过期。
