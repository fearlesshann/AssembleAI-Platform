# Product

## Register

product

## Users

制造业企业用户、平台与租户管理人员、内部应用研发与审核人员。业务用户发现和使用应用；管理人员配置访问范围、治理资源并排查运行问题。

## Product Purpose

统一发现、接入和治理独立 AI 应用。目标与范围以 SPEC.md 为准；研发验证样例不作为真实 AI 用例交付。

用户已确认现有 ChatBI 与企业知识库两个示范用例足够。当前开发重点是平台功能与页面的完整交付，包括管理操作、权限边界、真实状态及异常处理；具体清单见 SPEC.md C8。

## Brand Personality

安静、技术、信息优先。参考 Linear 的信息密度与背景层级，保留集智品牌。

用户于2026-11-05明确要求与其原始示例的底层设计风格严格一致。`references/ui-v1`实际HTML/CSS为视觉基准；正式页面按语义组件复用其尺寸、字体、字重、颜色、边框、圆角和状态，内容由平台功能决定。不能用泛称“Linear风格”替代来源对照。具体参数及验收见[设计合同](docs/architecture/reference-design-contract.md)。

## Anti-references

不照搬 Linear 标志；不使用聊天首页、毛玻璃顶栏或额外装饰性动效；不只提供深色主题。示例已有的28px胶囊按钮按其语义使用，表单控件仍采用示例的8px圆角。

## Design Principles

应用任务优先；状态真实可解释；主子应用交互一致；权限事实来自后端；降低日常重复操作成本。

## Accessibility & Inclusion

采用 SPEC 中的 reduced-motion 约束；实现清晰的键盘焦点、语义标签和可读对比度。未声明通过完整 WCAG 审计。
