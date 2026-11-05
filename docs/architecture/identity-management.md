# 平台身份与授权管理

T13 第一批；实施选择，依据 [SPEC C8](../../SPEC.md#c8-平台功能与页面交付清单)。沿用现有 BFF、PostgreSQL 身份表及实时授权判断，不新增身份提供方或业务用例。

## 权限边界

- `platform_identity_administrators` 显式记录平台身份管理员。只有启用的用户及有效记录取得此管理权；既有 developer / reviewer 和业务角色均不自动取得管理权。
- 平台身份管理员管理用户、租户及其成员、组织、角色。全局控制面允许管理未选中或已停用租户，不要求管理员成为每个租户的业务成员；这不赋予应用、模型、连接器、文档的业务访问权。
- `tenant_identity_administrators` 显式记录租户身份管理员。仅平台身份管理员能授予或撤销。租户管理员须有当前启用的用户、租户、成员关系及匹配的 BFF 选中租户，管理范围限该租户。
- 租户管理员可管理成员、平面组织和业务角色；不能枚举平台用户目录、编辑用户身份、启停租户或变更管理员标记。新增成员需提供已存在的精确平台用户 UUID；用户密码、MFA 等归 OIDC 身份中心。
- 业务角色授予精确 permission / resourceType / resourceId；无通配符、无组织继承。范围为显式 tenantWide 或非空同租户启用组织集合，不能同时设置。此管理能力是租户内的授权管理权，不从操作者现有业务访问权限推导；业务授权不能创建平台或租户身份管理员。
- 用户 `(issuer, subject)` 创建后不可修改，避免重新绑定已授权主体；显示名称与启用状态可编辑。阻止移除或停用最后一个有效平台身份管理员。
- 初始化由部署操作者执行显式引导脚本；迁移不生成默认账号、口令或管理员。部署恢复操作与浏览器管理分离。

## 初始化

部署初始化先应用迁移，再由有数据库操作权限的部署人员通过 [初始化入口](../../tools/bootstrap-identity-admin.mjs)执行 [bootstrap-identity-admin.sql](../../deploy/postgres/bootstrap-identity-admin.sql)。从可信身份中心获取精确 issuer / subject；`user-id` 是新生成的固定平台 UUID，重跑时沿用同一个值。凭据使用 PostgreSQL 密码文件或 `PGPASSWORD`。以下占位值须替换后执行，`--psql` 可指定本机可执行文件：

```powershell
node tools/bootstrap-identity-admin.mjs --psql .local-data/native-postgres/postgresql/pgsql/bin/psql.exe --host 127.0.0.1 --username postgres --user-id 00000000-0000-4000-8000-000000000001 --issuer https://identity.example/realms/manufacturing --subject subject-from-identity-provider --display-name "平台管理员"
```

脚本只建立首个管理员，不创建 OIDC 账号、密码或业务成员关系。对同一已启用管理员精确重跑不写入；存在其他有效管理员时拒绝，改从正式管理页添加。已有用户绑定必须一致，禁用用户不会被脚本自动恢复。成功引导与审计同事务；后续登录即使用既有 OIDC / BFF 流程。

Windows 原生 psql 的命令行参数编码可能与 UTF-8 连接不一致；不要直接通过 psql `--set` 传中文名称。正式入口将元数据编码为仅 ASCII 的 SQL 数据并通过标准输入传入，由 PostgreSQL 解码并设置 psql 变量；不拼接未转义的 SQL 或 shell，口令不进入命令行和输出。

## HTTP 接口

统一前缀 `/api/platform/v1/identity-management`。全部接口需真实当前 BFF 会话，`Cache-Control: no-store`；查询参数不受支持。写入额外校验固定 Origin 与 CSRF，严格 JSON 字段、类型、重复键与正文预算。会话展示信息不用于授权。

| 方法 / 路径 | 结果或正文 |
| --- | --- |
| GET `/workspace` | `{actorUserId, platformAdministrator, tenants, users, audit}`；非全局管理员 users / audit.items 为空，tenants 仅包含其可管理租户；全局 audit 展示无租户的用户操作 |
| GET `/tenants/{tenantId}` | `{actorUserId, platformAdministrator, tenant, members, organizations, roles, audit}` |
| GET `/tenants/{tenantId}/permission-catalog` | `{tenantId, resources}`；具名资源、业务能力、组织范围支持与精确配套权限；同租户身份管理权 |
| PUT `/users/{userId}` | `{expectedRevision, issuer, subject, displayName, enabled, platformAdministrator}`；全局管理 |
| PUT `/tenants/{tenantId}` | `{expectedRevision, name, enabled}`；全局管理 |
| PUT `/tenants/{tenantId}/members/{userId}` | `{expectedRevision, enabled, administrator, roleIds}` |
| PUT `/tenants/{tenantId}/organizations/{organizationId}` | `{expectedRevision, name, kind, enabled}` |
| PUT `/tenants/{tenantId}/roles/{roleId}` | `{expectedRevision, name, enabled, grants}`；全量替换该角色的授权集合 |

实体形状：

```text
user         = userId, issuer, subject, displayName, enabled, platformAdministrator, revision
tenant       = tenantId, name, enabled, revision
member       = userId, displayName, enabled, userEnabled, administrator, roleIds, revision
organization = organizationId, name, kind, enabled, revision
role         = roleId, name, enabled, revision, grants
grant        = permission, resourceType, resourceId, tenantWide, organizationIds
audit        = items, hasMore
```

新建 `expectedRevision=0`；编辑使用读取的修订号。成功返回 `{revision}`，页面重新读取权威快照。冲突返回 409，页面保留输入但要求重新加载后再编辑，不能自动覆盖。400 非法请求，403 无授权，404 已授权范围内目标不存在，503 存储或预算不可用；响应不泄漏数据库异常、凭据或 SQL。

每类集合最多 500 项、每角色最多 100 条授权，超限明确失败，不静默截断；本批采用有界快照和本地搜索。集合达到 500 时，新增在事务内返回 409 `identity_capacity_reached`，已有记录仍可编辑，停用不释放容量；不能通过正式写操作让管理快照超过容量而失效。审计显示最近 50 条及 `hasMore`，统一筛选 / 分页 / 导出属于 T13 后续审计模块，不宣称已经交付。

浏览器完整响应限制 4 MiB。服务端同时维护保守的聚合响应预算：文本按 UTF-8 字节的六倍覆盖 JSON 转义，每个实体预留 512 字节固定字段、每个 UUID 数组元素 40 字节，审计预留 512 KiB；租户快照为每个成员显示名预留最大 6144 字节，避免全局用户改名扩大其他租户响应。所有新增与编辑在事务内写后、审计与提交前校验，超限返回同一容量错误并回滚。初始化遵循相同全局预算。预算可能早于 500 项触发，500 是数量上限，不承诺任意长资料和授权组合均可容纳 500 项；外部 SQL 导入超限状态明确拒绝读取，不自动删减数据。

## 一致性与审计

管理事务按一致顺序获取涉及身份表的锁，取得锁后再次复核实时 BFF 会话，在同一事务内读取操作者权限、检查对象修订号并提交写入和成功审计。锁等待期间的退出或租户切换须拒绝本次写入。既有授权版本触发器继续使业务访问读取最新状态；管理修订号独立服务于并发编辑。

成功审计与变更原子提交，审计写失败时整个变更失败；拒绝与冲突保持安全错误。审计仅记录操作者、目标类型 / ID、租户、动作、结果、时间与 requestId，不复制表单正文、Token 或用户问题。此批不自动清理审计、不提供导出；统一留存、导出及调用可用性指标仍由 D9 / 后续任务收敛。

## 页面与交互

- `/manage/users[/userId]`、`/manage/tenants[/tenantId]`：平台身份管理员的全局入口。
- `/manage/members[/userId]`、`/manage/organizations[/organizationId]`、`/manage/roles[/roleId]`：租户管理入口；全局管理员可由租户详情进入指定管理上下文，不冒充该租户业务成员。
- 导航由后端可管理范围决定；提示性导航不代替服务端校验。身份管理不可用时，不影响现有应用中心与应用管理。
- 复用现有浅暗 token、紧凑列表及详情表单；列表可搜索，创建和编辑动作清晰，组织为平面集合。授权表单明确展示资源与组织范围，新增授权默认不扩大到全租户。
- 覆盖加载、空态、拒绝、失败、保存中、修订冲突、写入成功但回读失败、未保存离开、身份 / 租户变化及迟到响应。无自动写重试；保存后列表与详情以重新读取的结果为准。
- 验收包含键盘、窄屏、深链刷新、双主题和真实 OIDC / PostgreSQL 浏览器链路。

## 角色权限工作区（2026-11-05）

角色列表展示授权 / 资源数量和关联成员，详情进入独立工作区。编辑时从资源分类与搜索选择对象，一次勾选多项能力，再显式选整个租户或指定组织，加入右侧清单。相同资源的不同精确范围分开展示，不自动取并集。技术编码收进辅助信息，未知既有授权明确标注并完整保留；不要求管理员手填编码。

应用访问 / 发布 / 调用观察、模型调用 / 配置、数据源测试 / 管理、连接测试工具和审计为租户级能力；数据源读取、数据集查询、查询工具、文档阅读 / 管理可按组织分配。目录仅描述能力，不改变服务端实时授权规则。既有不适用的组织范围明确提示，必须由管理员决定是否扩大或移除。

配套权限由目录描述，例如查询数据集同时需要对应数据源读取和 connector.query 工具。明确选择“补齐”时只添加缺失权限，沿用对应范围；已存在但范围不足的权限保留原样并继续提示。多个角色权限叠加，不将本角色缺项解释为用户一定无权。

保存前逐项展示新增、移除、范围调整与元数据变化，显示关联成员数，允许撤销单项。未加入清单的配置阻止保存和静默切换资源；离开沿用原确认框。保存协议、64KiB正文、100项权限、100组织 / 项、修订保护与原子审计保持。目录失败不丢授权，身份撤销清空管理数据。

目录采用身份管理员权限读取，而非管理员个人的模型 / 数据业务权限。应用只取已发布信息或目标租户已分配版本，最多500项；不公开其他租户未公开版本、草稿、前端部署路径、后端服务名、供应商地址、环境变量名或物理表。模型 / 连接器由已验证部署目录构建具名元数据，停用项标记状态。目录整体最多1000资源 / 2MiB，超限明确503；现有浏览器4MiB上限继续有效。
