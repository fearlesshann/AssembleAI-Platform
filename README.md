# 集智 / AssembleAI

制造业 AI 应用平台。内部团队独立交付应用，平台统一接入、授权、模型与数据治理；支持 SaaS 和私有化。

## 当前进度

T12 进行中：首期文档摄取已实现 PDF / Word（DOCX）/ TXT / Markdown 的上传、权限、原文下载、删除和后台提取。检索问答、知识库界面及 RabbitMQ / 向量链路仍待完成，见 [文档摄取接入说明](docs/architecture/knowledge-ingestion.md)。

T1–T11 已完成。ChatBI 独立 Vue / FastAPI 应用已接通自然语言提问、模型规划、授权 PostgreSQL 查询、结果与依据展示，支持独立访问及门户内运行。DeepSeek `deepseek-flash` 三题实测得到 300、200 和按产线汇总 300；工厂 / 租户隔离、撤权、断连及双调用审计通过。41 项 Node、178 项 Python、36 项浏览器测试与 .NET 检查通过，完整 PostgreSQL 合同及最终 ChatBI 实库 / 实际模型验收记录见开发文档。当前使用本机进程，制造业数据明确标注为测试数据，真实业务数据与生产部署另行交付。

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

此入口使用隔离的临时数据库和制造业测试数据，只支持页面上的三个示例问题，结果分别为 300、200 和按产线汇总 300；模型端采用固定示例查询方案，没有连接真实模型。登录、权限、应用运行、数据查询与结果展示经过实际服务。知识库页面和文档问答仍未交付。

体验最多运行四小时；另开终端执行 `powershell -File tools/local-demo.ps1 -Stop` 可提前停止并清理临时数据。已有服务和数据库不受影响。若 Node 不在系统路径，可用 `-Node` 指定完整路径。

## 工程基线

Vue 3 + TypeScript + Element Plus + Vite + qiankun；ASP.NET Core + YARP；独立 FastAPI 应用；PostgreSQL / pgvector、Redis、MinIO、RabbitMQ。

身份中心采用 Keycloak，通过标准 OIDC 接入。租户成员关系、角色与数据范围归平台管理。

## 下一里程碑

T12：交付企业知识库问答，落实文档格式、访问范围、分块 / embedding、检索与来源引用。ChatBI 的配置和启动见 [T11 接入说明](docs/architecture/chatbi.md)。模型网关与数据连接器默认关闭；由部署者声明目标和环境变量凭据引用，再按租户启用并授权，见 [模型接入](docs/architecture/model-gateway.md)和[数据接入](docs/architecture/connectors.md)。正式门户需配置身份、数据库和应用授权，见 [T6 本机登录配置](docs/architecture/portal.md)；子应用按 [T7 接入模板](docs/architecture/application-template.md) 配置。管理工作区入口 `/manage/applications`，旧演示模板保留在 `/reference-ui/`。Docker 是可选部署方式，开发使用本机进程。

`references/ui-v1` 中的模拟登录、模拟数据和占位测试不属于正式平台实现。该目录保留来源仓库的 LICENSE；源码未改写。
