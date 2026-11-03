# T3 · 租户选择与内部调用信任契约

> **For agentic workers:** 按既定 SPEC 继续实施；文件独立并行，主线程集成和验收，不重复请求已授权范围的确认。

**Goal:** 登录后安全选择租户，向服务端调用提供短期且可核验的权限凭据，并验证撤权边界。

**Architecture:** 租户 ID 是服务端 ticket 中的选择意图；每次使用重新核验数据库。内部凭据独立于 OIDC，固定 RS256 / issuer / audience / 类型，签发与消费都检查当前授权。T5 将这些契约接入已发布应用的动态路由。

**Tech Stack:** 现有 ASP.NET Core Cookie / Antiforgery、Npgsql、IdentityModel；不增加前端或 Python 依赖。

**Spec:** SPEC I.identity / I.authz，V4–V7 / V14 / V20 / V25 / V27；t1-decisions D1 / D8。

## 决策与边界

- 采用服务端 ticket 保存选择；浏览器自报租户仅为意图。拒绝把整份权限放进 Cookie，或仅修改客户端状态。
- `GET /bff/tenant` 返回 `{tenantId:null, authorizationVersion:null}` 或重新核验的选中租户与策略版本；已选租户被撤权则 403。`POST /bff/tenant` 接收 `{tenantId:UUID}`，当前成员才可切换；同源 Origin + CSRF 必须通过；无身份 401、非法 ID 400、无成员 403、存储故障 503。登录没有默认租户，GET 不修改状态。
- 切换复制原 ticket / tokens / IssuedUtc / ExpiresUtc，只更新选择字段；不得延长 8 小时期限、改绑主体或复活退出会话。并发切换最后成功写入者生效；租户业务请求必须带明确目标并与选中租户核对，旧页面不得隐式落入新租户。
- 内部调用无浏览器签发接口；输入 target 必须来自服务端发布映射和资源读取。绑定 appId / releaseVersion / audience / permission / resource tenant/type/id/org / method/path / requestId。OIDC 凭据永不作为内部 token。
- RS256，RSA 至少 2048 位；`typ=assembleai-internal+jwt`，单一 audience，kid 固定对应受控公钥。最长 30 秒，零时钟宽限；消费时重新读当前成员、策略版本并核验同一权限范围，无授权缓存。签名只解决来源，不替代业务授权或写操作幂等。
- 配置默认关闭；外部 PFX 私钥仅由平台签发端加载，公钥通过受控 JWKS 输出。轮换先发布新旧公钥，再切签名，至少等待 30 秒并确认所有消费者更新后撤下旧 key；消费者应用移除旧 key 的配置并重建密钥集后拒绝旧签名，无自动热加载承诺。
- 此轮测试直接使用生产签发 / 验证服务与真实 HTTP/PG；不虚构 T4 注册记录或把开发 sample 代理升级为正式网关。跨语言接入、正式路由和在途 SSE 撤权仍在 T5/T7。

## Task 1 · 租户会话

Files: `Identity/TenantSession.cs`、`Identity/BffEndpoints.cs`、`Identity/BffAuthentication.cs`、`tests/platform/TenantSessionTests.cs`。

- [x] HTTP 红测覆盖无租户、双租户切换、无成员拒绝、Origin / CSRF、原会话期限、旧 Cookie / 多标签页、撤权、存储故障与退出竞争。
- [x] 实现受保护端点与服务端读取方法；复用验证逻辑，保留 session 响应现有结构。
- [x] 增加实库会话选择持久性验证，普通 Core 与实库入口分别验收。

## Task 2 · 内部调用凭据

Files: `services/platform/InternalCalls/*`、`tests/platform/InternalCallTests.cs`；主线程接入默认关闭的配置和 JWKS 映射。

- [x] 固化服务端 target / verified context 类型、签发与消费接口；先写失败契约。
- [x] 配置校验 / RSA 私钥 / 公钥 JWKS / RS256 签名与严格验证；无 public mint 端点。
- [x] 正常调用、身份伪造、错误签名 / issuer / audience / typ / kid、过期 / 未来、目标与 requestId 不匹配、过期策略版本 / 撤权、密钥轮换与安全故障均有测试。

## Task 3 · 交付

- [x] Core、实库验收与独立复核；遇到失败定位并记录 SPEC §B。
- [x] 更新 SPEC / 接口文档和验收边界，提交并推送当前功能分支。

## Review Focus

1. Cookie SigningIn 自动更新时间不能让切换延长期限。
2. 退出与切换并发不能通过 Store 生成新会话，已认证旧 ticket 只能 Renew。
3. 选中租户撤权后不能继续被当作授权；旧页面的明确目标不能被静默换租户。
4. 签名 JWT 中重复字段、多 audience、算法替换与不受控 key 地址必须拒绝。
5. JWT 有效也必须满足当前策略版本和精确资源范围；接收端不从未验证 token 决定目标。

## 执行记录

- 初始租户 HTTP 测试以 404 失败；实现后继续捕获非法 UUID 500 与 7 小时切换 issued 时间变更，修复后完整矩阵通过。
- 内部凭据首次 PFX 加载失败；独立探针确认 CNG 可签名但不能导出私钥，改为持有句柄直签后 JWT / 仅公钥 / 轮换契约通过。回溯见 B20–B22。
- 完整 Core 通过：Node 3 项、Python 9 项、锁定还原、.NET 零警告 / 零错误、原有 HTTP 与新租户 / 内部凭据测试、实际 FastAPI 代理。
- PG 新联合验收：run-4423450349cc4c1a9ba6da9851c4dbf8；真实 Keycloak 回归：run-600e263855f044e098199c4447bdf4ae；均退出 0 且清理完成。
- 独立实现复核与 T3 原任务结项审计无阻塞问题；T3 标 x，后续 T4 注册 / T5 正式网关 / T7 跨语言 / T14 生产部署保留各自验收边界。
