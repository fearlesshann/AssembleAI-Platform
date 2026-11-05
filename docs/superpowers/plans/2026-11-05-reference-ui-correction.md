# 原示例UI严格复用纠正

用户已明确授权直接实施；原快照保持不改，仅main。涉及V4 / V7 / V11 / V12 / V25 / V33 / V36 / V37，接口沿用I.identity / I.portal / I.registry / I.models / I.connectors / I.audit。

1. 完整移植原登录页到Keycloak主题与隔离预览身份服务。真实表单提交、错误 / remember / forgot，BFF继续OIDC；退出后prompt=login防止无感重新登录。
2. 抽取原shell和现成输入、按钮、自定义下拉、日期时间 / 滚轮、分页、抽屉、确认实现，保留源码声明和状态；管理页重组为原完整data-grid，不另画表格。
3. Portal及4类管理工作区接真实组件；两个既有AI用例可见控件同步；保留权限、修订、草稿防护、取消及迟到响应保护。
4. 对照原页面实际渲染与展开状态，执行Vue类型、构建、前端全量、.NET / Python、原生PostgreSQL浏览器、Keycloak；检查真实截图后重启4180本机预览。

涉及apps/portal/src、两个AI前端、deploy/keycloak主题、OIDC测试夹具和相应浏览器用例。回归命令：tools/verify.ps1 -Scope All、tools/verify-reference-login.mjs、tools/verify-keycloak.py、tools/verify-postgres.py管理浏览器专项。SPEC B100记录此前误判，B101记录退出后SSO自动重建。

验收只报告本轮实际结果；旧token对照通过不当作本次完整复用通过。
