# 原生 Keycloak 验收

本入口启动真实 Keycloak、平台 BFF 和 Chromium，验证原示例登录主题、实际凭据校验、密码重置和浏览器会话边界。它使用一次性测试 realm、随机凭据和 Keycloak 开发 H2 数据库；平台仍使用 Development 内存会话。测试账号不写入 `deploy/keycloak/assembleai-realm.json`，也不自动建立平台租户成员关系。

## 准备与运行

在仓库根目录执行：

```powershell
# 一次性准备；下载官方 ZIP、校验 SHA256，解压在项目 .local-data 中
powershell -NoProfile -File tools/install-native-identity.ps1

# 使用项目已锁定的依赖构建平台
dotnet restore AssembleAI.slnx --locked-mode
dotnet build AssembleAI.slnx --no-restore

# 原示例登录骨架/CSS和身份主题保持同步；不改参考快照
node tools/sync-reference-login.mjs

# 启动隔离服务、跑浏览器验收、停止自己启动的进程
& '.\apps\sample\backend\.venv\Scripts\python.exe' tools/verify-keycloak.py
```

另需已安装根目录 `package-lock.json` 对应的 Node 依赖和 Playwright Chromium，安装方式见 [开发记录](development.md)。验证器不下载依赖，不调用 Docker，不改系统 PATH，也不注册 Windows 服务。

依赖固定在 `tools/native-identity.lock.json`：Temurin JDK `25.0.5+8`、Keycloak `26.8.1`，两个 ZIP 合计约 301 MiB，解压和每次独立运行还需要额外磁盘空间。来源为 [Adoptium 官方 release](https://github.com/adoptium/temurin25-binaries/releases/tag/jdk-25.0.5%2B8) 和 [Keycloak 官方 release](https://github.com/keycloak/keycloak/releases/tag/26.8.1)。安装脚本核对文件长度与 SHA256，拒绝覆盖不认识的已有安装目录。

首次启动会执行 Keycloak 构建和开发库初始化，可能需要约两分钟；验证器显示等待进度。默认启动超时 180 秒，可显式使用 `--startup-timeout 300`。进程仅监听临时 loopback 端口；不复用或停止开发者已有服务。流程基于 [官方 ZIP 启动方式](https://www.keycloak.org/getting-started/getting-started-zip) 和 [启动时导入 realm](https://www.keycloak.org/server/importExport#_importing_a_realm_during_startup)。

## 验证内容

- 真实 Keycloak 使用 `assembleai` 主题：原示例的左右分栏、三张叠放预览、波纹、字体、输入框、复选框、按钮与主题切换。账号密码表单直接 POST 给 Keycloak，拒绝错误密码后完成 Authorization Code 登录；realm 强制 PKCE S256。
- 登录错误、忘记密码请求、邮件提交反馈和更新密码 required action 使用同一原示例壳和控件。Keycloak 26 提交重置请求后带通用提示返回登录页；测试以本次私有 SMTP 接收的真实邮件和 action token 证明发送及重置成功，不能仅凭提示文案判断。
- 两次新密码不一致由真实 Keycloak 拒绝；正确设置后，以全新浏览器会话验证新密码登录。
- 记住我由 Keycloak 实际发出有期限的 Cookie；门户 BFF Cookie 仍为会话 Cookie。已有 SSO 时，显式 `prompt=login` 要求再次输入密码；Keycloak 可隐藏已经确认的账号，重认证仍使用同一原示例控件。
- 未登记的回调地址被真实 Keycloak 拒绝，通用协议错误页仍使用原示例壳，不能落回另一套默认登录界面。
- `/bff/session` 的 issuer / subject 与真实账号匹配；匿名请求为 401。
- BFF Cookie 为 HttpOnly、SameSite=Lax；浏览器脚本读不到会话引用，响应和浏览器存储不包含 OIDC token，浏览器不直接调用 token endpoint。
- 已有有效会话不能重新登录覆盖身份（409）；GET 退出为 405。
- 缺 CSRF 或错误 Origin 退出为 403；正常浏览器 POST 退出为 204，旧 Cookie 重放仍为 401。
- 本地退出后可通过 Keycloak 的现有 SSO 再进入，并得到新平台会话；不把本地退出误认为全局 SSO 注销。

标准 OIDC 测试服务的错误签名、issuer、audience、nonce、state 和并发账号回归仍由 `.NET HTTP smoke` 覆盖。`BffKeyPersistenceTests` 另外用实际 RSA PFX 和落盘 keyring 验证生产密钥恢复与错误证书拒绝，不启动数据库。

## 数据与清理

每次独立运行位于 `.local-data/keycloak-tests/run-*`。登录密码、客户端 secret、真实重置邮件中的 action token 和临时数据库都在该忽略目录；凭据不进入进程命令行。SMTP 接收器仅监听本次 loopback 临时端口，不向外部邮箱发信。验证器隔离继承的 Java、Keycloak、.NET 环境和 Playwright 调试设置，避免误用外部数据库、错误 JDK 或记录协议凭据。

结束后停止本次 SMTP 接收器和进程树，再核对绝对路径并删除本次临时目录。若清理失败，命令返回非零并保留目录便于处理，不宣称成功。验证记录位于 `.artifacts/keycloak/run-*`：服务日志、浏览器阶段结果、登录/失败/重置各状态截图，以及 `result.json` 的完成 / 清理状态。浏览器仅打印固定阶段名称，不输出异常中可能包含的表单、Cookie 或回调参数；不录制带凭据的 trace。目录均被 Git 忽略。

此入口验证真实主题与 BFF 的登录链路；它不验证 PostgreSQL 授权仓储、生产数据库会话、多实例故障恢复、证书轮换或正式门户全页与 HTTPS 的联合部署。普通 Core / All 检查不会自动启动 Keycloak；它们和本入口分别记录结果。

## 主题部署与邮件配置

原生验证器将 [assembleai 主题](../deploy/keycloak/themes/assembleai/README.md) 复制到本次 Keycloak 的 `themes/assembleai`，使用部署 realm 的 `loginTheme=assembleai`、`rememberMe=true`、`resetPasswordAllowed=true`。常驻原生 Keycloak 同样需要安装该目录并更新 realm；已导入的 realm 不会因为修改导入 JSON 自动更新。

当前主题覆盖登录、只需密码的重认证、找回密码、设置新密码、通用信息和通用错误页面。表单使用 Keycloak 官方的 action 和字段；其他尚未启用的身份流程继承父主题。部署 realm 不包含邮箱凭据。正式找回邮件必须在 Keycloak 配置企业 SMTP 并验证投递；本机验收的 loopback SMTP 仅用于隔离验证，不能当作生产邮件服务。

`node tools/verify-reference-login.mjs` 另行在随机端口对照原始示例和共享登录资产，覆盖深浅主题、1440/1366/390 宽度以及焦点/勾选状态；测试固定噪声种子并减少动态效果，验证波纹、表单光晕、按钮亮边三个 canvas 的实际像素与原示例一致。正式资源仍保留原随机噪声。该视觉验证不代替本入口的真实身份校验。

## 2026-11-05 原示例登录主题验收

最终命令 `python tools/verify-keycloak.py --startup-timeout 300` 返回 0；`.artifacts/keycloak/run-aea27c5dd5334fa88d2b5df8450a9974/result.json` 的 `passed` 和 `cleanupComplete` 均为 true，本次运行目录已删除。验证了真实主题登录与波纹绘制、错误密码、记住我 Cookie、`prompt=login` 重认证、SMTP 重置邮件、邮件 action token、设置新密码与不一致拒绝、新密码重新登录、非法回调错误页，以及原有 BFF Cookie/CSRF/Origin/撤销重放/SSO 回归。

同轮独立视觉回归在深浅主题与 1440/1366/390 三种宽度全部通过：26 个区域的布局/样式、焦点/勾选、三个 canvas 的实际像素、原生身份表单 POST 和浏览器存储边界。截图位于 `.artifacts/reference-login`。原始参考快照保持不改。

## 2026-11-03 验收记录

完整原生入口返回 0，`result.json` 的 `passed` 和 `cleanupComplete` 均为 true。该次运行故意继承无效 JAVA、外部 Keycloak DB 地址、Production 环境及 Playwright 协议调试 / Node 预加载参数，隔离后仍通过全部检查；未生成指定的调试日志。失败轮次也已回收本次服务和临时凭据。

生产密钥测试及 Core 全部通过：.NET 构建 0 警告 / 0 错误，Node 3 项、Python 9 项、真实 FastAPI → YARP 和扩展后的 .NET smoke。测试脚本修复与回溯见 SPEC B13–B16。

PostgreSQL 后续工作已完成原生锁定安装，并在真实迁移失败确认缺少 `vector.control` 后，将控制面迁移 1–3 与 pgvector 0.8.7 初始化显式拆分。独立的 [原生 PostgreSQL 验收](native-postgres.md) 已通过真实授权仓储、加密会话和 readiness 边界测试，集群与临时凭据已清理；扩展缺失仍会使 vector 初始化失败。

这些实库结果不改变本页边界：Keycloak 浏览器入口仍使用 H2 和平台开发内存会话，尚未串联真实 Keycloak、生产 HTTPS Cookie 与 PostgreSQL 完整登录链路。

租户选择与内部调用契约完成后，真实 Keycloak 回归再次通过：`.artifacts/keycloak/run-600e263855f044e098199c4447bdf4ae`，入口退出 0，验收与清理成功。新租户 / 内部凭据及 OIDC 协议服务 → PG → HTTP 下游联合验收见 [开发记录](development.md)；T3 身份与授权基础已结项。
