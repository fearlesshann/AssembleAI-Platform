# T8 Registry Management Implementation Plan

> 按已授权 SPEC 继续实施；根线程集成，读取接口、管理界面、真实浏览器验收分别并行，关键边界交叉复核。

**Goal:** 在现有门户交付应用注册、独立审核、全局与租户启停、版本切换 / 回滚和可归属操作记录。

**Architecture:** Vue 管理工作区复用 BFF / T4 写 API / T5 运行规则。补充按当前角色和精确租户授权过滤的读取接口；全局发布与租户配置分别显示与授权。前端能力仅决定交互，服务端每次重新校验。

**Tech Stack:** 既有 Vue 3 / Element Plus / ASP.NET Core / PostgreSQL / YARP；原生开发，不增加依赖或路由库。

**Spec:** SPEC.md T8；I.registry / I.gateway / I.audit / I.portal / I.identity；V4,V6,V12,V15,V16,V17,V19,V25,V28,V29。

## 方案与约束

- 采用现有门户侧栏下 `/manage/applications` 与 `/{appId}`，完整表单编辑应用 Manifest；前端入口按标识 / 前端版本派生。保留现有状态机和修订号比较，不另起管理站或修改 T4 发布模型。
- 全局 owner developer 可创建 / 编辑 draft / 提交；reviewer 可独立审核 / 全局启停。全局管理不要求租户。租户管理员仅管理当前选中租户获 registry:manage/application/appId tenantWide 授权的应用，不因此取得他人草稿或其他租户数据。
- workspace global 返回 actorUserId / canCreate / canReview / 受权 items 的 canEdit / canReview；tenant 返回受权应用及 current 配置。全局版本复用旧 API；tenant 新版本列表仅 published。完整应用列表最多 500、版本最多 100，超限 503；不静默截断。
- 操作记录按 application 过滤，全局仅 tenantId=null，租户仅当前 tenant；最近 50 条加 hasMore，返回 eventId / actorUserId / tenantId / appId / releaseVersion / action / result / requestId / occurredAt。T13 仍负责跨资源查询、留存与综合审计界面。
- 所有读取 no-store / nosniff / 有界预算；新读取前后复核活跃 BFF。为全局操作补允许空租户的真实会话捕获；旧写边界在执行操作前再次验证会话，仍按既有事务提交语义处理，不宣称会话存储与注册 SQL 原子提交。
- UI 提示全局停用影响所有租户；版本切换 / 回滚只改当前租户后续运行，已有调用按 T5 排空；停用 / 撤权取消已有调用。按钮不在失败时假装成功，409 明确需要重读，不自动覆盖他人修改。
- 无持久化身份 / 表单 / Token。主体 / 租户变化及退出清空旧管理数据，迟到响应不得恢复；写入时禁用重复动作与壳层切换；焦点刷新保留当前未保存草稿，但权限 / 状态变化需阻止过期写入。离开有修改的草稿明确确认。
- 延续 PRODUCT.md、双主题、13px 密集列表、220px 壳层；表单字段有标签，键盘可达，状态不只靠颜色，390px 无页面横向溢出。技术部署字段只出现在研发注册表单。

## 实施任务

- [x] 管理读取 API / DTO / PostgreSQL 仓储：services/platform/Registry 新管理文件；隔离、角色 / 成员撤销、边界容量、审计范围与安全故障的实际 PG 测试。根线程接 Add/Map。
- [x] 管理 UI 与 API 客户端：RegistryWorkspace.vue / registry-api.ts / registry.css；复用写 API，按修订号发送，401 / 403 / 409 / 部署不匹配可解释；单独浏览器 fixtures 覆盖状态 / 竞态。
- [x] 门户接线与边界：Portal.vue 管理路由 / 标题 / 会话 / dirty / busy；静态深链回退；LiveBffSession 全局捕获及旧 RegistryEndpoints 活跃会话检查；相关 HTTP / Node 回归。
- [x] 真实多用户联合：测试 OidcFixture 可选账号页（仅测试工程），新 registry browser harness。owner → reviewer → tenant admin UI 全流程，两个版本 / 固定运行 / SSE 排空取消，SQL 验证 actor 和 requestId。
- [x] 执行 Core / Frontend 和原生 PG `--registry-browser`（必要时联合既有 browser flags），检查双主题 / 窄屏截图；复核 / 文档 / SPEC 记录通过后提交推送当前分支。

## Review Focus

- 无租户的全局审核者是否能进入；租户单独管理员是否被错误要求全局角色。
- 自审、跨租户、混合角色与撤权后的列表 / 审计是否泄露。
- 409 / 取消 / 超时的结果是否误报成功；迟到读取是否覆盖新选择或未保存输入。
- 发布 / 全局停用与租户 enable 是否混淆；旧运行是否因切换重建或得到延长期限。
- 导航离开、退出、焦点恢复是否清理旧数据 / 阻止重复写入并仍保留合理的草稿编辑体验。
