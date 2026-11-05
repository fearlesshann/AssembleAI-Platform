# T13 统一调用与审计

正式门户的`/manage/audit`汇总当前业务租户中有权查看的调用、操作和异常记录。`/manage/audit/global`只查询全局身份与应用注册操作，不聚合其他租户。两个范围均支持`/{source}/{id}`详情深链，查询条件保留在URL中。

界面使用[用户示例设计合同](reference-design-contract.md)的共享字体、颜色、外壳、输入、筛选、表格和状态。原示例只提供设计基准；记录来自真实API。任务、运行与依赖诊断仍属T13下一批。

## 记录与权限

先执行统一迁移1–13并配置OIDC/BFF。迁移13增加历史查询索引及`platform_audit_exports`，不创建默认角色或授予权限。使用角色权限页面显式分配以下精确资源授权；`tenantWide`仍受当前租户及有效成员关系约束。

| 来源 | 必要权限 |
| --- | --- |
| `gateway` 应用网关调用 | `gateway:observe`，`application/{appId}`，tenantWide |
| `model` / `model-configuration` 模型调用 / 配置 | 原有`model:manage`，`model/{modelId}`，tenantWide |
| `connector` / `connector-configuration` 数据调用 / 配置 | 原有`datasource:manage`，`datasource/{sourceId}`，tenantWide |
| `knowledge-search` / `knowledge-document` 知识检索 / 文档操作 | `audit:read`，`audit/knowledge`，tenantWide；只暴露安全元数据，不赋予正文访问权 |
| `registry` 应用操作 | 租户范围使用原有`registry:manage`精确应用授权；全局developer限本人应用，reviewer按原全局审核范围 |
| `identity` 身份操作 | 原有显式平台 / 租户身份管理权；全局范围仅平台身份管理员 |
| `audit-export` 导出操作 | `audit:export`，`audit/events`，tenantWide |

身份管理员不隐式获得模型、数据源、网关、知识审计或导出权限。导出权限只允许发起导出，每条记录仍受上表读取权限过滤。全局范围不提供导出；普通成员无来源权限时返回空来源集合与空列表，不自动升级角色。

## API 与查询边界

统一前缀`/api/platform/v1/audit`：

| 方法与路径 | 合同 |
| --- | --- |
| `GET /workspace?scope=tenant\|global` | 当前主体、范围、租户、可读来源、导出能力、全局入口及查询政策 |
| `GET /events` | `{items,nextCursor}`；按时间、来源、ID稳定倒序 |
| `GET /events/{source}/{id}` | 当前授权与筛选内的单条安全记录；隐藏记录与不存在记录均404 |
| `POST /export` | CSRF与Origin检查，正文严格`{}`；返回`{items,truncated,exportedAt}` |

列表和详情必须传`from` / `to`，为以Z结尾的UTC ISO时间，开始包含、结束不包含，最长31天。可选`source`、`status=success|failure|unfinished`、`appId`、`requestId`；`requestId`匹配记录请求号或其可信网关关联号。页面默认最近24小时，输入按本地时间显示，发送时转换UTC。

默认每页50条，最大100条。游标受Data Protection保护、15分钟有效，绑定主体、范围、租户、完整筛选、页大小与当前授权状态。对未变化的记录集合，相同时间通过来源与ID继续定位，防止漏页或重复；更换筛选、主体或权限后旧游标拒绝。详情和关联查询不扩大原资源授权。页面关联动作清除来源 / 结果 / 应用筛选，在原时间窗与范围内查同一请求链。

DTO包含时间、主体、租户、资源、动作、原始安全结果分类、统一状态、requestId及可用的app / release / run / gatewayRequestId。details仅固定允许的安全字段，精确整数以字符串返回。不会选择网关path/query、模型提示与回答、查询值、文件正文 / 文件名、凭据或目标地址。

`success`是各来源完成元数据的统一分类；网关`transport_completed`仅代表传输完成，不证明业务成功或浏览器收到结果。`running`归为`unfinished`，保留排查价值，不伪造完成。查询有SQL与总请求时限，返回受界限约束；服务错误只返回安全状态，不透出数据库异常。

## 导出与持久化

导出不带cursor或pageSize，从筛选范围最前端最多取1000条；存在更多时显式`truncated=true`，页面要求缩小范围。下载JSON保留完整envelope，不能把截断结果标为全量。浏览器仅在收到完整合法响应且租户仍匹配时创建文件，不自动重试。

服务端读取结果后，在事务内复核权限与实时BFF会话，持久化主体、租户、条数、截断状态及时间，再次复核并提交；提交后及响应出口仍复核。审计故障503且不交付数据；等待期间退出、切租户或撤权使事务回滚。提交后的最后复核若失败也不交付数据，但审计可能已存在。因此导出审计表示服务端处理了导出请求，不证明文件已送达；超时、取消、403或失去登录后没有文件，也不证明没有审计。页面的403和不确定失败明确说明可能已记录，不自动重放。

本批不自动清理历史，workspace明确`automaticRetention:false`。31天是单次查询窗口，不是留存时长；不宣称具备防篡改归档、所有入口拒绝事件的统一采集、生产留存SLA或大规模离线导出。D9生产留存、容量、备份恢复及多实例交付继续按T14 / T15验收。

## 验证

- `tools/verify.ps1 -Scope All`：API边界、静态深链、页面状态、示例实际样式对照、类型检查、构建及既有用例回归。
- `tools/verify-postgres.py --audit-only`：隔离真实PostgreSQL与OIDC协议/BFF，验证多来源查询、权限、分页、导出、故障及锁等待竞态。
- 加`--audit-browser`使用正式门户直接访问上述真实API；不拦截浏览器业务请求。`--audit-browser`不带only时同时执行完整控制面实库回归。

专项不能替代完整回归，也不代表真实Keycloak产品、实际模型效果或生产部署已重新验收。运行参数与清理见[原生PostgreSQL](../native-postgres.md)，执行证据见[开发记录](../development.md)。
