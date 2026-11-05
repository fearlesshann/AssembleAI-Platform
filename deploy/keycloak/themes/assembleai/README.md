# 集智身份服务登录主题

`login/login.ftl` 的 DOM 来自 `references/ui-v1/index.html`，样式完整复用同目录原始 `app.css`，保留登录页的分栏、宣传叠图、波纹、输入状态、复选框、亮面按钮、主题切换与响应式布局。原始快照保持不改；本目录不引入示例的模拟认证代码。

运行 `node tools/sync-reference-login.mjs` 从快照生成模板、CSS 和图片，并将 `apps/portal/public/reference-login` 的共享字体及行为脚本同步到此主题。两种登录宿主使用相同的资源字节。业务文案替换仅移除“任意非空账号密码即可进入”等演示声明和虚构在线数。

将 `assembleai` 目录复制到 Keycloak 的 `themes` 目录，设置 realm 的 `loginTheme` 为 `assembleai`。页面直接向 Keycloak 提供的 `${url.loginAction}` 提交 `username`、`password`、`rememberMe` 和 `credentialId`；密码不经过 Portal/BFF，也不存储在浏览器。失败反馈使用身份服务返回的错误，记住我与忘记密码入口分别服从 `realm.rememberMe` 和 `realm.resetPasswordAllowed`。登录、只需密码的重认证、找回密码、设置新密码 required action、通用信息和通用错误页面共用同一原示例壳与控件；其余尚未启用的身份流程继承 `keycloak` 父主题。

找回表单实际交由 Keycloak 发出邮件；部署环境需要配置企业 SMTP。`tools/verify-keycloak.py` 在独立 Keycloak 中使用本次 loopback SMTP 接收器验证真实邮件、action token、新密码设置和重新登录；不向外部邮箱发送测试邮件。

开发夹具使用 `apps/portal/public/reference-login/login.html`，由身份夹具服务同源渲染；该 HTML 不能作为可绕过身份服务的独立登录入口。文本替换槽位如下：

| 槽位 | 值 |
| --- | --- |
| `@@ASSET_BASE@@` | 同源资源目录，例如 `/reference-login` |
| `@@LOGIN_ACTION@@` | 身份夹具接收原生 POST 的 URL |
| `@@USERNAME@@` | 首次空值；登录失败可回显账号 |
| `@@ERROR@@` | 身份夹具的安全错误文案 |
| `@@CSRF_TOKEN@@` | 隐藏字段 `form_token` 的服务端校验值 |
| `@@FORGOT_HREF@@` | 实际找回或重置帮助路由 |
| `@@ERROR_HIDDEN@@` | 仅可信常量 `hidden` 或空字符串 |
| `@@REMEMBER_CHECKED@@` | 仅可信常量 `checked` 或空字符串 |

前六项必须按 HTML 属性/文本上下文转义，不能直接拼接用户输入。夹具必须验证凭据和 CSRF；前端仅做非空验证和防止重复提交。

`node tools/verify-reference-login.mjs` 在随机本地端口运行浏览器对照：深浅主题、1440/1366/390 宽度，逐项比较原示例与实际登录的布局、字体、颜色、边框、焦点和勾选状态，并验证原生 POST 和浏览器存储中不存在凭据/伪认证标记。截图输出到 `.artifacts/reference-login/`。对照时仅统一两处允许变化的业务文案，在测试中固定噪声种子并使用减少动态效果偏好，逐字节比较波纹、表单光晕和按钮亮边三个 canvas 的绘制结果；正式资源保持原随机噪声行为。
