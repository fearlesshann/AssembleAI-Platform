# T13 模型与数据源管理

本批将既有模型网关、只读连接器接入正式门户。完整接口分别见[模型网关](model-gateway.md)与[数据连接器](connectors.md)，任务范围与验证见[实施计划](../superpowers/plans/2026-11-05-t13-resource-management.md)。统一审计筛选、分页与导出已由[T13第三批](audit-console.md)接入；任务诊断继续后续。

## 页面与上下文

| 路径 | 操作 |
| --- | --- |
| `/manage/models`、`/manage/models/{modelId}` | 搜索授权模型、查看启用状态、配置租户输出上限、主动验证调用、查看近期调用 |
| `/manage/data-sources`、`/manage/data-sources/{sourceId}` | 搜索授权数据源、查看受控数据集与字段、配置租户行数 / 超时、主动测试连接、查看近期调用 |

资源标识遵守既有`[a-z][a-z0-9._-]{0,63}`。页面作用于BFF会话当前选择的业务租户，不继承身份管理页面的显式目标租户。导航依据该租户的实际可见资源显示；没有资源权限时不显示模块链接，直接访问深链仍有无权、空列表或模块不可用状态。一个资源模块读取失败不阻断其他模块导航。

供应商端点、上游模型名、密钥及环境变量引用，数据源地址、物理表、物理列和连接串均不由浏览器配置或回读。页面展示部署上限与别名；租户配置可以收紧到部署上限内。部署关闭时，租户启用也不会使资源可调用。

列表合同以增量字段表达页面能力，保持原有字段：

| 列表 | 新增字段 |
| --- | --- |
| 模型 | `canInvoke`、`deploymentEnabled`、`deploymentMaximumOutputTokens`、`callTimeoutSeconds`；既有`maximumOutputTokens`仍为有效上限 |
| 数据源 | `canTest`、`deploymentEnabled`、`maximumRows`、`timeoutSeconds`、`deploymentMaximumRows`、`deploymentTimeoutSeconds`；前两个额度字段为部署与租户配置的较小值 |

这些能力字段仅用于解释页面；每次服务端请求仍实时授权。模型未配置时GET返回明确JSON `null`，已配置返回原裸对象；数据源仍为`{configuration:null}`或配置对象包装。浏览器只接受安全整数修订号，后端写入也保留递增后仍可精确表示的范围。

## 授权与初始化

先完成统一迁移1–12并配置OIDC/BFF，按[身份管理合同](identity-management.md)显式建立管理员、租户成员与角色。部署者配置`Models` / `Connectors`受控目录及服务进程环境变量；两个模块默认关闭。没有新增数据库迁移，也没有自动创建模型、数据源或授予业务权限。

| 能力 | 必要授权 |
| --- | --- |
| 模型配置与调用记录 | `model:manage`，资源`model/{modelId}`，tenantWide |
| 模型验证调用 | `model:invoke`，同一精确模型资源，tenantWide |
| 数据源配置与调用记录 | `datasource:manage`，资源`datasource/{sourceId}`，tenantWide |
| 连接测试 | `datasource:test`，精确数据源；以及`tool:invoke`，资源`tool/connector.test`，均tenantWide |
| 已授权数据集与字段查看 | 数据源管理员可看该源受控目录；普通查询用户需当前有效组织上的`datasource:read`、`dataset:read`、`tool:invoke` (`connector.query`)交集 |

仅管理权不会取得模型调用或连接测试权；仅调用 / 数据读取权不会取得配置和其他用户调用记录。身份平台管理员、租户管理员及原有应用开发 / 审核角色也不自动具备上述权限。权限可通过角色权限管理页面显式配置，再分配给租户成员。

## 保存与测试

首次配置使用`expectedRevision:0`；之后使用最近读取的修订号。配置与相应审计在同一事务提交。写事务取得身份与资源配置表锁、目标行及外键身份行锁后，重新检查原授权版本、精确管理权和当前会话；审计写后、提交前再检查一次。等待期间发生撤权、退出或换租户则拒绝，并回滚配置、修订及审计。配置变更会使依赖旧修订号的在途调用失效。

修订冲突保留草稿并阻止直接重试，用户重新读取后再编辑；明确成功但确认读取失败时显示已提交，不声称保存失败。浏览器超时或网络断开不能推定服务端未提交，不自动重放。离开未保存表单需要确认；切换身份 / 租户、退出、组件卸载时取消请求并丢弃迟到响应。模型验证文本与结果仅保存在当前页面内存。

模型验证由用户主动提交，执行一次非流式文本调用，受输出上限和调用预算约束，可能产生模型用量。连接测试由用户主动触发，验证只读连接及部署数据集条件。测试结果、错误和调用记录都使用安全投影，不显示供应商错误原文、SQL、凭据或地址；提供取消测试操作，取消后先重新读取调用状态再尝试；不自动重试。

近期调用记录仅覆盖当前租户、当前资源的最多50条，并明确此范围。记录包含时间、结果、耗时、请求编号及可得用量 / 返回行数；应用发起的调用保留应用 / 版本 / 运行关联。审计不存模型提示与回答、查询值或数据结果。`completed`表示执行完成，不保证浏览器收到响应；`running`表示尚无完成记录，不能当成成功。配置操作审计仍持久化在既有表，统一浏览和筛选见[审计控制台](audit-console.md)。

## 验证入口

- `tools/verify.ps1 -Scope All`：有界API、静态深链、页面状态、类型检查、构建及既有用例回归。
- `tools/verify-postgres.py --resource-only --resource-browser`：隔离临时PostgreSQL、真实OIDC协议/BFF、生产门户与本地模型协议服务、真实只读数据连接；浏览器不拦截业务API。
- `tools/verify-postgres.py --resource-browser`：完整基础实库回归加本批浏览器验收。

原生运行器需既有Node、.NET、PostgreSQL与Playwright Chromium；参数及清理约束见[原生PostgreSQL](../native-postgres.md)。不安装Docker，不读取实际供应商密钥，不将协议夹具的响应视作真实模型效果验收。最终执行结果记录在[开发记录](../development.md)。
