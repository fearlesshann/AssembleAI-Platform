# T13 身份管理实施计划

> **For agentic workers:** Use superpowers:subagent-driven-development to implement this plan. 用户已授权直接开发，仅使用 main；独立文件分工，不创建分支或 worktree。

**Goal:** 交付用户、租户、成员、组织、角色权限管理的后端、页面与真实验收。

**Architecture:** 显式全局 / 租户身份管理员与既有应用开发审核角色分离。复用 BFF、实时授权与 PostgreSQL；角色保持精确资源和显式组织范围，变更、CAS 和审计同事务。Vue 门户增加授权导航、列表与详情编辑。

**Tech Stack:** 现有 ASP.NET Core / Npgsql、Vue 3 / TypeScript / Element Plus、Playwright、原生 PostgreSQL；不增依赖，不用 Docker。

**Spec:** [SPEC](../../../SPEC.md) C8、I.identity、I.authz、I.portal、I.audit；[身份管理合同](../../architecture/identity-management.md)。

## 全局约束

- 覆盖 V4/V5/V6/V7/V11/V12/V14/V20/V25/V33/V34/V35；既有业务接口和两个用例保持兼容。
- main 提交与用户现有 GitHub 身份；子任务不独立提交，最终集成检查后提交。
- 平台管理员不自动获得业务读取权；租户管理员不能自升为全局管理员或授予身份管理权。
- 迁移不种账号；成功写与审计同事务；严格 JSON、CSRF、实时会话与修订保护。
- 本批只对应 T13 第一批，不将 T13 整体标记完成。

## Review Focus

1. global actor 无 membership 也可管理禁用租户，但业务 API 仍拒绝：实库与 HTTP 验证。
2. tenant admin 无法跨租户或通过角色/成员表单改变管理员身份：实库拒绝测试。
3. 两个相同修订请求、审计失败、管理员撤权：实库 CAS / 原子回滚 / 同会话重新检查。
4. 保存已成功但回读失败、晚到响应与租户切换：浏览器状态回归。
5. 现有登录、目录、应用管理在新管理接口不可用时仍可用：既有前端完整回归。

## Task 1: 后端管理边界与事务

**Files:** `deploy/postgres/migrations/0012_identity_management.sql`、`apply.sql`；`services/platform/IdentityManagement/*`、`PlatformApplication.cs`；`tests/platform/IdentityManagement*Tests.cs`、`Program.cs`。

**Interfaces:** 上述合同 GET workspace / tenant snapshot；PUT user / tenant / member / organization / role。成功 `{revision}`，拒绝 / 冲突 / 不可用采用安全状态码。

- [x] 先编写接口与输入边界负例，运行红测。
- [x] 增加幂等 migration 12、管理员表、revision、审计与 store。
- [x] 接入真实 BFF、CSRF、会话复核、事务锁及授权验证。
- [x] 运行定向 HTTP / store 验证，审查严格 JSON 与权限边界。

## Task 2: 五类管理页面

**Files:** `apps/portal/src/Portal.vue`、`IdentityWorkspace.vue`、`identity-api.ts`、`identity.css`、`portal.css`；`tests/frontend/identity-api.test.mjs`、`tests/frontend/e2e/identity.spec.ts`；`tools/serve-frontend.mjs`、`tests/frontend/server.test.mjs`。

**Interfaces:** workspace 为导航权限提示和全局数据来源，tenant snapshot 提供成员 / 组织 / 角色 / 局部审计；保存后重新读取。

- [x] 先增加协议与浏览器行为测试，确认新路由当前缺失。
- [x] 接入统一授权导航、全局与租户管理上下文及列表 / 详情深链。
- [x] 完成五类创建 / 编辑 / 启停和角色授权表单。
- [x] 覆盖主题 / 窄屏 / 键盘、草稿离开、冲突、撤权 / 会话变化 / 迟到响应。
- [x] Node 测试、类型检查、门户构建、定向 Playwright 通过。

## Task 3: 原生数据库与浏览器验收

**Files:** `tests/platform/PostgresIdentityManagement*Tests.cs`、`PostgresIdentityBootstrapTests.cs`、`PostgresIdentityCapacityTests.cs`、`PostgresIntegrationTests.cs`；`tests/frontend/identity-postgres-browser.mjs`；`tools/verify-postgres.py`、`services/platform/DependencyReadinessCheck.cs`。

- [x] 增加 migration 12 重放 / 不种账号 / 缺迁移 readiness 拒绝。
- [x] 实库验证授权、跨租户、CAS、撤权、管理员保护及审计失败回滚。
- [x] 真实 OIDC / BFF / PG / 浏览器验证创建、授权、停用和操作记录。
- [x] 运行已有原生 PG 全套及完整代码回归；保留结果路径。

## Task 4: 引导、文档与交付

**Files:** `deploy/postgres/bootstrap-identity-admin.sql`、`tools/bootstrap-identity-admin.mjs`、`tests/frontend/identity-bootstrap.test.mjs`、`docs/architecture/identity-management.md`、`SPEC.md`、`README.md`、`docs/development.md`。

- [x] 提供显式引导管理员脚本，验证重放 / 既有身份绑定 / 错误目标。
- [x] 独立复核后端与页面差异，必要修复回溯 §B / §V。
- [x] 根据实际结果更新状态，不将未验收功能写成完成。
- [x] 检查 diff、凭据、工作区与提交身份，提交并推送 main。

## 验证命令

```powershell
dotnet build AssembleAI.slnx --no-restore --configuration Debug
dotnet tests/platform/bin/Debug/net10.0/AssembleAI.Platform.SmokeTests.dll --identity-management-tests
npm test
npm run check
npm run build --workspace @assembleai/portal
npx --no-install playwright test tests/frontend/e2e/identity.spec.ts
apps/sample/backend/.venv/Scripts/python.exe tools/verify-postgres.py --identity-browser
powershell -NoProfile -File tools/verify.ps1 -Scope All
```

具体入口随实现接线，验收结果与差异记录在开发日志。
