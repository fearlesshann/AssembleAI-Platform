# 企业知识库索引与问答

T12 在[文档摄取](knowledge-ingestion.md)之上增加受控索引、检索与独立问答应用。代码回归、原生 MinIO / RabbitMQ / pgvector / 浏览器联合链路及实际 DeepSeek 合成题集已通过，证据与适用范围见[开发记录](../development.md)。

## 部署与职责

| 进程 | 职责 | 允许持有的凭据 |
| --- | --- | --- |
| 平台 ASP.NET Core | BFF 文档管理、任务 / ACL / 审计、MinIO 与 RabbitMQ、pgvector 检索、模型网关 | 当前部署的数据库 / 存储 / 队列 / 模型配置 |
| `knowledge_backend.main:app` | 私有解析与本机 embedding | 私有解析密钥；模型目录 |
| 解析子进程 | 有界 PDF / DOCX / TXT / Markdown 文本提取 | 无服务凭据 |
| `knowledge_backend.application:app` | 独立业务 JWT 校验、活动能力检索、问题编排、引用校验 | 单次活动模型 / 知识库能力；无上游服务密钥 |
| 知识库前端 0.1.0 | 文档操作、问题、答案与原文引用 | 同源 BFF 会话；受限应用 SDK，不接收后端密钥 |

前后端均使用 `knowledge` 应用 / 服务标识、`0.1.0` 版本。应用准入权限为 `knowledge:use`，资源 `application/knowledge`；文档读取与管理仍独立授权。

平台在既有 Knowledge / Objects 配置之外显式设置：

```json
{
  "Knowledge": {
    "Enabled": true,
    "WorkerEnabled": true,
    "IndexEnabled": true,
    "ParserEndpoint": "http://127.0.0.1:8093/internal/parse",
    "EmbeddingEndpoint": "http://127.0.0.1:8093/",
    "ParserKeyEnvironmentVariable": "ASSEMBLEAI_KNOWLEDGE_PARSER_KEY",
    "AllowInsecureLoopback": true,
    "BrokerEnabled": true,
    "BrokerConnectionEnvironmentVariable": "ASSEMBLEAI_KNOWLEDGE_AMQP_URI",
    "BrokerQueue": "assembleai.knowledge"
  }
}
```

私有进程通过 `ASSEMBLEAI_KNOWLEDGE_MODEL_DIRECTORY` 加载已下载且摘要匹配的固定模型。运行时不下载模型，不隐式使用外网 embedding。配置、依赖安装与协议详见 [后端说明](../../apps/knowledge/backend/README.md)和[原生服务](../native-knowledge.md)。本机 HTTP 只允许显式 loopback；正式部署使用 TLS 与受控网络。

## 索引与消息

核心迁移 11 增加向量片段和检索审计，不要求 vector 扩展才能建立核心表。`enable-vector.sql` 单独安装并严格检查 pgvector 0.8.7；启用知识检索的部署必须完成该步骤。readiness 核心 schema 版本要求 11，`RequireVector` 保持独立检查。

固定 profile `e5-small-dd476dd0-384-v1` 绑定 multilingual-e5-small ONNX / tokenizer 摘要与分段算法。E5 的 `passage: ` / `query: ` 前缀、特殊 token 均计入 512 token；文档按原文精确子串继续分段，问题超限明确拒绝。mask mean pooling + L2 normalization 生成 384 维有限单位向量。核心表存受约束的 `real[]`；查询显式转换为 pgvector 并计算余弦距离。首期使用精确扫描，不声称具备大规模 ANN 性能。

开启 IndexEnabled 时，摄取完成在同事务登记 embed 任务。每次领取最多处理 8 个原始片段、64 个子片段；成功保存进度后重新排队并重置连续失败计数。最多 4096 个索引子片段；整个文档同 revision/profile 完成后才发布 `ready`。每次任务预算 30 秒，租约 60 秒，暂态失败最多 3 次；永久错误直接失败。

PostgreSQL 任务是事实源，RabbitMQ 消息只是持久工作通知。消费者根据 jobId 重新加载租户、文档、revision、创建者和当前权限，不能相信消息提供的授权。重复通知、过期租约、旧版本结果均受数据库围栏限制。broker 未启用时保留显式数据库轮询；启用后的消息确认、失败队列和恢复行为必须经过真实 broker 验证。

删除同事务写 `deleted` 墓碑、清文字与向量、取消 ingest/embed、登记对象 purge。旧任务无法恢复可读内容；对象清理失败也不解除墓碑。提交索引前同时复核创建者完整管理 grant、文档 revision、租约与有效组织。数据库事务与外部身份会话并非跨系统原子事务；检索和最终回答继续独立复核。

此前在 IndexEnabled=false 下已到 `extracted` 的文档不会自动建立新索引。当前未提供重建索引 API；需在启用索引后重新上传，确认新文档可问答再按需要删除旧文档。界面将 `extracted` 显示为“已解析，尚未可用于问答”，不能把它当成 ready。

## 活动检索与权限

Gateway 每个受控操作显式配置 `KnowledgeRead=true`，才签发独立 `X-Knowledge-Delegation`。`POST /ask` 同时声明 `ModelIds=["assistant"]`，`POST /references` 不声明模型能力。知识库能力使用独立注册表，业务 JWT、模型能力、数据连接器能力不能互换；父调用结束即撤销。

| 内部接口 | 请求 | 返回 |
| --- | --- | --- |
| POST `/internal/knowledge/search` | `{question,limit}`；1–1000字，1–5条命中 | `{requestId,authorizationVersion,profile,hits}` |
| POST `/internal/knowledge/check` | `{authorizationVersion,profile,documents:[{documentId,revision}]}`；最多5份 | `{valid:true}` 或拒绝 |

内部请求以知识库能力作为单个 Bearer 凭据。平台恢复可信主体 / 租户 / 父运行，以当前授权状态查询。SQL 将同一 grant 上的资源与组织一起匹配，在排名前过滤当前租户、有效组织、ready、revision、profile；集合 read/manage 或精确文档 read/manage 均遵循相同规则。空组织文档需要 tenant-wide。文档列表的最近100条限制不限制检索范围，也不充当授权集合。

命中包含 `chunkId,documentId,documentRevision,fileName,locator,text,score`。chunkId 使用 `<documentId的N格式>-<revision>-<原分块index>-<子片段index>`；引用位置保持真实页 / 段 / 行。数据库核对当前授权版本，检索后复核全部命中文档。check 在一个数据库检查中复核所有上下文，父调用在入口和出口检查，避免重复放大查询。

检索先持久准入审计、交付前记录结果，仅保存主体、租户、版本、profile、数量、耗时、结果与可信 app/release/run/gatewayRequestId。不保存问题、片段、向量、模型答案或活动能力。模型与知识检索审计关联同一原始网关调用。

## 答案与引用

独立后端只将授权片段提供给现有模型网关。检索后、模型前和最终 complete 前检查所有上下文文档；最终检查与断连竞争并有界收尾，不让后台检查占用另一并发槽。

模型输出必须是 `{answer,citations:[{chunkId,quote}]}` 或明确的 `insufficient_evidence` 拒答。引用最多5条，chunkId必须来自本次检索、quote必须是该片段的精确连续子串。文档名、revision和位置从可信命中附加，模型不能提供下载路径或伪造来源。无命中直接固定拒答，不调用模型。引用校验不等同于对自由文本每个结论的形式化证明；实际答案质量仍以明确题集评估。

阶段 SSE 为 retrieving / answering / verifying，空命中跳过 answering，等待时每10秒心跳。完整答案只出现在最终 complete；超时、撤权、断流均不显示部分答案。问答总限180秒，模型120秒；内部检索30秒 / 检查3秒，客户端35秒 / 5秒。

前端不持久化答案历史。主体、租户、SDK运行能力变化或取消会清除旧内容，迟到响应不能恢复。焦点返回时先隐藏旧答案，通过新的 `/references` 活动调用复核全部上下文，通过才恢复；失败则清除。主题更新不会丢失答案。已交付并被用户看见或下载的字节无法撤回。

文档上传与删除走同源 BFF、CSRF与expectedRevision。原文下载再次授权、核对大小/SHA和当前文档后按附件返回；无公开或预签名对象地址。处理状态最多自动刷新5分钟，后台页面不持续轮询，用户可手动刷新。

## 验证入口

```powershell
./tools/verify.ps1 -Scope All
# 以下要求固定模型与独立原生服务已安装；命令启动并清理自己的临时实例。
apps/sample/backend/.venv/Scripts/python.exe tools/verify-postgres.py --knowledge-index-only
apps/sample/backend/.venv/Scripts/python.exe tools/verify-postgres.py --knowledge-browser
# 额外使用已显式配置的真实模型，凭据仅环境变量。
apps/sample/backend/.venv/Scripts/python.exe tools/verify-postgres.py --knowledge-real-model
```

Core/All的Python语义向量专项需要 `ASSEMBLEAI_KNOWLEDGE_TEST_MODEL_DIRECTORY`；未配置时显式跳过真实模型权重测试，不将跳过视为本地embedding验收。联合验收则必须配置 `ASSEMBLEAI_KNOWLEDGE_MODEL_DIRECTORY`，不会以模拟向量替代。
