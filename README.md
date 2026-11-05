# 集智 / AssembleAI

制造业 AI 应用平台。内部团队独立交付应用，平台统一接入、授权、模型与数据治理；支持 SaaS 和私有化。

## 当前进度

平台核心身份、权限、应用注册、门户、网关及公共模型 / 数据能力已建立。正式平台当前主要包含应用中心与应用管理；用户 / 成员、租户 / 组织、角色权限、模型 / 数据源、统一调用 / 审计 / 异常等管理功能与页面仍需补齐。T1–T12按各自任务范围完成，不代表平台管理与生产交付已经完成。

两个AI示范用例已交付：ChatBI支持授权数据查询、结果与依据；企业知识库支持PDF / DOCX / TXT / Markdown摄取、真实MinIO / RabbitMQ / 本机embedding / pgvector链路、问答与原文引用。两者均支持独立 / 门户运行，并通过实际DeepSeek合成题集及隔离 / 撤权验收，结果见[开发记录](docs/development.md)。当前使用本机进程；实际业务效果与生产部署仍待后续验收。

用户已明确现有两个用例足够。后续优先交付平台本体的完整功能和页面，按[SPEC C8](SPEC.md#c8-平台功能与页面交付清单)逐项推进；平台建设与用例建设分别汇报，不再通过新增用例扩展当前范围。

- [产品与验收规格](SPEC.md)
- [T1 实施决策与 T2 开工清单](docs/architecture/t1-decisions.md)
- [T3 身份与授权基础](docs/architecture/t3-authorization.md)
- [T4 注册、审核与租户启停](docs/architecture/registry.md)
- [T5 正式网关与运行契约](docs/architecture/gateway.md)
- [T6 授权门户与本机登录配置](docs/architecture/portal.md)
- [T7 子应用 SDK、独立接入与发布物部署](docs/architecture/application-template.md)
- [T8 应用注册与生命周期管理工作区](docs/architecture/registry-management.md)
- [T9 模型配置、调用协议与验收边界](docs/architecture/model-gateway.md)
- [T10 只读数据连接器、权限与查询协议](docs/architecture/connectors.md)
- [T11 ChatBI 接入、查询链与题集验收](docs/architecture/chatbi.md)
- [T12 文档摄取、解析与删除边界](docs/architecture/knowledge-ingestion.md)
- [T12 知识库索引、问答与引用](docs/architecture/knowledge-qa.md)
- [知识库原生服务与验收](docs/native-knowledge.md)
- [内部调用身份与权限契约](docs/architecture/t3-internal-calls.md)
- [原生 Keycloak 验收入口](docs/native-identity.md)
- [原生 PostgreSQL 验收入口](docs/native-postgres.md)
- [本地开发与验证结果](docs/development.md)
- [后端与基础设施启动](docs/development-services.md)
- [旧 UI 复用评估](docs/architecture/ui-reuse-audit.md)
- [旧 UI 原样快照](references/ui-v1/README.md)
- [快照来源与文件校验值](references/ui-v1.source.json)

## 本机可操作体验

构建前端、.NET 解决方案并安装原生 PostgreSQL 与 ChatBI 的锁定 Python 环境后，运行 `powershell -File tools/local-demo.ps1`。访问 `http://127.0.0.1:4174`，点击“企业账号登录”，选择体验租户，再打开“ChatBI 本地体验”。本机测试身份自动登录，无需填写账号密码。

此入口使用隔离的临时数据库和制造业测试数据，只支持页面上的三个示例问题，结果分别为300、200和按产线汇总300；模型端采用固定示例查询方案，没有连接真实模型。登录、权限、应用运行、数据查询与结果展示经过实际服务。该体验入口目前只装配ChatBI；已交付的知识库有独立配置与验收入口，见[知识库接入](docs/architecture/knowledge-qa.md)。

体验最多运行四小时；另开终端执行 `powershell -File tools/local-demo.ps1 -Stop` 可提前停止并清理临时数据。已有服务和数据库不受影响。若 Node 不在系统路径，可用 `-Node` 指定完整路径。

## 工程基线

Vue 3 + TypeScript + Element Plus + Vite + qiankun；ASP.NET Core + YARP；独立 FastAPI 应用；PostgreSQL / pgvector、Redis、MinIO、RabbitMQ。

身份中心采用 Keycloak，通过标准 OIDC 接入。租户成员关系、角色与数据范围归平台管理。

## 下一里程碑

T13：优先补齐平台管理功能及页面。按用户 / 成员、租户 / 组织、角色权限与管理导航，模型 / 数据连接管理，调用 / 审计 / 异常与任务诊断的顺序逐功能交付；已有应用管理持续回归。T14 / T15继续落实稳定交付、备份恢复和性能 / 可用性验收。完整清单与完成标准见[SPEC C8](SPEC.md#c8-平台功能与页面交付清单)。

模型网关与数据连接器默认关闭；由部署者声明目标和环境变量凭据引用，再按租户启用并授权，见[模型接入](docs/architecture/model-gateway.md)和[数据接入](docs/architecture/connectors.md)。正式门户需配置身份、数据库和应用授权，见[T6本机登录配置](docs/architecture/portal.md)；子应用按[T7接入模板](docs/architecture/application-template.md)配置。管理工作区入口 `/manage/applications`，旧演示模板保留在 `/reference-ui/`。Docker是可选部署方式，开发使用本机进程。

`references/ui-v1` 中的模拟登录、模拟数据和占位测试不属于正式平台实现。该目录保留来源仓库的 LICENSE；源码未改写。
