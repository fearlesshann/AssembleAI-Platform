# T8 · 应用管理工作区

在门户侧栏打开“应用管理”，或直接访问 `/manage/applications`、`/manage/applications/{appId}`。管理页沿用真实 BFF 和同源请求，刷新可恢复页面位置；未保存输入只留在当前组件内存。它补齐 [T4 注册控制面](registry.md) 的交互，不引入另一套发布状态机。

## 操作流程

研发人员点击“注册应用”，填写名称、分类、应用标识、发布组合版本、前端版本 / 真实 SHA256、后端逻辑服务 / 版本和入口权限。前端入口自动派生为 `/apps/{appId}/{frontendVersion}/`，不能输入任意 URL。真实构建与摘要生成见 [T7 发布物部署](application-template.md)。保存只建立草稿；审核期、已发布和已停用版本不能改写发布物，内容变化需要新版本。

草稿可编辑、提交审核。审核员查看版本、前后端映射和提交人后发布或驳回；提交者不能发布自己提交的版本，即使同时拥有 reviewer。已发布版本可全局停用；恢复发布仍是原内容。确认对话框说明作用范围，读取数据刷新时关闭旧确认，避免确认已经过时的版本动作。

“当前租户”独立显示未配置 / 已启用 / 已停用 / 全局版本不可用。租户管理员选择已发布版本后启用或切换；选择仍已发布的旧版本即回滚。全局不可用时仍可停用已有租户配置，不要求下拉框能选择这个已禁用版本。

版本切换只改变新运行准入；已有运行固定原发布组合，按 [T5 排空窗口](gateway.md) 结束，回滚不会复活永久排空 / 停用的旧运行。租户停用、全局停用、授权撤销会令既有运行与在途调用按网关复核策略停止。这些行为由服务端执行，界面文案不替代授权或取消机制。

## 权限与读取 API

全局管理不需要已选租户或租户成员关系。所有者必须仍有 developer 才能编辑其应用；reviewer 可读全局应用并审核。租户管理员仅因 registry:manage 获得当前租户相关应用的配置和已发布版本，不能读取他人的草稿或其他租户记录。普通已注册账号的全局 workspace 返回 false 能力和空列表；未注册 / 禁用主体拒绝。按钮能力只决定显示，所有写入仍由 T4 API 重新检查。

基础路径 `/api/platform/v1`：

| GET 路径 | 返回与范围 |
| --- | --- |
| `/registry/workspace` | `{actorUserId,canCreate,canReview,items:[{appId,name,category,ownerUserId,canEdit,canReview}]}`；reviewer 全局可读，或 owner + developer |
| `/tenants/{tenantId}/registry/workspace` | `{actorUserId,tenantId,items:[{appId,name,category,current}]}`；当前成员的精确 registry:manage / application / appId / tenantWide 授权；只列有已发布版本或已有租户配置的应用 |
| `/tenants/{tenantId}/registry/apps/{appId}/releases` | `{items: ApplicationReleaseView[]}`；同一租户管理授权，只包含 published；全局版本列表 / 单版本详情继续使用 T4 路径 |
| `/registry/apps/{appId}/audit` | 全局可读应用的 tenantId=null 操作记录 |
| `/tenants/{tenantId}/registry/apps/{appId}/audit` | 同一租户管理授权，只读明确 tenantId 的操作记录 |

`current` 为 null 或 `{releaseVersion,enabled,available,revision}`。租户应用名称 / 分类从当前配置或已发布 Manifest 取得，不泄漏未发布的初始草稿名称。所有者 / 审核者能读全局完整版本，因此全局列表允许使用应用原始元数据。

两个 workspace 分别最多 500 个完整结果，版本列表最多 100（旧全局列表也同步收紧）；多一条即安全 503，不截断后让管理者误以为列表完整。排序按 appId / releaseVersion 字符串，不宣称语义版本最新排序。页面在已取得的受权集合中搜索。

审计返回 `{items,hasMore}`，每条仅含 eventId、actorUserId、tenantId、appId、releaseVersion、action、result、requestId、occurredAt。每个 scope 最近 50 条，更多时明确提示。UI 同时有两个 scope 权限时合并显示并标明“全局发布”或“当前租户”；全局角色不能因此读取租户审计。记录主体是稳定平台 userId，关联 requestId 来自服务端请求，不展示 Token、SQL、部署地址或完整请求内容。跨资源审计与留存管理仍归 T13。

新读取禁止未声明 query，响应 no-store / nosniff；未认证 401，权限 / 当前选择不符 403，不存在 404，参数错误 400，依赖不可用或容量超限 503。读取整体 3 秒；查询前后复核角色或当前授权版本，输出前再核对真实 BFF 会话。全局会话捕获允许空租户，但仍绑定主体、引用、原期限及当前 nullable 租户选择。

旧注册 API 同步补充 live-session 边界和 3 秒整体预算。PUT / POST 读取完命令后，在进入仓储前再次核对真实会话；读取在返回前也复核。操作主体直接来自这次捕获的会话；无法仅凭缓存的 Cookie 认证结果完成退出后的写入。命令与审计仍在同一 PostgreSQL 事务提交；不宣称 BFF 会话存储和注册数据库之间存在分布式原子事务，提交期间会话改变的边界沿用已授权操作语义。

## 冲突、取消与未保存修改

每次 PUT / POST 都先获取 CSRF，发送明确 expectedRevision；不自动重试。成功后重读列表、版本、租户配置和记录。写入成功但读取失败会明确说“操作已提交”，不能重复点击当作未执行。

409 或结果不确定的服务错误会锁住过时动作；已有草稿输入保留，用户先重新读取再决定修改。焦点恢复时，当前身份 / 租户不变且读取成功，则保留未保存输入，同时检查权限、状态和修订号，陈旧草稿不可覆盖服务端结果。离开或切租户前询问是否放弃修改；写入期间禁重复动作和壳层切换。会话或租户变化清除旧内容，旧异步响应不能恢复它。壳层会话读取故障导致编辑器关闭时，明确提醒未保存修改已清除。

## 验证

```powershell
powershell -NoProfile -File tools/verify.ps1 -Scope Core
powershell -NoProfile -File tools/verify.ps1 -Scope Frontend
& apps/sample/backend/.venv/Scripts/python.exe tools/verify-postgres.py --registry-browser
```

Core 包含管理 API 匿名边界、活跃会话 / 退出与命令读取竞争、预算和旧功能回归。默认原生 PG 已包含多主体 workspace / 版本 / 审计范围、撤权、容量边界和真实操作归属；registry-browser 增加三账号浏览器流程。账号选择页仅在测试 OidcFixture 显式启用，仍使用真实 OIDC Code / PKCE / JWT；生产平台没有测试登录入口。

联合验收从同模板构建两个真实前端发布物，使用实际 PostgreSQL、正式注册 / 网关 / 内部签名及流式 HTTP 下游。浏览器完成注册、审核、启用、切换、回滚和停用，并在同一 BFF 会话中检查固定运行、在途流排空 / 取消与 SQL 审计主体。T7 另有真实 Python 消费端链路，T8 不以重复启动 Python 代替管理行为验证。具体本轮结果和截图见 [开发记录](../development.md)。
