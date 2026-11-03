# Application Manifest v1

`application-manifest.schema.json` 使用 JSON Schema 2020-12，描述写入草稿的结构。原方案七字段全部保留；新增 schemaVersion、前端发布物版本 / SHA-256、后端版本、分类及说明。`production-reports.valid.json` 是格式示例，摘要为测试值，不代表真实部署。`external-entry.invalid.json` 演示禁止任意外部入口。

运行时还执行 schema 之外的检查：

- 原始 Manifest UTF-8 不超过 32 KiB，整个 API 正文不超过 64 KiB、深度不超过 8；拒绝重复 JSON 字段。对象经过常规 JSON 解析后可能丢失重复字段证据，单独做 schema 校验不足以验证这一项。
- frontendEntry 必须精确等于 `/apps/{appId}/{frontendArtifact.version}/`。拒绝 URL、查询、片段、编码路径和不同应用 / 版本的入口。
- 文本不得含控制字符或首尾空白；版本仅允许无前导零的 `major.minor.patch`，暂不支持预发布标签。字段类型严格，不将字符串数字自动转换成整数。
- 发布 / 恢复时，前端三元组 appId / version / sha256 和入口必须匹配受控部署配置，后端 service / version 必须存在。Manifest 不能写后端 URL；部署声明不代替实际健康 / 兼容验证。

`permissions` 是应用入口所需权限的完整列表，运行时应全部满足，资源为 `application/{appId}`。它只声明需求，不给提交人或租户授权，也不覆盖应用内部的数据集 / 文档等资源授权。

写入的 status 仅为 draft。读接口返回服务器真实状态；读响应不能直接当写入请求发送。发布记录 version 与前端 / 后端版本分别存储，前后端可以独立编号，但发布后整个组合不可修改；升级创建新发布版本，回滚选择仍为 published 的旧记录。

协议及权限见 [注册控制面](../../docs/architecture/registry.md)。Schema 的标准语义参见 [JSON Schema 2020-12 Validation](https://json-schema.org/draft/2020-12/json-schema-validation)。
