# T3 · 原生 Keycloak 浏览器联调

> **For agentic workers:** 沿用已确认的 Keycloak / OIDC / BFF 和原生进程方案；独立文件分别实现，统一联调与复核。不重新确认已授权的技术选择。

**Goal:** 使用真实 Keycloak 和 Chromium 验证现有 BFF 登录、会话与退出，提供可重复执行的本机验收入口。

**Architecture:** 官方 ZIP + SHA256 锁定 JDK / Keycloak，解压到忽略的本地目录。每次验证复制独立 Keycloak 运行目录，以临时 realm、随机账号密码和 H2 开发库启动，仅监听 loopback；启动真实平台程序集并执行 Playwright。结束时停止自己的进程、删除临时凭据 / 数据，保留脱敏结果与未填写的登录页截图。

**Tech Stack:** Windows 原生进程；Temurin JDK 25、Keycloak 26.8.1、现有 Python 3.13 stdlib、Node 24 / Playwright 1.61.1、ASP.NET Core BFF。

**Spec:** SPEC I.identity / I.deploy、V14 / V25 / V27；`docs/architecture/t3-bff.md`。

## 约束与复核重点

- 不改变系统 PATH、注册系统服务或使用 Docker；测试账号不进入业务 realm 文件或版本库。
- 下载校验精确 SHA256；验证器只使用已安装的锁定依赖，不自动下载。
- Keycloak 和平台端口从本机空闲端口分配；不得终止已有服务。
- 测试失败和中断也回收自己的进程；递归删除前核对绝对路径位于本次工作目录。
- 不输出口令、Cookie 或 OIDC token；正文 / 浏览器存储不得持有 token。Keycloak PAR 可能使授权 URL 不直接携带 PKCE 参数，验收以 realm 强制 S256 的真实成功登录为准。
- 保留标准 OIDC 测试服务的负面协议测试；此次真实 Keycloak 验收不代替 PostgreSQL 迁移、生产共享密钥或前端模板 SSO 接入。

## Task 1 · 官方原生依赖

- [x] 新增 `tools/native-identity.lock.json`，记录官方下载 URL、版本和 SHA256。
- [x] 新增并执行 `tools/install-native-identity.ps1`；只写 `.local-data/native-identity/`，验证 Java / Keycloak 版本。

## Task 2 · 原生验收入口

- [x] 新增 `tools/verify-keycloak.py`，克隆隔离运行目录、生成临时 realm 与账号、启动进程、等待真实 discovery / liveness。
- [x] 新增 `tests/platform/keycloak-browser.mjs`，真实表单错误密码拒绝、正常登录、原始 iss/sub、HttpOnly 与 token 不泄漏、409 重登录保护、CSRF / Origin 拒绝、退出重放和 Keycloak SSO 再登录。
- [x] 使用唯一环境变量 `ASSEMBLEAI_KEYCLOAK_TEST_CONFIG` 传本地配置路径，禁止把凭据放进进程命令行或测试日志。
- [x] 完整运行验收入口，验证停止与临时数据清理；排查实际集成失败并补回归。

## Task 3 · 结果与下一步

- [x] 新增 `BffKeyPersistenceTests.RunAsync()` 并挂接现有 smoke：实际 Production RSA PFX 加密落盘、重建后恢复、错误证书拒绝及临时凭据清理通过。
- [x] 独立复核启动、清理和测试边界，按变更执行现有代码检查。
- [x] 更新 SPEC / BFF 接入说明和开发记录，准确区分真实 Keycloak 已验收与 PostgreSQL 未验收；提交功能分支。
- [x] 只读评估 PostgreSQL / pgvector 原生依赖，确定可保持真实迁移链的下一步路径。

## 执行结果

- 最终真实 Keycloak / Chromium 验收返回 0，运行记录 run-d20c0f24e55f48c5a644a4a39323c118；passed / cleanupComplete 均为 true，剩余测试运行目录 0。
- 该次故意注入 Production、无效 JAVA / Keycloak DB、Playwright debug 与 Node 参数，隔离验证通过，指定调试日志未生成。
- 生产 PFX / keyring 恢复测试、Core 与 tooling 回归通过；修复记录见 SPEC B13–B16。
- 独立复核已处理 JVM 子进程回收和继承环境问题；原生 PostgreSQL / vector 依赖调查已完成，真实数据库验收仍未执行。
- 自动审批拒绝删除一个中断安装留下的临时解压目录，仅返回 blocked by policy；保留该忽略目录，不影响锁定安装与测试运行清理。
