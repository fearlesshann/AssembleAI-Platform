# T3 · 原生 PostgreSQL 实库验收

> **For agentic workers:** 沿用已授权的原生开发与 T3 实施；独立文件并行，迁移与生产修复由主线程统一处理。不得用伪造迁移记录或内存仓储代替实库验收。

**Goal:** 在独立 PostgreSQL 17 实例上执行真实迁移、授权仓储和加密会话契约，提供可重复运行的验证入口。

**Architecture:** EDB 官方 Windows ZIP 固定版本 / 哈希；每次验证用随机密码创建 loopback 独立数据库集群，完整应用核心迁移 1–3，再以非超级用户运行集成测试。控制面初始化与知识库 vector 0.8.7 初始化显式拆分；Compose 完整栈仍执行两者，不改变知识库选型。

**Tech Stack:** PostgreSQL 17.11、Npgsql 10、现有 ASP.NET Core / DataProtection、Python 3.13 stdlib 与 PowerShell。

**Spec:** I.identity / I.authz / I.deploy；V4 / V5 / V6 / V7 / V14 / V25 / V27。T3 仍包含后续租户切换与内部签名上下文。

## 实施边界

- 新集群只位于本次 `.local-data/postgres-tests/run-*`；SCRAM、随机密码、loopback、独立端口，不使用已有数据库或 Windows 服务。
- 配置通过忽略文件传递，不把密码放进进程参数、错误输出或 Git；启动前清理继承 PG 环境，失败也回收自己的进程和数据。
- 集成测试仅显式 `--postgres-integration` 执行；验证本地配置、loopback 数据库和 runId 标记后才允许写 fixture。
- 核心迁移不安装 vector；独立步骤严格检查 0.8.7。已有 vector 不删除、不降级；完整栈入口包括扩展。默认 readiness 仍要求 vector，可显式声明只验证控制面。
- 先执行原迁移链记录无 vector 时的失败，再实施拆分并执行完整核心链及幂等重放；扩展缺失不能报告知识库就绪。

## Task 1 · 安装与迁移

- [x] `tools/native-postgres.lock.json` / `tools/install-native-postgres.ps1`：固定官方包、真实校验和来源、解压与版本验证。
- [x] `0001_bootstrap.sql` 只保留核心初始化；新增 `enable-vector.sql` / `apply-with-vector.sql`；`migrate.ps1 -IncludeVector` 与 Compose 初始化维持显式完整栈行为。
- [x] readiness 检查所需扩展；SPEC 记录接口边界与兼容性。

## Task 2 · 实库契约

- [x] `PostgresAuthorizationTests.RunAsync(connectionString)`：双租户 / 双工厂、真实查询、复合外键、策略版本、提交撤权和回滚。
- [x] `PostgresSessionTests.RunAsync(connectionString, runDirectory)`：加密 ticket、跨实例恢复、篡改 / key 绑定、绝对期限、撤销与并发续期、取消和安全故障。
- [x] `PostgresIntegrationTests` 验证测试标记与无种子迁移，挂接显式入口；普通 Core 不自动连接数据库。
- [x] `tools/verify-postgres.py`：初始化、随机凭据、创建角色 / 库、psql 迁移与重放、负面扩展 / 错库检查、执行测试、停止与清理。

## Task 3 · 验证与交付

- [x] 原生实库验收、Core、必要工具回归及独立复核；失败定位并记录 §B。
- [x] 更新开发说明与 SPEC，保留 vector 成功安装、生产 TLS / 多机、租户切换和内部签名上下文的待验收项。
- [x] 提交并推送已通过的功能分支。

## 验证记录 · 2026-11-03

- 原迁移在无扩展原生库上因 vector.control 缺失而回滚：`.artifacts/postgres/run-15d4adb453d543819d728b362fade72d`。
- 拆分后完整通过：`.artifacts/postgres/run-0bc82b0de23c456d8d6bc91bb9f0deaf/result.json`；passed / cleanupComplete 均 true。
- Core 退出 0：Node 3 项、Python 9 项、锁定还原、.NET 构建零警告 / 零错误、授权 / BFF HTTP / 生产密钥及真实 FastAPI 代理通过。
- 独立入口负面探针：缺配置和工作区外配置均在 configuration 阶段退出 1，无凭据泄漏。安装器 / 迁移 PowerShell 语法检查通过。
- 独立复核发现的扩展负面测试判据已修复；最终无未解决实质问题。SPEC B17–B19 记录本轮修复。
- 本轮未改前端，不重复此前已通过的 8 项浏览器验收；数据库与 Keycloak 当前分别运行，未声称完整生产部署验收。
