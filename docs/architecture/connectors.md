# T10 只读数据连接器

首期支持 PostgreSQL 受控数据集。部署者固定目标、表与字段；租户配置只能启停和收紧行数 / 时限。调用方提交结构化查询，平台负责生成参数化 SQL，并在聚合前下推租户与工厂 UUID 条件。用户已确认先使用明确标注的制造业测试数据；实际 ChatBI 前后端与模型问答链在 T11 交付。

## 部署配置

执行控制面迁移 1–8。`Connectors:Enabled` 默认 false，没有默认数据源或业务授权。连接字符串放在专用服务端环境变量；该变量名由受控配置给出，不接受浏览器设置或返回凭据。配置示例：

```json
{
  "Connectors": {
    "Enabled": true,
    "Sources": {
      "manufacturing": {
        "DisplayName": "制造业测试数据",
        "ConnectionEnvironmentVariable": "ASSEMBLEAI_MANUFACTURING_READONLY",
        "Enabled": true,
        "MaximumRows": 500,
        "TimeoutSeconds": 10
      }
    },
    "Datasets": {
      "production": {
        "SourceId": "manufacturing",
        "DisplayName": "每日生产记录（测试）",
        "Schema": "manufacturing",
        "Table": "production_daily",
        "TenantColumn": "tenant_id",
        "OrganizationColumn": "factory_id",
        "Columns": {
          "line": { "Column": "line_name", "Type": "text" },
          "day": { "Column": "production_date", "Type": "date" },
          "produced": { "Column": "produced_count", "Type": "integer" },
          "defects": { "Column": "defect_count", "Type": "integer" }
        }
      }
    }
  }
}
```

源与数据集各最多 100，数据集最多 32 个字段；物理 schema / table / column 为严格标识符，不能插入 SQL。业务字段类型限 text、integer、decimal、date；tenant_id / factory_id 必须对应可信映射的 UUID。组织为平台现有平面 UUID，不隐含部门或工厂树继承。

源账号须专用只读角色，对声明对象仅有 SELECT；禁止 superuser、createdb、createrole、bypassrls 以及受控表写权限。配置测试和查询均检查权限，并在只读事务中执行，设置执行 / 锁等待超时。不要使用平台元数据库写账号作为数据源账号；业务源的网络、TLS 与账号由部署者管理。

## 权限

| 操作 | 精确资源权限 |
| --- | --- |
| 配置 / 调用历史 | datasource:manage → datasource/{sourceId}，tenantWide |
| 测试连接 | datasource:test → datasource/{sourceId}，并且 tool:invoke → tool/connector.test，均 tenantWide |
| 查询 | datasource:read → datasource/{sourceId}、dataset:read → dataset/{datasetId}、tool:invoke → tool/connector.query |

查询对每个当前启用的组织分别求三种权限交集，空集拒绝。每条 grant 的 permission / resource / organization 保持完整，不从其他资源拼范围。tenantWide 工具权限只表示工具可用于当前数据权限范围，不授予其他工厂数据。租户、主体来自实际 BFF 会话，工厂 Header 或查询值不能取代授权。

## HTTP 契约

统一前缀 `/api/platform/v1/tenants/{tenantId}/connectors`，必须有活跃 BFF 会话并选中该租户。所有写入、测试与查询要求固定 Origin 和 CSRF，响应 no-store。请求不允许 query string、压缩正文、重复 / 未知 JSON 字段或大于 16 KiB 的正文。

| 方法 / 路径 | 含义 |
| --- | --- |
| GET 空路径 | 当前授权源及可见数据集 / 字段别名；不返回地址、物理表名或凭据引用 |
| GET /{sourceId}/configuration | `{configuration}`，未设置时 null |
| PUT /{sourceId}/configuration | `{enabled,maxRows,timeoutSeconds,expectedRevision}`，返回 `{configuration}` |
| GET /{sourceId}/calls | `{items}`，该租户 / 源最近 50 条元数据审计 |
| POST /{sourceId}/test | 正文 `{}`，成功 `{requestId,status:"ready"}` |
| POST /{sourceId}/datasets/{datasetId}/query | 下述查询，成功 `{requestId,columns,rows}` |

首次配置 expectedRevision=0，之后精确 CAS；冲突 409，不自动重试。maxRows 1–500、timeoutSeconds 1–30 且不能超过部署上限。部署或租户禁用、缺授权、无组织交集均拒绝。源配置和权限在准入、查询中每 500ms、结果交付前重新读取；完成审计后仍复核会话 / 授权。依赖故障拒绝交付结果，执行审计 completed 表示查询完成，不保证客户端收到结果。

查询示例：授权工厂内按产线汇总每日产量与不良数。

```json
{
  "columns": ["line"],
  "filters": [
    { "column": "day", "operator": "gte", "value": "2026-11-01" },
    { "column": "day", "operator": "lte", "value": "2026-11-02" }
  ],
  "metrics": [
    { "function": "sum", "column": "produced" },
    { "function": "sum", "column": "defects" }
  ],
  "limit": 100
}
```

无 metrics 为明细投影；有 metrics 时 columns 为分组列，指标输出依次为 metric0、metric1。允许 count（无 column）和数值字段 sum / avg / min / max；筛选仅 AND eq / gte / lte，字段必须在白名单内，值必须符合字段类型，均作为驱动参数。最多 16 列、8 指标、16 筛选。没有 raw SQL、表达式、函数名扩展、排序 SQL、表名或连接字符串输入。decimal 筛选值必须能被 .NET decimal 精确表示，不接受舍入 / 下溢；源 numeric 超精度或 NaN / Infinity 明确失败。date 只接受 0001-01-01 至 9999-12-31 的有限日期，数据库 infinity / 超范围值失败，有限极值不会转换成 infinity。

最多返回请求 limit 行；发现第 limit+1 行时以 413 失败，不静默截断。单字符串最大 4 KiB，总 JSON 结果最大 1 MiB。查询有整体截止时间、statement_timeout、lock_timeout 和客户端断连取消；单实例每用户并发 2、全局 32、每分钟 60 次，无自动重试。多实例配额归 T14。

迁移 8 保存 tenant_connectors、配置审计与执行审计。审计只记录用户 / 租户 / 源 / 数据集 / 操作 / 请求编号 / 状态 / 行数 / 时长，不保存筛选值、SQL、数据或密钥。查询审计开始失败不访问源，完成审计失败不交付数据。进程崩溃留下的 running 记录由 T13 的审计对账进一步处理。

## 验证

`--connector-tests` 覆盖范围交集、查询编译与 HTTP 故障边界；`tools/verify-postgres.py` 使用隔离数据库、独立只读角色、制造业测试表和真实 OIDC/BFF 请求，验证跨工厂 / 租户、聚合、写权限拒绝、查询限制、撤权 / 超时及持久化审计。正式迁移不预置测试表、角色、数据或权限；测试夹具只在已标记的临时数据库内创建并清理。最终结果见 [开发记录](../development.md)。

确定性测试数据与基准（UUID 每轮随机）：

| 租户 / 工厂 | 产线 | 日期 | 产量 | 不良数 |
| --- | --- | --- | --- | --- |
| 当前租户 / A | fixture-line-a | 2026-11-01 | 100 | 2 |
| 当前租户 / A | fixture-line-a | 2026-11-02 | 200 | 3 |
| 当前租户 / B | fixture-line-b | 2026-11-01 | 900 | 90 |
| 其他租户 / 同名 UUID 行 | fixture-foreign | 2026-11-01 | 9999 | 999 |

源权限含 A/B，数据集和工具仅 A，因此实际两行、产量合计 300、不良数均值 2.5、产量最小 100 / 最大 200。日期筛选 2026-11-02 只返回产量 200。异租户行刻意复用工厂 UUID，证明租户条件独立强制执行；工具范围改为 B 时交集为空，403 且不启动源查询。这是连接器的测试基准，真实制造业业务数据、自然语言题集与答案效果仍由 T11 落实。
