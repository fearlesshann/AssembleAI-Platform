# ChatBI：生产数据分析

`apps/chatbi/frontend` 与 `apps/chatbi/backend` 为独立工程，前端发布物、后端服务及首个应用发布组合均为 `0.1.0`。应用标识 / 后端服务为 `chatbi`，入口权限为 `chatbi:use`。同一 UMD 发布物在 `/apps/chatbi/0.1.0/` 独立访问或由门户加载；通过既有注册、独立审核与租户启用流程发布，无需新增门户硬编码入口。

当前使用明确标注的制造业测试数据。自然语言只用于生成受控查询，数字来自真实连接器结果；不生成第二次模型总结，不把业务行发给模型。示例问题由用户点击填入，分析须明确提交。数据集由当前权限发现，不能用页面参数扩展权限。

## 请求链与身份

浏览器 → BFF / YARP → 独立 FastAPI → 模型网关生成查询 → 数据连接器执行 → FastAPI 校验权限版本 → SSE → 页面结果与依据。

下游首先验证原业务 JWT、服务版本与当前准入。网关分别签发仅限本次活动调用的模型能力和连接器能力；二者使用不同注册表，不能互换。浏览器伪造的委托头在网关被清除；能力与数据库 / 模型凭据均不进入浏览器。业务 JWT 本身不能调用连接器或模型内部 API。能力随原请求取消、完成、会话失效或运行撤销结束，不用于后台任务。

源、数据集和工具三类权限在平台执行边界重新求当前工厂交集；模型和应用没有租户 / 工厂覆盖字段。数据查询仍执行专用只读角色检查、只读事务、强制租户过滤及聚合前工厂过滤。连接器查询返回授权版本和配置修订号，FastAPI 在发出结果及 complete 前复核；权限 / 配置变化、撤权、关闭页面、超时均中止，不保留可见的临时结果。

迁移 9 给连接器审计补全 `appId / releaseVersion / runId / gatewayRequestId`；四字段全空或全部有效。两个委托调用可关联到原网关请求。审计不保存问题、模型计划、结果行或委托凭据。

## 应用 API

浏览器经 `/api/apps/chatbi/releases/0.1.0` 调用以下操作，使用现有受控 SDK 和活动运行；独立访问也必须真实登录、选择租户并明确连接。

| 方法 / 路径 | 请求 / 响应 |
| --- | --- |
| GET `/datasets` | `{items:[{sourceId,datasetId,displayName,columns:[{name,type}],maxRows}]}`，仅当前授权数据集 |
| POST `/ask` | `{datasetId,question}`；仅 SSE，禁止额外身份、SQL 或供应商参数 |

问题 1–1000 字符，正文最多 8 KiB，总预算 150 秒。首个 planning 事件立即发送，等待模型时每 10 秒心跳；一问只有一次模型生成，没有自动重试或自动修复。

| SSE 事件 | 数据 |
| --- | --- |
| delta | `{kind:"stage",stage:"planning"\|"querying"\|"receiving"}` |
| delta | `{kind:"rows",rows:(string\|null)[][]}` |
| complete | `{requestId,modelRequestId,queryRequestId,sourceId,datasetId,datasetName,columns,rowCount,queryText,scope,summary}` |
| error | `{code,requestId}` |

事件最多 32 KiB，页面在匹配列宽 / 行数的 complete 后一次性交付结果。数字按精确字符串传输，避免浏览器浮点舍入。取消、断流、重复 complete、顺序错误或结果结构异常均不能展示成功。查询依据展示已验证的原始 DSL、授权范围和关联编号；summary 为行数说明，不伪装模型分析。

模型只收到数据集语义别名、类型与问题，输出严格 JSON DSL，或 `{refusal:"unsupported_question"}`。必须 `finishReason=stop`；拒绝 Markdown 包装、任意 SQL、未知字段 / 列、身份覆盖、非法类型或有损小数。DSL 沿用[连接器协议](connectors.md)，ChatBI 进一步限定最多 100 行，保留原 JSON 数字字面量传往平台。

## 内部连接器协议

仅接受活动连接器能力的 Bearer，不接受浏览器 Cookie 或业务 JWT；最多 100 个部署数据集候选。

| 方法 / 路径 | 契约 |
| --- | --- |
| GET `/internal/connectors/datasets` | 同应用 datasets 形状；无物理表、组织 UUID、连接信息 |
| POST `/internal/connectors/{sourceId}/datasets/{datasetId}/query` | 原始 DSL；返回 `{requestId,columns,rows,authorizationVersion,configurationRevision}` |
| POST `/internal/connectors/{sourceId}/datasets/{datasetId}/check` | `{authorizationVersion,configurationRevision}`；成功 `{valid:true}`，不查询源数据 |

metadata / check 服务端总预算 3 秒，Python 客户端等待 5 秒以留出传输余量；query 服务端 30 秒且受源配置进一步限制，客户端 35 秒。所有调用受父请求期限和撤销约束。query 后、行批次间隔≥1秒以及 complete 前串行 check；单次分析最多一个在途检查，完成不遗留后台检查。check 验证最新主体、租户、精确数据权限、配置启用、版本相等；依赖失败关闭结果交付。原直接 BFF 查询响应仍为三字段，不携带内部 guard。

## 本机配置与独立发布

无需 Docker。依赖已固定，无新增第三方包。独立后端：

```powershell
cd apps/chatbi/backend
uv sync --locked --offline --dev
$env:ASSEMBLEAI_ENVIRONMENT = 'Development'
$env:ASSEMBLEAI_VALIDATION_URL = 'http://127.0.0.1:5080/internal/calls/validate'
$env:ASSEMBLEAI_MODEL_GATEWAY_ORIGIN = 'http://127.0.0.1:5080'
$env:ASSEMBLEAI_CONNECTOR_GATEWAY_ORIGIN = 'http://127.0.0.1:5080'
$env:ASSEMBLEAI_MODEL_ID = 'assistant'
.venv/Scripts/python.exe -m uvicorn chatbi_backend.main:app --host 127.0.0.1 --port 8092 --no-access-log
```

离线命令需要已缓存的锁定依赖；普通联网环境可移除 `--offline`。生产默认要求 HTTPS 固定 origin，禁止用户控制回调地址。Python 只配置平台地址和模型别名，不提供源连接串或模型密钥。`GET /health/ready` 返回服务 / 版本 / protocolVersion=1，表示配置与进程就绪；不等于业务权限和外部服务可用。

前端在仓库根执行 `npm run build --workspace @assembleai/chatbi`，按[子应用发布契约](application-template.md)计算目录摘要并声明受控前端发布物。部署配置增加后端 binding `Service=chatbi, Version=0.1.0, Audience=assembleai:chatbi, Destination=http://127.0.0.1:8092`（生产替换受信任目标），并声明两条 Gateway Operations：

| Service / Version | Method / Path | Permission | ModelIds | ConnectorDatasetIds |
| --- | --- | --- | --- | --- |
| chatbi / 0.1.0 | GET /datasets | chatbi:use | 空 | daily-production |
| chatbi / 0.1.0 | POST /ask | chatbi:use | assistant | daily-production |

此表为部署白名单，字段名称与 `Gateway:Operations` 配置一致。注册 Manifest 的 frontendArtifact 摘要必须与真实目录匹配。模型、数据源均沿用既有显式部署配置、租户启用与资源授权；不随前端安装而默认开放。无数据集 / 无权限时页面显示可操作的空态。

## 验收基准

真实隔离 PostgreSQL 中当前工厂 A 两行产量 100 / 200，工厂 B 900，异租户复用工厂 UUID 的行 9999。源权限含 A/B，数据集与工具仅 A。固定题集：

| 自然语言问题 | 必须匹配的结果 |
| --- | --- |
| 2026年11月1日至2日的产量合计是多少？ | 300 |
| 2026年11月2日的产量是多少？ | 200 |
| 按产线汇总产量 | fixture-line-a，300 |

`tools/verify.ps1 -Scope All` 覆盖编译、Node / Python / .NET、界面与协议回归。`tools/verify-postgres.py --chatbi-browser` 运行真实 OIDC / BFF / YARP / Python / PostgreSQL / Chromium，使用显式本地模型协议夹具验证固定计划、非法计划、撤权、断连、委托重放与来源审计；浏览器无 API 拦截，独立和嵌入各一次真实问答。`--chatbi-real-model` 在前述验收后按现有 `ASSEMBLEAI_MODEL_ACCEPTANCE_CONFIG` 再调用真实供应商三次，只有上述合成问题；密钥从本机环境变量读取。准确率结论仅覆盖题集，不推定真实制造业复杂问题的准确率。

回调能力目前属于签发实例，部署须使回调返回该实例；多实例路由 / 配额与生产拓扑归 T14，管理界面和运行审计治理归 T13。

定位 ChatBI 联调失败时，可在上述数据库命令追加 `--chatbi-only`：仍验证隔离实例、迁移与重放，但只执行 ChatBI 链，结果明确标注范围，不能替代全平台回归。失败诊断只输出终态码、计划结构统计和连接器 HTTP 状态 / 时长，不记录问题、计划内容、结果行或凭据。
