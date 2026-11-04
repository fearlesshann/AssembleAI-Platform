# 集智 / AssembleAI

制造业 AI 应用平台。内部团队独立交付应用，平台统一接入、授权、模型与数据治理；支持 SaaS 和私有化。

## 当前进度

T1–T9 已完成。门户、子应用模板与应用管理已接身份、授权、注册及网关；统一模型网关支持租户配置、受控子应用委托、JSON / SSE 和来源审计。T9 的 Core（36 项 Node、91 项 Python 及 .NET HTTP）、补充 2 项 Python 回归，以及真实 PostgreSQL / OIDC / YARP / FastAPI / Chromium 联合验收通过；用户提供的 DeepSeek `deepseek-flash` 已完成实际 JSON 与流式调用。当前使用本机进程开发，生产部署按后续任务交付。

- [产品与验收规格](SPEC.md)
- [T1 实施决策与 T2 开工清单](docs/architecture/t1-decisions.md)
- [T3 身份与授权基础](docs/architecture/t3-authorization.md)
- [T4 注册、审核与租户启停](docs/architecture/registry.md)
- [T5 正式网关与运行契约](docs/architecture/gateway.md)
- [T6 授权门户与本机登录配置](docs/architecture/portal.md)
- [T7 子应用 SDK、独立接入与发布物部署](docs/architecture/application-template.md)
- [T8 应用注册与生命周期管理工作区](docs/architecture/registry-management.md)
- [T9 模型配置、调用协议与验收边界](docs/architecture/model-gateway.md)
- [内部调用身份与权限契约](docs/architecture/t3-internal-calls.md)
- [原生 Keycloak 验收入口](docs/native-identity.md)
- [原生 PostgreSQL 验收入口](docs/native-postgres.md)
- [本地开发与验证结果](docs/development.md)
- [后端与基础设施启动](docs/development-services.md)
- [旧 UI 复用评估](docs/architecture/ui-reuse-audit.md)
- [旧 UI 原样快照](references/ui-v1/README.md)
- [快照来源与文件校验值](references/ui-v1.source.json)

## 工程基线

Vue 3 + TypeScript + Element Plus + Vite + qiankun；ASP.NET Core + YARP；独立 FastAPI 应用；PostgreSQL / pgvector、Redis、MinIO、RabbitMQ。

身份中心采用 Keycloak，通过标准 OIDC 接入。租户成员关系、角色与数据范围归平台管理。

## 下一里程碑

T10：实现只读数据连接器、组织范围过滤、查询约束与工具授权，为 ChatBI 准备数据访问能力。模型网关默认关闭；由部署者配置接口、模型与环境变量凭据引用，再按租户启用并授权，见 [T9 接入说明](docs/architecture/model-gateway.md)。正式门户启动需配置身份、数据库和应用授权，见 [T6 本机登录配置](docs/architecture/portal.md)；子应用按 [T7 接入模板](docs/architecture/application-template.md) 配置。管理工作区入口 `/manage/applications`，旧演示模板保留在 `/reference-ui/`。Docker 是可选部署方式，开发使用本机进程。

`references/ui-v1` 中的模拟登录、模拟数据和占位测试不属于正式平台实现。该目录保留来源仓库的 LICENSE；源码未改写。
