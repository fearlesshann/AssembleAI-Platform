# UI 初版复用评估

日期：2026-11-02。只读代码审查，未启动样例或执行其脚本。

来源：<https://github.com/fearlesshann/AssembleAI>，分支 `main`，提交 `f99202e32269ffcb411b36077ed329ea3b9d6f7e`。本地快照：[references/ui-v1](../../references/ui-v1/README.md)；29 个 Git 跟踪文件，排除 `.git`。逐文件 SHA-256 见 [source.json](../../references/ui-v1.source.json)。

## 结论

复用品牌资产、信息布局与交互参考；正式工程新建 Vue / Element Plus 外壳、OIDC 登录和后端接入。原样例保留，不直接搬运全局 DOM 脚本。

## 工程事实

| 项目 | 核验结果 | 依据（快照内行号） |
| --- | --- | --- |
| 技术形态 | 纯 HTML / CSS / JS；无 Vue、TypeScript、Element Plus、Vite、qiankun 工程 | `README.md:3`、`README.md:27`、`package.json:1` |
| 依赖 | package.json 仅声明 Lenis；页面加载本地脚本 | `package.json:14`、`index.html:971` |
| 后端与流 | 没有业务 API、真实认证、SSE、微前端生命周期 | `README.md:3`；脚本接入点审查 |
| 测试 | test 为占位失败命令，无可沿用测试基线 | `package.json:7` |
| 许可元数据 | LICENSE 为 MIT；package.json 标 ISC，二者不一致 | `LICENSE:1`、`package.json:11` |

## 复用清单

| 内容 | 处理方式 | 依据 |
| --- | --- | --- |
| `logo.png`、favicon 等品牌素材 | 作为首期候选；实际使用的素材保留来源与许可 | `index.html:7`、`index.html:52` |
| 侧栏 + 主区、列表密度、背景选中态 | 迁移为 Vue 组件；按 SPEC 校准尺寸 | `index.html:128`、`index.html:417`、`app.css:318`、`app.css:426` |
| 双主题与 token 组织 | 复用结构思路；重新映射 Element Plus 和 SPEC token | `app.js:2820`、`app.css:20`、`app.css:3178` |
| 应用搜索、空状态、表格、抽屉 | 保留交互参考，接真实状态与 API | `index.html:422`、`index.html:445`、`app.js:955`、`app.css:1318` |
| 登录页与截图 | 仅作视觉参考，不沿用模拟认证或登录过场动画 | `index.html:32`、`index.html:64` |

## 必须替换的行为

| 原行为 | 正式实现要求 | 依据 |
| --- | --- | --- |
| 非空账号密码即可登录；信任 localStorage / sessionStorage 标记 | Keycloak OIDC；服务端会话与权限验证 | `app.js:209`、`app.js:233`、`index.html:23` |
| 初始主题跟随系统，异常回退 dark | 首次访问默认浅色；以后尊重用户明确选择 | `index.html:15`、`index.html:20`；SPEC V10 |
| 默认首屏是回流焊工单工作台 | 应用列表 / 分类 / 搜索为门户入口 | `index.html:259`；SPEC V10 |
| 应用与用户硬编码；刷新只等待 700ms；搜索仅筛 DOM | 从授权清单加载，区分空数据、无权限、失败与加载态 | `index.html:453`、`index.html:592`、`app.js:955`、`app.js:2978` |
| 动效默认 full；部分过场使用缩放、宽度变化和 220ms 以上时长 | 100–160ms 颜色 / 透明度；尊重 prefers-reduced-motion | `app.js:3133`、`app.css:148`、`app.css:329`、`app.css:455`；SPEC C6 |
| 通用按钮胶囊化；属性面板 220px；浅色 token 与规格不一致 | 按 SPEC 圆角、320–380px inspector、主题值实现 | `app.css:42`、`app.css:615`、`app.css:3180` |
| 从 Linear 域名请求字体 | 首期系统字体；若需自托管字体，另核验许可并随包交付 | `app.css:8`、`app.css:16` |

## 尚未验证

实际渲染与对比度、键盘焦点、浏览器兼容、最小视口、深链刷新、qiankun 挂载 / 卸载 / 重挂、资源路径和样式隔离。源码中存在相关交互不等于这些能力已验收；分别在 T2 / T6 / T7 验证。

快照保留原许可与 package 元数据，避免静默改变来源；正式工程发布前统一新包的许可声明。
