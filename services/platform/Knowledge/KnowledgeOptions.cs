using AssembleAI.Platform.Authorization;

namespace AssembleAI.Platform.Knowledge;

public sealed class KnowledgeOptions
{
    public bool Enabled { get; set; }
    public bool WorkerEnabled { get; set; }
    public string ParserEndpoint { get; set; } = "";
    public string ParserKeyEnvironmentVariable { get; set; } = "ASSEMBLEAI_KNOWLEDGE_PARSER_KEY";
    public bool AllowInsecureLoopback { get; set; }
    public const int MaximumBytes = 5 * 1024 * 1024;
}

public static class KnowledgeAuthorization
{
    public static bool Allows(AccessSnapshot? access, string permission, Guid tenant, Guid? organization, Guid? document = null) =>
        PermissionEvaluator.Allows(access, permission, new(tenant, "knowledge", "documents", organization))
        || document is { } id && PermissionEvaluator.Allows(access, permission, new(tenant, "document", id.ToString("N"), organization));
}
