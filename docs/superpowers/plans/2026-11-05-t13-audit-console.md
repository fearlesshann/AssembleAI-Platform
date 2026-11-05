# T13 Unified audit console and reference design Implementation Plan

> **For agentic workers:** Use superpowers:subagent-driven-development with independent backend, design and native acceptance ownership. User requires direct `main` development and strict fidelity to the supplied UI example.

**Goal:** 交付统一调用 / 审计 / 异常查询及导出，并将正式门户和已有管理页校准到用户示例的底层设计。

**Architecture:** 现有持久表经权限交集投影为安全元数据；统一查询采用固定时间窗、服务端保护游标与实时BFF复核。全局身份 / 注册审计和已选业务租户分开。Vue沿用现有生命周期，抽取参考设计共享token与本地字体，保留原快照不改。

**Tech Stack:** ASP.NET Core、PostgreSQL、Vue 3 / TypeScript、Element Plus、Node / Playwright；原生进程。

**Spec:** [SPEC.md](../../../SPEC.md) C6 / C8、I.audit / I.portal / I.authz；V4,V6,V7,V11,V12,V19,V20,V25,V33,V34,V36。

## Global Constraints

- 用户Git身份、仅`main`、无Docker要求、现有两个AI用例不扩展。
- 设计以`references/ui-v1`实际HTML/CSS为准，语义组件对齐颜色 / 字体 / 密度 / 尺寸 / 状态，具体参数见[设计合同](../../architecture/reference-design-contract.md)。
- 模型 / 数据历史保留精确manage资源过滤；身份管理员不自动获得业务审计权限。
- 新增`gateway:observe → application/{appId}`、`audit:read → audit/knowledge`及`audit:export → audit/events`均需显式tenantWide授权。
- 查询最大31天，默认50条 / 最多100条；导出当前筛选最多1000条，截断明确标记，无自动删除历史。
- 页面和日志不包含网关path、提示 / 回答、SQL / 参数、凭据 / 端点或文档标题。关联只在当前获授权范围查询。

## Review Focus

- 全局与当前租户、普通身份管理员与业务资源权限分离；停用 / 移除部署资源的获授权历史仍可查。
- 游标绑定主体、scope、tenant、权限版本、筛选与页大小；同时间多源记录稳定排序，权限变化不可复用旧页。
- 导出必须先持久审计，拒绝或持久化故障不交付结果；导出受每条读取授权约束。
- 页面请求取消 / 身份或租户变化 / 筛选变化不能恢复旧详情和下载；失败清除旧结果。
- 字体、本地静态服务MIME、浅暗主题、hover / focus、390px及主面板滚动需实际浏览器对照来源。

## Task 1: 查询、关联、导出与数据库合同

**Files:** `services/platform/Audit/*`、迁移13 / readiness、`tests/platform/Audit*`、合同。

**Interfaces:** `/api/platform/v1/audit/workspace`、`/events`、`/events/{source}/{id}`、`POST /export`；scope=tenant/global，固定from/to及source/status/appId/requestId/cursor/pageSize过滤；事件安全投影、keyset游标、导出items / truncated / exportedAt。

- [x] 固定10源（9类既有记录+导出审计）、来源授权与DTO，补必要迁移 / 索引。
- [x] 实现有界查询 / 详情 / 关联、保护游标、真实会话与权限版本复核。
- [x] 实现显式导出权限、1000条限额与持久审计；不自动purge。
- [x] 单元 / HTTP及真实PG验证边界；实质缺陷回写SPEC §B。

## Task 2: 用户示例设计校准

**Files:** `reference-tokens.css`、`portal.css`、既有管理页CSS、本地Inter字体 / license、`reference-design.spec.ts`、设计合同与AGENTS / PRODUCT / SPEC。

- [x] 抽取原CSS token及语义组件尺寸；记录来源及本地字体校验值。
- [x] 校准已有正式页面，原样例内容和代码保持不改。
- [x] 实际浏览器对比原样例和正式组件的浅暗token、侧栏 / 顶栏 / 字体 / 按钮 / 表格，并检查390px。

## Task 3: 审计页面与门户集成

**Files:** `AuditWorkspace.vue`、`audit-api.ts`、`audit.css`、`Portal.vue`、静态深链 / API / 浏览器测试。

**Routes:** `/manage/audit[/source/id]`及`/manage/audit/global[/source/id]`。详情与列表保持同scope；筛选绑定固定时间窗；JSON导出保留truncated和exportedAt。

- [x] 授权导航、筛选 / 查询 / 分页、元数据详情、requestId与gatewayRequestId关联。
- [x] 显式导出动作与完成 / 截断状态；加载 / 空 / 无权 / 错误 / 过期游标 / 迟到响应完整处理。
- [x] 复用共享设计基线，mock浏览器覆盖交互状态，原生浏览器访问真实API。

## Task 4: 验证与交付

- [x] 运行All代码回归与原生`--audit-browser`完整PG联合验收，审阅最终页面截图。
- [x] 独立复核权限、查询 / 导出边界及设计对照，处理发现的缺陷。
- [x] 更新SPEC / README / 开发记录，明确留存与生产部署边界，T13任务运行诊断仍待后续。
- [x] 使用用户Git账号提交并推送`main`，核验远程SHA及工作区状态。
