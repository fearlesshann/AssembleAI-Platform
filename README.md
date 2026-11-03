# 集智 / AssembleAI

制造业 AI 应用平台。内部团队独立交付应用，平台统一接入、授权、模型与数据治理；支持 SaaS 和私有化。

## 当前进度

T1、T2 已完成：依赖锁定与独立环境、原生启动、前后端构建、8 项浏览器测试及后端检查均通过。T3 已实现授权基础和 OIDC / BFF 登录、服务端会话、CSRF 退出，完整 HTTP 协议测试与 Core 检查通过；真实 Keycloak / PostgreSQL 联调及内部签名上下文待完成。当前使用本机进程开发。

- [产品与验收规格](SPEC.md)
- [T1 实施决策与 T2 开工清单](docs/architecture/t1-decisions.md)
- [T3 身份与授权基础](docs/architecture/t3-authorization.md)
- [本地开发与验证结果](docs/development.md)
- [后端与基础设施启动](docs/development-services.md)
- [旧 UI 复用评估](docs/architecture/ui-reuse-audit.md)
- [旧 UI 原样快照](references/ui-v1/README.md)
- [快照来源与文件校验值](references/ui-v1.source.json)

## 工程基线

Vue 3 + TypeScript + Element Plus + Vite + qiankun；ASP.NET Core + YARP；独立 FastAPI 应用；PostgreSQL / pgvector、Redis、MinIO、RabbitMQ。

身份中心采用 Keycloak，通过标准 OIDC 接入。租户成员关系、角色与数据范围归平台管理。

## 下一里程碑

完成真实 Keycloak / PostgreSQL 联调与内部可信上下文，再推进注册、网关和门户的最小闭环。[BFF 接口与原生配置](docs/architecture/t3-bff.md) 已提供；首页仍为演示登录。Docker 是可选部署方式，开发使用本机进程。

`references/ui-v1` 中的模拟登录、模拟数据和占位测试不属于正式平台实现。该目录保留来源仓库的 LICENSE；源码未改写。
