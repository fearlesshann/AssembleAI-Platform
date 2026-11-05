# 用户示例设计合同

2026-11-05用户明确：正式前端必须与示例抽象的底层设计风格严格一致，业务内容按实际功能组织。此合同将要求落实到可测的样式；来源是仓库保留的示例运行时HTML/CSS，不从“Linear风格”一词或预览宣传图推断。

## 基准与来源

- 原始快照：[references/ui-v1](../../references/ui-v1/README.md)，来源提交`f99202e32269ffcb411b36077ed329ea3b9d6f7e`；保持原快照不改。
- 样式：[app.css](../../references/ui-v1/app.css)。`:root`/dark在20行起、light在3178行起；外壳318–660、列表1040–1290、表单1490–1538、实际管理数据表2787–3120。
- 结构：[index.html](../../references/ui-v1/index.html)的sidebar/surface、应用中心和管理中心；正式参考路由`/reference-ui/`保留其真实DOM与脚本。
- `preview-shots/login-shot-*.png`是示例登录展示素材，不等同于当前`app.css`真实布局，不能用其较粗字号或密度覆盖运行时规则。
- 参考页面的模拟认证、业务假数据和按钮反馈不进入正式流程；正式OIDC、授权、路由、持久化、失败恢复仍执行SPEC。此合同对齐视觉语言，不迁移模拟业务。

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

示例motion-instant100ms、fast160ms，`ease-out-soft:cubic-bezier(.2,.8,.2,1)`。正式界面保留SPEC的100–160ms颜色/透明度约束和prefers-reduced-motion，不复制示例缩放、220ms页面过场或模拟登录动画。这些是业务可达性与已确认行为约束，不能作为另起视觉体系的理由。

## 本轮校准前的差异

2026-11-05 / `c2cc530`正式门户：侧栏220px、导航约40px、topbar64px、主区35px/32px内边距、页面标题26px/650、品牌19px/650、系统字体。主面板缺少示例8px外缩进/边框/radius；暗色主背景误用`#08090a`而不是surface `#141516`，浅色侧栏与文本也采用另一组token。应用列表为多行大留白行而不是示例40px单行，按钮/表单圆角及高度未使用示例规格。这些都属于风格漂移，不能以“同为Linear风格”视作一致。

校准要求：共享示例token与字体；统一外壳、导航、工具区、应用列表、输入、按钮、管理表格；每个新页面按语义组件复用。身份、应用、模型、数据源和统一审计的业务信息不必相同，但外观基础不可各自另定。

## 验收

`tests/frontend/e2e/reference-design.spec.ts`将运行保留示例与正式页面，对比实际computed styles和关键矩形尺寸；对照light/dark、选中/hover和必要焦点。测试样例使用明确协议夹具，不用模拟认证替代正式BFF验证。正式页面字体请求应来自本地，参考远程字体在测试内映射至同版本本地文件，保证离线可重复。

最终截图仍需目视检查层次、密度与窄屏；CSS变量数值相同不能代替真实布局验收。旧[复用评估](ui-reuse-audit.md)记载的是2026-11-02按当时SPEC重新映射的决定；本次用户更严格的要求以此合同及更新后的SPEC为准。
