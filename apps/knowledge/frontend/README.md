# Knowledge frontend 0.1.0

独立 Vue 3 / UMD 发布物，同一构建支持 `/apps/knowledge/0.1.0/` 独立运行和门户 qiankun 挂载。入口 `knowledge-root`；导出 bootstrap / mount / update / unmount；环境构建参数与其他应用相同。

浏览器只使用同源 BFF 与受控 ApplicationApi。文档库显示当前账号可见的最近文档子集；管理 grant 仅用于显示操作提示，服务端独立核验每次操作。问题走 `ask`，焦点恢复走新的 `references` 调用。依赖、版本和业务实现不从 ChatBI 或 sample 运行时导入。

```powershell
npm run build --workspace @assembleai/knowledge
node --test tests/frontend/knowledge-protocol.test.mjs
node node_modules/@playwright/test/cli.js test tests/frontend/e2e/knowledge.spec.ts
```

从仓库根目录运行验证命令。前端、后端与应用注册配置见[知识库契约](../../../docs/architecture/knowledge-qa.md)。
