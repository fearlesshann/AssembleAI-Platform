# T12 文档摄取基础

首期包含 PDF / Word 的文档上传、租户 / 组织权限、原文下载、删除、持久任务和文本提取；Word 指 `.docx`，同时支持 UTF-8 TXT / Markdown，单文件最多 5 MiB。本页描述摄取层；RabbitMQ、embedding / pgvector、独立问答与引用界面见[索引与问答契约](knowledge-qa.md)。`extracted` 仅表示文字提取成功，不能用于问答。

## 组件与启动

ASP.NET Core 控制面持有授权、元数据、任务与审计；Minio 官方 SDK 7.0.0 访问部署者预建的私有 bucket；独立 FastAPI 服务在受限子进程中提取文本。PostgreSQL 为任务事实源，30 秒任务预算、60 秒租约，失败重试间隔 30 秒，最多 3 次连续失败；重启后可重新领取到期任务。BrokerEnabled=false 时每 2 秒轮询执行；开启后由 RabbitMQ 持久通知驱动，任务状态和授权仍从数据库读取。

先应用迁移 1–11。启动解析服务的方法见 [独立解析服务](../../apps/knowledge/backend/README.md)，其地址示例为 `http://127.0.0.1:8093/internal/parse`。仅启用文本摄取的配置示例：

```json
{
  "Knowledge": {
    "Enabled": true,
    "WorkerEnabled": true,
    "ParserEndpoint": "http://127.0.0.1:8093/internal/parse",
    "ParserKeyEnvironmentVariable": "ASSEMBLEAI_KNOWLEDGE_PARSER_KEY",
    "AllowInsecureLoopback": true,
    "Objects": {
      "Endpoint": "http://127.0.0.1:9000",
      "Bucket": "knowledge-documents",
      "AccessKeyEnvironmentVariable": "ASSEMBLEAI_KNOWLEDGE_ACCESS_KEY",
      "SecretKeyEnvironmentVariable": "ASSEMBLEAI_KNOWLEDGE_SECRET_KEY",
      "AllowInsecureLoopback": true
    }
  }
}
```

默认 `Enabled` / `WorkerEnabled` 均 false。环境变量由进程启动环境提供，配置只保存变量名称；解析服务与平台使用同一解析密钥（16–256 个无空白 ASCII 字符）。非 loopback 目标必须使用 HTTPS。MinIO bucket 应保持私有，关闭版本管理和对象锁，并设置未完成 multipart 上传的过期清理。SDK 在恰好 5 MiB 时使用 multipart；中断上传可能留下临时分片。平台不创建 bucket 或公开下载地址，也不把存储凭据传给浏览器。

## 文档接口和权限

前缀 `/api/platform/v1/tenants/{tenantId}/knowledge/documents`。需要真实 BFF 登录，路由租户必须等于当前选中的租户；写操作要求 Origin 与 CSRF。集合权限资源为 `knowledge / documents`，权限名为 `knowledge:manage` 和 `knowledge:read`；单文档也可使用 `document / <documentId的32位小写N格式>` 精确资源授权。组织范围与原有 RBAC / ABAC 规则一致；无组织的文档必须有 tenant-wide 授权。上传者不能自行授予额外访问权限，文档组织范围创建后不可变。

| 方法 | 行为 |
| --- | --- |
| POST `?filename=文件名&organizationId=可选组织UUID` | `application/octet-stream` 原始字节，返回 202 和服务端生成的文档信息；有 manage 权限才可上传 |
| GET 集合 | 返回最近 100 条非删除文档中当前可访问的条目；并非完整分页目录 |
| GET `/{documentId}` | 读取当前授权元数据，含 status、revision、failureCode |
| GET `/{documentId}/content` | 再次验证权限、文档修订与 SHA256，按附件返回原文 |
| DELETE `/{documentId}` | JSON `{"expectedRevision":1}`，成功 204，冲突 409；需 manage 权限 |

文件名只用于显示和格式选择，存储路径固定由租户 / 文档 UUID 生成。拒绝未知参数、重复参数、路径分隔符、控制字符、其他扩展名、压缩正文及空文件。浏览器不能提供对象路径或上游地址。

下载先完整读取不超过 5 MiB 的对象，再复核文档是否被删除、权限与会话，并检查大小和摘要；不支持外部预签名 URL、范围请求或公开缓存。复核覆盖开始发送前，已经发送的字节无法撤回，当前附件发送阶段不提供持续撤权监控。Markdown 以附件和 octet-stream 下载，不在平台域执行 HTML。

## 状态、失败与删除

IndexEnabled=false 时状态为 `queued → processing → extracted`；开启索引后为 `queued → processing → indexing → ready`。失败为 `failed`；删除为不可恢复的 `deleted` 墓碑。只有当前 revision/profile 的完整索引到达 ready 才可检索。既有 extracted 文档不会自动重建索引，需在启用索引后重新上传。解析服务支持真实 PDF 文本层与 DOCX 正文（含表格段落）；不执行链接、嵌入对象或宏。扫描 PDF 返回 `ocr_required`，加密 PDF 返回 `encrypted_document`，旧 `.doc` 不支持。提取上限、定位符与隔离机制见解析服务说明。

`failureCode` 只保存允许列表中的安全原因：`ocr_required`、`encrypted_document`、`unsupported_document`、`empty_document`、`extracted_text_too_large`、`document_too_complex`、`invalid_document`、`parser_timeout`、`parser_unavailable`、`ingestion_failed`。永久文档错误直接失败；超时、依赖故障或通用异常按上限重试，成功清除原因，不保存原始异常或库诊断。

上传登记与摄取任务 / 审计原子提交；任务领取有唯一租约，完成时同时检查租约、文档修订和当前身份。worker 在读取对象前和写入结果前复核上传者权限。删除在同一事务中写墓碑、清除文本分块与向量、取消摄取及索引任务、登记清理任务和审计；陈旧 worker 无法把分块写回。对象清理成功后才确认 purge，清理不要求原上传者继续有权限。三次清理失败需运维处理，墓碑持续阻止平台读取。

对象上传先于数据库登记；若数据库提交结果不确定，平台保留私有对象，避免误删可能已登记的文件。这可能留下未关联对象，需要后续运维对账；不能直接按一次请求失败删除对象。审计只保存主体、租户、动作、资源和请求编号，不保存原文、提取内容或密钥。提取后的分块属于受保护业务数据，存于 PostgreSQL。

## 验证边界

`tools/verify.ps1 -Scope Core` 包含真实 OIDC/BFF 的文档 HTTP 回归、受控 S3 HTTP 协议下的 Minio SDK、worker 测试和实际生成 PDF / DOCX 的解析测试。`tools/verify-postgres.py` 使用隔离真实 PostgreSQL，验证迁移重放、重复领取、过期租约、失败分类、撤权、删除竞争及审计原子性。

后续真实 MinIO / RabbitMQ / pgvector 与浏览器联合验证使用[原生服务工具](../native-knowledge.md)，入口为 `tools/verify-postgres.py --knowledge-browser`；加 `--knowledge-real-model` 的独立运行还验证已配置供应商。各次实际结果和限制见[开发记录](../development.md)，不能以单元测试或文本提取成功替代整链路验收。
