# 用户示例设计合同

2026-11-05用户再次明确：任何组件先查示例，已有实现必须复用其结构、样式和交互，业务内容按实际功能组织。此前仅对齐token、保留另一套登录入口 / 表格 / 控件的实现不满足要求；此前“严格一致”验收结论作废。来源是仓库保留的示例运行时HTML / CSS / JS，不从“Linear风格”一词推断。

## 基准与来源

- 原始快照：[references/ui-v1](../../references/ui-v1/README.md)，来源提交`f99202e32269ffcb411b36077ed329ea3b9d6f7e`；保持原快照不改。
- 样式：[app.css](../../references/ui-v1/app.css)。`:root`/dark在20行起、light在3178行起；外壳318–660、列表1040–1290、表单1490–1538、实际管理数据表2787–3120。
- 结构：[index.html](../../references/ui-v1/index.html)的sidebar/surface、应用中心和管理中心；正式参考路由`/reference-ui/`保留其真实DOM与脚本。
- `preview-shots/login-shot-*.png`是原登录左侧三张静态叠放展示图，必须保留在完整双栏登录页；它们不是管理表格运行时的替代样式来源。
- 参考页面的模拟认证、业务假数据不进入正式流程；正式OIDC、授权、路由、持久化、失败恢复仍执行SPEC。复用控件的实际交互，包括选择、滚轮、分页、抽屉和确认。

## 组件来源与接入

| 用户可见部分 | 必须复用的原实现 | 接入点 |
| --- | --- | --- |
| 登录整页 | index.html的login-screen双栏、48px字段、remember / forgot、Specular按钮、三张截图、波纹与背景效果 | tools/sync-reference-login.mjs生成Keycloak登录主题和隔离预览模板；原生表单POST到身份服务，不在Portal持有密码 |
| 门户外壳 / 用户菜单 | page / frame / sidebar / surface / topbar / user-menu与折叠结构 | Portal.vue；tools/sync-reference-shell.mjs保留源声明，仅加reference-ui作用域 |
| 应用列表 | list-toolbar / list-search / filter-row / app-list / app-row / props | Portal.vue接授权目录、筛选和详情 |
| 管理表格 | data-grid / toolbar / body / table / foot / pager完整结构 | Registry / Identity / Resource / Audit Workspace；只替换业务列与真实分页数据 |
| 输入 / 按钮 | ui-input / subwindow-field / pill-btn / icon-btn / link-btn / switch | reference组件及原类名；移除原先覆盖规则 |
| 自定义下拉 | ui-select及listbox选项DOM，app.js原打开、键盘和选中逻辑 | ReferenceSelect与reference-runtime.js；不使用原生select模拟 |
| 日期时间 | ui-datetime / dtp-pop；日期 / 月 / 年面板及小时分钟3D滚轮 | ReferenceDateTime；真实筛选值保持UTC接口边界 |
| 分页 | data-grid__pager及每页条数原下拉 | ReferencePagination；接真实有界集合或服务端游标，不能假翻页 |
| 抽屉 / 确认 | overlay / drawer.subwindow / subwindow-foot与dialog | ReferenceDrawer / ReferenceConfirm；native dialog仅提供焦点限制，视觉仍原结构 |
| AI用例控件 | 同上表格 / 输入 / 下拉 / 按钮及composer | ChatBI、知识库共享组件；业务流程与API保持原合同 |

原快照不可修改。生成文件应可从源码重复生成；适配仅限Vue生命周期、作用域、语义标签、真实数据和必要响应式布局，不能改源组件的圆角、尺寸、颜色、字体或展开层外观。新增组件须先检索原HTML / CSS / JS并记录无对应实现的理由。

登录主题位于`deploy/keycloak/themes/assembleai`。Keycloak实际验证账号密码；其他OIDC提供方需在其身份页面应用同一主题，不通过把密码交给Portal解决视觉问题。门户401和退出后导航到`/bff/login?...&prompt=login`，请求身份服务重新验证；退出依然是撤销平台本地会话，不宣称注销其他系统的SSO。隔离本地预览的测试身份服务也使用同一登录模板，测试账号只属于该环境。

## 字体与层次

示例字体是Inter Variable 4.1，font stack为`"Inter Variable", "SF Pro Display", -apple-system, BlinkMacSystemFont, "Segoe UI", "Roboto", "Oxygen", "Ubuntu", "Cantarell", "Open Sans", "Helvetica Neue", sans-serif`；中文由系统回退字体显示。字重为400/510/590；`font-feature-settings:"cv01","ss03"`、optical sizing auto。

正式版本自托管同版本常规字体：[InterVariable.woff2](../../apps/portal/public/fonts/InterVariable.woff2)，[OFL许可](../../apps/portal/public/fonts/Inter-LICENSE.txt)。来源`rsms/inter`的v4.1提交`e3a3d4c57d5ecc01453a575621882a384c1995a3`，字体SHA256 `693b77d4f32ee9b8bfc995589b5fad5e99adf2832738661f5402f9978429a8e3`。正式页无需请求Linear字体域名。

| 语义 | 示例实际规则 |
| --- | --- |
| 页面列表标题 | 16px / 590 / line-height 28px |
| 品牌工作区名 | 14px / 590 / line-height 21px / letter-spacing -0.182px |
| 导航、资源名称 | 13px / 510 |
| 数据正文 | 13px / 400 |
| 面包屑、表头、筛选 | 12px / 510；面包屑line-height 14px |
| 次要信息 | 12px / 400，使用对应secondary或tertiary颜色，不放大成页面标题 |
| 标识 | Berkeley Mono / 系统等宽fallback；请求号等机器标识才用等宽 |

## 双主题层次

| token / 角色 | Light | Dark |
| --- | --- | --- |
| frame / plate 外框 | `#f7f8f8` | `#08090a` |
| panel 侧栏 | `#f3f4f5` | `#08090a` |
| surface 主面板 | `#ffffff` | `#141516` |
| text-frame 强调文字 | `#08090a` | `#f7f8f8` |
| text-primary 正文 | `#2c2e33` | `#d4dae3` |
| text-secondary 次要正文 | `#5a5f6b` | `#8e949d` |
| text-tertiary 辅助标记 | `#8a8f98` | `#6a6e76` |
| surface-border | `rgba(0,0,0,.08)` | `#ffffff16` |
| hairline | `#e6e8eb` | `#32343a` |
| nav-selected | `rgba(0,0,0,.06)` | `#ffffff1a` |
| nav-hover / strong | `rgba(0,0,0,.04)` / `.07` | `#ffffff12` / `#ffffff18` |
| accent / bright | `#5e6ad2` / `#7170ff` | 同Light |
| control背景 | `#ffffff` | `#0d0e0f` |
| table shell / header / row | `#ffffff` / `#f7f8f8` / `#ffffff` | `#0f1011` / `#161718` / `#121314` |

颜色语义保持一致。低对比tertiary用于辅助标记；必要说明和操作文本使用primary/secondary，不能为“像示例”把重要错误或指引变成难以阅读的灰字。不得声称通过完整WCAG审计。

## 结构与组件度量

| 组件 | 示例实际规则 |
| --- | --- |
| 桌面侧栏 | 宽232px；padding `8px 16px 16px 8px`；模块gap8px |
| 导航 | 高28px；padding `0 7px`；gap8px；radius8px；行gap2px；icon16px、细线约1.3px |
| 品牌图 | 22×22px，无底板、无padding、无radius；浅色invert(1) |
| 主面板surface | 外margin `8px 8px 8px 0`；1px边框、radius8px、token定义的细ring，独立可滚动内容 |
| topbar | 高44px；padding `8px 12px`；gap8px；底边1px |
| 列表工具区 | padding `12px 16px`；gap12px；标题与工具处于同一紧凑层次 |
| 普通工具按钮 | 高28px；横padding7px；radius9999px；13.3333px / 400；pill背景与1px inset |
| 主操作按钮 | 同一尺寸；accent底、白字、无普通按钮inset；hover使用accent-hover |
| 图标按钮 | 28×28px；radius8px |
| 筛选chip | 高24px；横padding8px；radius16px；12px / 510 |
| 应用搜索 | 高28px；横padding8px；radius8px；宽220px；search背景和inset |
| 管理搜索 | 高32px；横padding10px；radius8px；最小宽220px |
| 表单单行输入 | 高36px；横padding10px；radius8px；1px边框；13px / 510；control背景 |
| 应用列表项 | 高40px；横padding8px；gap10px；radius8px；24px icon / radius6px；单行主次信息 |
| 管理数据表外壳 | margin `0 8px 12px`；radius10px；1px边框；table-shell背景 |
| 表工具栏 | padding `10px 12px`；gap10px；底边1px；filter-gap4px |
| 实际管理表头 | 高40px；padding `0 10px`；12px / 510；table-header背景 |
| 实际管理表行 | 高44px；padding `0 10px`；13px / 400；table-row背景；divider-row底边 |
| 表底栏 | padding `8px 12px`；gap12px；顶边1px；12px状态/页码 |

管理中心实际用`.data-grid`，不使用较早`.admin-table`的40px行高替代44px基准。多行业务详情可自然增高；无需为凑44px裁掉必要内容。窄屏沿用正式门户的可达导航、内容重排与局部表格横滚，避免复制示例桌面固定宽度造成页面溢出。

## 状态与动效

选中通过背景差异表达，无侧边品牌色条。hover由对应nav/control/table token表达。焦点保持可辨识，错误/停用/空态/加载使用同一字体与层次；不得只校准默认截图。

示例motion-instant100ms、fast160ms、正常220ms，`ease-out-soft:cubic-bezier(.2,.8,.2,1)`；复用对应组件的缩放、弹层和滚轮交互，登录保留原背景效果，遵循prefers-reduced-motion。不能以旧的100–160ms约束为由删掉用户明确要求的既有交互；不另加装饰。

## 历史偏差与本次纠正

2026-11-05 / `c2cc530`正式门户：侧栏220px、导航约40px、topbar64px、主区35px/32px内边距、页面标题26px/650、品牌19px/650、系统字体。主面板缺少示例8px外缩进/边框/radius；暗色主背景误用`#08090a`而不是surface `#141516`，浅色侧栏与文本也采用另一组token。应用列表为多行大留白行而不是示例40px单行，按钮/表单圆角及高度未使用示例规格。这些都属于风格漂移，不能以“同为Linear风格”视作一致。

随后`65c926c`虽通过部分token / 尺寸对照，仍遗漏完整登录与原组件交互，也保留了另一套表格结构。用户再次指出的这些问题按SPEC B100纠正；该轮结果不能当作完整复用验收。现行要求是直接复用已有结构、CSS与交互，覆盖整页和展开状态；身份、应用、模型、数据源和审计只替换真实业务内容。

## 验收

`tests/frontend/e2e/reference-design.spec.ts`与`reference-controls.spec.ts`运行原示例和正式页面，对照完整组件实际computed styles、几何及交互；覆盖light / dark、选中 / hover / 焦点、展开下拉、日期时间面板、抽屉与确认框，保存双方截图。`reference-lifecycle.spec.ts`验证真实qiankun卸载重挂与独立发布字体。`tools/verify-reference-login.mjs`对照原登录整页在两主题与1440 / 1366 / 390视口的26个区域、焦点、复选框及真实表单POST；固定测试噪声种子后，三个canvas必须已绘制且像素逐字节一致。Keycloak原生验收独立验证实际身份服务。协议夹具不代替Keycloak。正式字体本地自托管或内联至独立发布物，参考远程字体仅在测试内映射同版本本地文件。

最终截图仍需目视检查层次、密度与窄屏；CSS变量数值相同不能代替真实布局验收。旧[复用评估](ui-reuse-audit.md)记载的是2026-11-02按当时SPEC重新映射的决定；本次用户更严格的要求以此合同及更新后的SPEC为准。
