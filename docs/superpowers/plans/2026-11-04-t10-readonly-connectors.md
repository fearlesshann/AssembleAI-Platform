# T10 Read-only Connectors Implementation Plan

**Goal:** 交付真实 PostgreSQL 只读数据连接器、租户配置、工厂范围过滤与工具授权，为 T11 ChatBI 提供可验证的数据执行边界。

**Architecture:** 延用 .NET 控制面与当前授权快照。部署者声明源、数据集、物理字段及环境变量凭据引用；租户管理员只能启停并收紧行数 / 时限。调用者提交严格结构化查询，服务端生成白名单 SELECT 和参数，始终下推租户与工厂 UUID 交集。BFF 用实时会话和 CSRF，执行前后复核授权与配置，执行期间取消 / 撤权终止查询。

**Tech Stack:** .NET 10、既有 Npgsql、PostgreSQL 17.11、本机进程；不增加依赖。

**Spec:** SPEC.md T10 / D5 / I.connectors / I.authz，V4,V5,V6,V7,V12,V18,V20,V21,V25。用户确认使用制造业测试数据；本期不声称业务数据或 ChatBI 问答效果完成。

## Design decisions

- 工厂、产线、每日产量与不良数为明确标注的测试数据。工厂对应平台平面 organization UUID；不推导组织树、部门继承或上下级权限。
- 采用结构化查询而非任意 SQL：允许列投影、AND 筛选 eq/gte/lte、count/sum/avg/min/max 聚合；聚合时投影列是分组列。只允许声明字段，过滤值均参数化。后续模型也只能生成此查询契约。
- `datasource:manage` 对 datasource/{sourceId} 的 tenantWide 授权控制配置与审计；连接测试还需 `datasource:test` 对同一资源及 `tool:invoke` 对 tool/connector.test 的 tenantWide 授权。
- 查询每个工厂同时要求 `datasource:read` 对 datasource/{sourceId}、`dataset:read` 对 dataset/{datasetId}、`tool:invoke` 对 tool/connector.query。逐个组织调用既有 PermissionEvaluator，三种权限取交集；空集拒绝。客户传租户 / 工厂 Header 无效。
- 默认关闭、源 / 数据集白名单各最多 100；字段最多 32。请求最多 16 KiB，最多 16 个投影列、8 个指标、16 个筛选；最多 500 行、单元格 4 KiB、结果 1 MiB；限时最多 30 秒，单用户 2 / 全局 32 并发、每分钟 60 次，无重试。
- 源连接字符串仅从部署配置指定的环境变量解析。驱动使用专用无池连接、只读事务、statement_timeout / lock_timeout；拒绝超级用户 / 建库 / 建角色 / bypassrls 及对受控表有写权限的角色。结果类型严格限制为 text / int64 / decimal / date。
- 迁移 8 存租户源配置与配置审计、执行审计；保存状态 / 行数 / 时长 / 主体 / 数据集，不保存查询值、结果、连接字符串或异常详情。完成审计失败不返回数据；完成后再次复核实时权限。
- T10 提供 BFF 管理 / 查询接口与独立执行器。T11 将业务子应用接入此边界；T13 承接管理页面，T14 承接多实例限流与生产部署。

## Tasks

- [x] 定义 Connectors contracts/catalog/query compiler：配置校验、严格查询 JSON、参数化 SQL、组织交集。先写边界测试并确认失败，再实现。
- [x] 实现 PostgresConnectorClient 与独立只读账号验收：连接测试、类型 / 行数 / 大小预算、只读限制和取消。
- [x] 实现迁移 8 与 IConnectorStore/PostgresConnectorStore：CAS 配置、原子配置审计、调用开始 / 终态 / 最近 50 条，真实 PG 回归。
- [x] 实现 ConnectorEndpoints / ConnectorExecutor：真实 BFF/CSRF、精确资源授权、部署与租户配置合并、周期撤权检查、审计与结果交付。默认关闭，注册到 PlatformApplication。
- [x] 添加真实 PG/OIDC/HTTP 验收：A/B 工厂与异租户数据、聚合基准、工具权限交集、注入/写语句/未知字段拒绝、停用/撤权、只读角色、超限与安全错误。更新测试 dispatcher/readiness 为迁移 8。
- [x] 运行 Core 与原生 PostgreSQL；独立代码审查；更新 SPEC/配置文档/测试数据说明与 README，提交推送当前分支。

## Review Focus

- 同一 permission/resource/scope 不能与其他 grant 拼接；租户级工具授权不扩大数据范围。
- 行过滤必须先于聚合；不能把未授权行计入 count/sum 后再隐藏明细。
- 关闭源、退出、切租户或撤权发生在执行 / 审计等待中，结果必须被阻止。
- 数据库连接或 SQL 错误不得回显地址、凭据、参数值、SQL 或数据。
- 结果超限明确失败，不把截断结果宣称为完整查询；请求与执行预算均不可延长。

**Verification:** `dotnet build AssembleAI.slnx --no-restore`; SmokeTests `--connector-tests`; `tools/verify.ps1 -Scope Core`; `apps/sample/backend/.venv/Scripts/python.exe tools/verify-postgres.py`。
