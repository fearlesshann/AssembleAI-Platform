using System.Text.Json;
using AssembleAI.Platform.Connectors;
using AssembleAI.Platform.Models;

namespace AssembleAI.Platform.IdentityManagement;

public sealed record IdentityPermissionReference(string Permission, string ResourceType, string ResourceId);
public sealed record IdentityPermissionAction(string Permission, string Name, string Description, bool OrganizationScope,
    IReadOnlyList<IdentityPermissionReference> Requires);
public sealed record IdentityPermissionResource(string ResourceType, string ResourceId, string Name, string Category, string Status,
    IReadOnlyList<IdentityPermissionAction> Actions);
public sealed record IdentityPermissionCatalogView(Guid TenantId, IReadOnlyList<IdentityPermissionResource> Resources);

/// <summary>Assignment metadata, never an authorization decision or a source of credentials.</summary>
public static class IdentityPermissionCatalog
{
    public static IdentityPermissionCatalogView Build(Guid tenant, IReadOnlyList<IdentityGrantApplication> applications,
        ModelGatewayOptions models, ConnectorOptions connectors)
    {
        var resources = new List<IdentityPermissionResource>();
        static IdentityPermissionAction Action(string permission, string name, string description, bool organizations = false,
            params IdentityPermissionReference[] requires) => new(permission, name, description, organizations, requires);
        foreach (var app in applications)
        {
            var actions = app.Permissions.Select(permission => Action(permission,
                app.Permissions.Count == 1 ? "打开与使用应用" : $"应用访问 · {permission}",
                "授予应用入口。应用声明的访问权限需全部具备；模型与业务数据权限另行分配。", false,
                app.Permissions.Where(other => other != permission).Select(other => new IdentityPermissionReference(other, "application", app.AppId)).ToArray())).ToList();
            if (!actions.Any(x => x.Permission == "registry:manage")) actions.Add(Action("registry:manage", "管理租户发布", "选择版本、启停当前租户的应用，查看发布操作记录。"));
            if (!actions.Any(x => x.Permission == "gateway:observe")) actions.Add(Action("gateway:observe", "查看调用记录", "查看当前租户中此应用的网关调用与异常记录。"));
            resources.Add(new("application", app.AppId, app.Name, "applications", app.Enabled ? "当前租户已启用" : "当前租户未启用", actions));
        }
        if (models.Enabled)
            foreach (var (id, model) in models.Catalog.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                resources.Add(new("model", id, model.DisplayName, "models", model.Enabled ? "部署已启用" : "部署已停用", [
                    Action("model:invoke", "调用模型", "通过平台模型网关调用此模型，不包含管理配置。"),
                    Action("model:manage", "管理模型配置", "调整当前租户配置并查看调用记录，不自动授予模型调用权限。")
                ]));
        if (connectors.Enabled)
        {
            foreach (var (id, source) in connectors.Sources.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                resources.Add(new("datasource", id, source.DisplayName, "data", source.Enabled ? "部署已启用" : "部署已停用", [
                    Action("datasource:read", "读取数据源", "允许在指定范围读取数据；查询还需数据集与查询工具权限。", true),
                    Action("datasource:test", "测试连接", "执行受控连接检查，需要连接测试工具权限。", false, new IdentityPermissionReference("tool:invoke", "tool", "connector.test")),
                    Action("datasource:manage", "管理数据源配置", "调整当前租户的限额、超时和启用状态，查看调用记录。")
                ]));
            foreach (var (id, dataset) in connectors.Datasets.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                resources.Add(new("dataset", id, dataset.DisplayName, "data", "受控数据集", [
                    Action("dataset:read", "查询数据集", "查询受控字段；数据源、数据集与查询工具的组织范围须同时覆盖目标工厂。", true,
                        new("datasource:read", "datasource", dataset.SourceId), new("tool:invoke", "tool", "connector.query"))
                ]));
            resources.Add(new("tool", "connector.query", "数据查询工具", "tools", "平台工具", [Action("tool:invoke", "执行数据查询", "运行受控查询，同时检查数据源与数据集权限。", true)]));
            resources.Add(new("tool", "connector.test", "连接测试工具", "tools", "平台工具", [Action("tool:invoke", "执行连接测试", "允许执行检查，同时需要目标数据源的测试连接权限。") ]));
        }
        resources.Add(new("knowledge", "documents", "企业知识文档", "knowledge", "当前租户文档", [
            Action("knowledge:read", "阅读与问答", "读取可见原文、检索及问答；仅返回授权组织内的文档。", true),
            Action("knowledge:manage", "管理文档", "上传、读取、索引与删除授权范围内的文档。", true)
        ]));
        resources.Add(new("audit", "knowledge", "知识库操作审计", "audit", "当前租户记录", [Action("audit:read", "查看知识库审计", "查看文档操作元数据，不授予文档内容访问权限。") ]));
        resources.Add(new("audit", "events", "审计记录导出", "audit", "当前租户记录", [Action("audit:export", "导出审计记录", "仅导出该成员已获准读取的记录，不扩大可读范围。") ]));
        var result = new IdentityPermissionCatalogView(tenant, resources);
        if (resources.Count > 1000 || JsonSerializer.SerializeToUtf8Bytes(result).Length > 2 * 1024 * 1024) throw new IdentityManagementUnavailableException();
        return result;
    }
}
