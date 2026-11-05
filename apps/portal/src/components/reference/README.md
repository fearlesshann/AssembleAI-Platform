# 原示例控件复用

来源 `references/ui-v1`，原目录不改。保留原类名、HTML层次与全部控件状态。

| 封装 | 原结构 / 样式 / 交互 |
| --- | --- |
| ReferenceSelect | index.html 789、866；app.css 2153–2312；app.js 996–1135 |
| ReferenceDateTime | index.html 902；app.css 2313–2785；app.js 1137–2482，含42日格、月/年层级、24/60项时间滚轮及惯性 |
| ReferenceInput | app.css 1503–1533 的 ui-input |
| ReferenceButton | app.css 612–663、1699–1721 的 pill-btn/icon-btn/is-primary/is-danger；3049 link-btn |
| ReferencePagination | index.html 783–812，data-grid__footer/pager/page-size+ui-select--sm+icon-btn；app.css 3075–3130 |
| reference-wheel.js | tools/sync-reference-wheel.mjs从原inertialWheelScroll.js生成；只替换IIFE导出，不改变算法 |
| reference-motion.ts | app.js的switchView、content-fade-in和openUserMenu；原CSS负责轨迹 / 时长，Vue指令负责触发；文档共享滚轮控制器按界面根生命周期释放 |
| useReferenceOverlay | app.js initOverlays；原开关类名、强制布局和220ms退出，Vue Transition只管理DOM保留，native dialog提供模态焦点 |

reference-controls.css直接提取相关原rules，仅增加零特异性作用域，未重新设计样式。reference-runtime.js直接提取两个原控件模块，调整命名空间和选择器作用域，增加Vue卸载清理、禁用守卫、焦点返回及滚轮键盘操作。通过 document 级 Symbol 共享运行时，门户和各微应用的独立 bundle 只注册一次委托监听；卸载清理当前控件拖拽监听、延迟与动画，不持有已销毁DOM。下拉菜单保留原 4px 间距与宽度，定位改为视口fixed，并用manual popover进入浏览器顶层；DOM仍属于原控件，保留继承样式、委托事件和原生抽屉焦点边界。打开期间跟踪滚动、布局、祖先动画 / 缩放与视口变化，避免fixed被祖先transform重新建立坐标系或被table/drawer裁切；触发控件离开可见区域时关闭，关闭 / 卸载释放定位RAF与监听。依赖支持Popover API的现代浏览器；Escape 仅关闭当前控件，防止同时关闭父模态抽屉。

日期与原示例一致以本地分钟为选择精度；接收秒级输入时不静默改写父状态，只有用户真实选择才发布分钟字符串。服务查询仍由调用方转换UTC并校验时间窗。分页未知总数使用cursor模式的当前页 / …，不伪造总条数或可跳转页。

MIT原许可见本目录LICENSE，完整声明也保留在复制运行时头部。

默认动效full与原示例一致；显式`?motion=system`跟随OS偏好，`?motion=reduced`禁用。必须在正常动效下测试真实帧、退出完成后移除和滚轮轨迹，静态样式测试不覆盖这些行为。ReferenceDrawer调用方用`:open`控制，不能用父级`v-if`提前销毁而跳过leave；父页面整体卸载时仍清理模态与计时器。

定位依据：[HTML Popover标准](https://html.spec.whatwg.org/multipage/popover.html)、[CSS顶层与包含块](https://drafts.csswg.org/css-position-4/#top-layer)。原示例快照不改；`reference-position.spec.ts`在默认full下验证实际矩形与运动帧，不通过减少动效掩盖偏移。
