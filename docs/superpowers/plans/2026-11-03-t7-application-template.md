# T7 · 子应用接入模板实施计划

按已授权 SPEC 继续；原生进程开发，保留既有 OIDC / Keycloak 接口。

目标：同一版本化前端支持独立页面与 qiankun 嵌入；通过固定运行实例的 SDK 调用真实 YARP → FastAPI JSON / SSE；以部署配置和注册记录接入第二个应用，不改门户核心。

依据：I.portal / I.manifest / I.identity / I.gateway / I.deploy，V1,V2,V6,V8,V14,V18,V19,V25,V30。本轮为技术模板，业务 ChatBI / 知识库留 T11 / T12。

## 契约

- SDK 将 runId / tenantId / appId / releaseVersion / expiresAt 复制到内存绑定；只允许安全相对操作路径、GET / POST、数据 query 与 JSON body。Cookie 同源，POST 获取 CSRF，不向应用交付 OIDC / 内部 Bearer。应用上下文是展示信息；SDK 不是 JavaScript 安全隔离边界，服务端仍逐次授权。
- SSE 按 UTF-8 和有界帧读取，delta / complete / error；只有 complete 代表完成，截断 / error 不能伪装成功。离开应用、切租户、运行到期或用户取消立即释放请求和 reader；不自动重试。
- FastAPI 将内部 Bearer 和 requestId 交给固定平台验证端点，附自身配置的 service / version 及实际 method / path。端点从可信注册与部署配置派生 audience / permission，复用 T3 RS256 严格验证与实时授权版本。请求不能指定用户 / 租户 / 权限 / audience。只在显式配置开启；HTTPS，开发 loopback 可 HTTP；无代理 / 重定向，失败关闭。
- 验证端点加载 token 中 app / release / tenant 的未可信路由提示，随后必须完成签名、全部目标字段、期限及当前授权验证才返回身份。继续允许已签发旧版本调用遵循 T5 排空，不能把当前版本指针检查替代排空规则。每次验证还检查租户启用与全局发布状态。既有内部凭据最大 30 秒及非单次使用语义不变。
- 样例 POST probe 返回已验证身份和关联信息；GET events 首段即时送达，周期重新验证原凭据。原凭据过期停止，不延长；模板流最长约 20 秒。独立页面同源 BFF，显式连接平台创建运行；刷新不自动重建。
- 静态资源按部署配置的 app / version / root / sha256 启动验证并持有不可变快照；真实摘要覆盖排序文件路径、长度和文件 SHA256。拒绝路径越界、符号链接、隐藏 / 源文件；只对已知应用版本的无扩展深链回退 index，缺失资产和未知版本 404。Vite / 预览复用，不从浏览器输入选择目录。

## 实施与验证

1. SDK + 样例 UI / 独立接入，Node 边界、取消与 SSE 协议回归。
2. FastAPI 验证适配器、JSON / SSE，真实 loopback HTTP / uvicorn 负例与断连回归。
3. 配置化资源快照与可参数化样例构建，Node 摘要 / 路由 / 篡改负例。
4. 平台验证端点、可信目标查询、门户能力生命周期集成，.NET 正反例。
5. 真实 PG / OIDC / YARP / FastAPI / 浏览器联合：独立与嵌入、流取消、第二应用注册、无凭据泄漏、撤权拒绝。
6. Core / Frontend / 原生 PG 验证；复核、文档和 SPEC 结果；完成后提交推送当前分支。

文件范围：packages/app-sdk、apps/sample、ApplicationRunner、InternalCalls、静态服务工具、tests 对应目录、接入与开发文档。职责并行，根线程集成与修改 SPEC。
