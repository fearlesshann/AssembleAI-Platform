# T13 模型与数据源管理 Implementation Plan

> **For agentic workers:** Use superpowers:subagent-driven-development to implement the independent contracts, pages and native acceptance; root integrates and reviews. User requires direct `main` development.

**Goal:** 正式门户交付模型、数据源与受控数据集的列表、详情、配置、授权测试及近期调用记录。

**Architecture:** 沿用现有 Vue 门户与 BFF，复用 I.models / I.connectors 的精确资源授权和 PostgreSQL 配置修订。列表增加必要的能力与部署上限投影；供应商端点、物理表和凭据仅在服务端部署配置。不增加 AI 用例，不引入新依赖。

**Tech Stack:** Vue 3 / TypeScript、ASP.NET Core、PostgreSQL、Node、Playwright；原生进程。

**Spec:** [SPEC.md](../../../SPEC.md) C8、I.portal、I.models、I.connectors、I.audit；V4,V6,V7,V11,V12,V19,V20,V25,V33,V34,V35。

## Global Constraints

- 仅使用 `main`；提交身份 `YeHan <mr.yehan@outlook.com>`；GitHub 账号 `fearlesshann`。
- 身份管理员不隐式取得业务模型或数据权限；模型管理 / 调用、数据源管理 / 连接测试 / 数据集访问分别校验。
- 页面只配置租户启用和受部署上限约束的输出、行数、时间预算；浏览器不读取密钥、端点、物理表或连接串。
- 使用当前所选业务租户。退出、换身份或换租户必须丢弃旧响应和内容。
- 近期记录保持现有最多50条，明确范围；统一筛选、分页、操作审计与运行诊断仍属T13后批。
- 浅色 / 暗色、窄屏和键盘保持现有 PRODUCT.md 风格；不以静态占位计为完成。

## Review Focus

- 管理权与调用 / 测试权独立；部署停用与租户停用状态可区分。
- 首次配置不存在、并发修订冲突、已提交但确认读取失败须可恢复，不能自动重复调用。
- 数据库锁等待期间退出 / 切租户 / 撤权，不得使旧授权提交配置与审计。
- 导航加载失败不得拖垮其他模块，深链刷新不得误退首页或404，非法资源路径仍拒绝。
- 测试响应及调用历史只显示安全投影，晚响应不跨身份恢复；资源最大限额来自服务端。

## Task 1: 后端管理合同与写边界

**Files:** `services/platform/Models/*`、`services/platform/Connectors/*`、对应 `tests/platform/*Model*Tests.cs` / `*Connector*Tests.cs`。

- [x] 固定列表能力、配置状态和部署上限字段，保持既有客户端兼容。
- [x] 为必要补齐与发现的授权 / 并发边界添加有意义回归。
- [x] 实现、执行 .NET 与真实 PostgreSQL 验证；实质缺陷由root回写§B。

## Task 2: 正式管理页面与门户集成

**Files:** `apps/portal/src/resource-api.ts`、`ResourceWorkspace.vue`、`resource-workspace.css`、`Portal.vue`、`tools/serve-frontend.mjs`、`tests/frontend/resource-api.test.mjs`、`e2e/resources.spec.ts`、`server.test.mjs`。

**Interfaces:** `/manage/models[/modelId]`、`/manage/data-sources[/sourceId]`；ResourceWorkspace props `section`, `resourceId`, `tenantId`, `refreshToken`；events `navigate`, `busy`, `dirty`, `unauthorized`。ID遵守既有`[a-z][a-z0-9._-]{0,63}`。

- [x] 有界同源API、CSRF、取消和安全错误；仅用户主动启动测试，非流式模型调用不自动重试。
- [x] 列表 / 搜索 / 详情、配置表单、受控数据集与字段、测试结果及近期50条记录。
- [x] 授权导航、精确深链、未保存离开、CAS冲突、失败重载、无权 / 空 / 加载状态。
- [x] 类型检查、构建、Node与浏览器状态回归，检查双主题 / 390px / 键盘。

## Task 3: 原生联合验收与交付

**Files:** `tests/platform/PostgresResourceBrowserTests.cs`、`tests/frontend/resource-postgres-browser.mjs`、`tools/verify-postgres.py`、测试入口、合同与开发文档。

- [x] 复用真实OIDC/BFF/PostgreSQL和本地模型协议服务，真实连接器只读合成数据。
- [x] 浏览器从深链到持久配置、测试、调用记录；数据库核验修订与审计；验证权限隔离及响应清除。
- [x] 运行适当完整回归，独立复核代码、页面和证据。
- [x] 更新合同 / README / SPEC C8与T13进度（整体仍`~`），以用户Git账号提交并推送`main`。


## 执行证据

All代码回归返回0：`.artifacts/t13-resources-all.log`，61 Node / 201+109 Python / 67 Playwright，类型检查、四构建、.NET和FastAPI链路通过。最终完整原生`--resource-browser`为`run-41a13bc7eba34e1cabf3634e87ea2d71`，passed / cleanupComplete true，resourceOnly false；12项真实锁等待竞争、生产资源页面与既有PG基线通过。独立复核无剩余P1/P2。根因、红绿证据及边界见[开发记录](../../development.md#2026-11-05-t13-第二批模型数据源与数据集管理)与SPEC B89–B93。
