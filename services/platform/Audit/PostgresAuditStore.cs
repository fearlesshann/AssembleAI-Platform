using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Identity;
using Npgsql;
using NpgsqlTypes;

namespace AssembleAI.Platform.Audit;

/// <summary>Live-authorized, parameterized history projections. No provider content or gateway paths are selected.</summary>
public sealed class PostgresAuditStore(IConfiguration configuration, IAuthorizationStore authorization, AuditCursor cursors, TimeProvider clock)
{
    private sealed record Scope(Guid User, Guid? Tenant, bool Identity, bool Developer, bool Reviewer, bool GlobalAvailable,
        bool Export, bool Knowledge, string[] Models, string[] Connectors, string[] Registry, string[] Gateway, long Version)
    {
        [JsonIgnore] public string Stamp => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this))));
        [JsonIgnore] public string[] Sources => AuditQuery.Sources.Where(source => source switch {
            "identity" => Identity, "registry" => Tenant is null ? Developer || Reviewer : Registry.Length > 0,
            "model" or "model-configuration" => Models.Length > 0, "connector" or "connector-configuration" => Connectors.Length > 0,
            "gateway" => Gateway.Length > 0, "knowledge-search" or "knowledge-document" => Knowledge, "audit-export" => Export, _ => false }).ToArray();
    }
    private async Task<Scope> ReadScope(NpgsqlConnection db, SubjectIdentity subject, Guid? tenant, CancellationToken token)
    {
        Guid user; bool global; bool developer; bool reviewer; bool tenantAdmin;
        await using (var cmd = new NpgsqlCommand("""
            SELECT u.user_id,
                EXISTS(SELECT 1 FROM public.platform_identity_administrators a WHERE a.user_id=u.user_id AND a.enabled),
                EXISTS(SELECT 1 FROM public.platform_registry_operators o WHERE o.user_id=u.user_id AND o.role='developer' AND o.enabled),
                EXISTS(SELECT 1 FROM public.platform_registry_operators o WHERE o.user_id=u.user_id AND o.role='reviewer' AND o.enabled),
                EXISTS(SELECT 1 FROM public.tenant_identity_administrators a WHERE a.user_id=u.user_id AND a.tenant_id=@tenant AND a.enabled)
            FROM public.platform_users u WHERE u.issuer=@issuer AND u.subject=@subject AND u.enabled
            """, db))
        {
            cmd.Parameters.AddWithValue("issuer", subject.Issuer); cmd.Parameters.AddWithValue("subject", subject.Subject);
            cmd.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, (object?)tenant ?? DBNull.Value);
            await using var r = await cmd.ExecuteReaderAsync(token);
            if (!await r.ReadAsync(token)) throw new AuditRequestException(403);
            user=r.GetGuid(0); global=r.GetBoolean(1); developer=r.GetBoolean(2); reviewer=r.GetBoolean(3); tenantAdmin=r.GetBoolean(4);
        }
        if (tenant is null)
        {
            if (!global && !developer && !reviewer) throw new AuditRequestException(403);
            var owned = new List<string>();
            if (developer && !reviewer)
            {
                await using var cmd = new NpgsqlCommand("SELECT app_id FROM public.platform_applications WHERE owner_user_id=@user ORDER BY app_id COLLATE \"C\" LIMIT 10001", db);
                cmd.Parameters.AddWithValue("user", user); await using var reader = await cmd.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) owned.Add(reader.GetString(0));
                if (owned.Count > 10000) throw new AuditRequestException(503);
            }
            return new(user,null,global,developer,reviewer,true,false,false,[],[],owned.ToArray(),[],0);
        }
        var access = await authorization.ReadAccessAsync(subject,tenant.Value,token);
        if (access is null || access.UserId != user || access.TenantId != tenant || access.AuthorizationVersion < 1) throw new AuditRequestException(403);
        string[] Ids(string permission,string type) => access.Grants.Where(g => g.Permission==permission && g.ResourceType==type && g.TenantWide)
            .Select(g=>g.ResourceId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        bool Allows(string permission,string type,string id) => PermissionEvaluator.Allows(access,permission,new(tenant.Value,type,id,null));
        return new(user,tenant,global||tenantAdmin,developer,reviewer,global||developer||reviewer,
            Allows("audit:export","audit","events"),Allows("audit:read","audit","knowledge"),Ids("model:manage","model"),Ids("datasource:manage","datasource"),
            Ids("registry:manage","application"),Ids("gateway:observe","application"),access.AuthorizationVersion);
    }
    private static string Binding(LiveBffSession session, Scope scope, AuditQuery query) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        session.Subject.Issuer + "\n" + session.Subject.Subject + "\n" + scope.Stamp + "\n" + query.Fingerprint())));
    private async Task Check(NpgsqlConnection db, LiveBffSession session, Scope scope, CancellationToken token)
    {
        if ((await ReadScope(db,session.Subject,scope.Tenant,token)).Stamp != scope.Stamp || !await session.CheckAsync(token)) throw new AuditRequestException(403);
    }
    public Task<AuditWorkspace> Workspace(LiveBffSession session, Guid? tenant, CancellationToken token) => Run(async (db,ct) => {
        var scope = await ReadScope(db,session.Subject,tenant,ct);
        await Check(db,session,scope,ct);
        return new AuditWorkspace(tenant is null ? "global" : "tenant",tenant,scope.User,scope.Sources,tenant.HasValue&&scope.Export,scope.GlobalAvailable,new());
    },token);
    public Task<AuditPage> Events(LiveBffSession session, Guid? tenant, AuditQuery query, string? source, string? id, CancellationToken token) => Run(async (db,ct) => {
        var scope=await ReadScope(db,session.Subject,tenant,ct);
        if (source is not null && !scope.Sources.Contains(source)) throw new AuditRequestException(404);
        var binding=Binding(session,scope,query);
        var after=query.Cursor is null ? null : cursors.Decode(query.Cursor,binding);
        var rows=await Read(db,scope,query,source,id,after,query.PageSize+1,ct);
        await Check(db,session,scope,ct);
        var hasMore=rows.Count>query.PageSize;
        if (hasMore) rows.RemoveAt(rows.Count-1);
        var next=hasMore ? cursors.Encode(binding,new(rows[^1].OccurredAt,rows[^1].Source,rows[^1].Id)) : null;
        return new AuditPage(rows,next);
    },token);
    public Task<AuditExport> Export(LiveBffSession session, Guid? tenant, AuditQuery query, CancellationToken token) => Run(async (db,ct) => {
        var scope=await ReadScope(db,session.Subject,tenant,ct);
        if (tenant is null || !scope.Export) throw new AuditRequestException(403);
        var rows=await Read(db,scope,query,null,null,null,1001,ct);
        var truncated=rows.Count>1000; if (truncated) rows.RemoveAt(1000);
        await using var tx=await db.BeginTransactionAsync(ct);
        await Check(db,session,scope,ct);
        var time=clock.GetUtcNow();
        await using (var cmd=new NpgsqlCommand("""
            INSERT INTO public.platform_audit_exports(event_id,tenant_id,actor_user_id,request_id,record_count,truncated,occurred_at)
            VALUES(@id,@tenant,@actor,@request,@count,@truncated,@time)
            """,db,tx))
        {
            cmd.Parameters.AddWithValue("id",Guid.NewGuid()); cmd.Parameters.AddWithValue("tenant",tenant.Value); cmd.Parameters.AddWithValue("actor",scope.User);
            cmd.Parameters.AddWithValue("request",Guid.NewGuid().ToString("N")); cmd.Parameters.AddWithValue("count",rows.Count);
            cmd.Parameters.AddWithValue("truncated",truncated); cmd.Parameters.AddWithValue("time",time);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await Check(db,session,scope,ct);
        await tx.CommitAsync(ct);
        // No response may leave after a late revocation, even after a durable export admission.
        await Check(db,session,scope,ct);
        return new AuditExport(rows,truncated,time);
    },token);

    private static async Task<List<AuditEvent>> Read(NpgsqlConnection db, Scope scope, AuditQuery query, string? detailSource, string? detailId,
        AuditPosition? after,int take,CancellationToken token)
    {
        if (query.Source is not null && !scope.Sources.Contains(query.Source)) throw new AuditRequestException(403);
        var sources=scope.Sources.Where(s=>(query.Source is null||s==query.Source)&&(detailSource is null||s==detailSource)).ToArray();
        if (sources.Length==0) return [];
        // Only fixed, server-owned projections/identifiers below are composed. Every request value is a parameter.
        var sql="SELECT * FROM ("+string.Join(" UNION ALL ",sources.Select(s=>Projection(s,scope)))+") e WHERE occurred_at>=@from AND occurred_at<@to "
            +"AND (@status IS NULL OR status=@status) AND (@app IS NULL OR app_id=@app) AND (@request IS NULL OR request_id=@request OR gateway_request_id=@request) "
            +"AND (@detail IS NULL OR id=@detail) AND (@aftertime IS NULL OR (occurred_at,source COLLATE \"C\",id COLLATE \"C\") < (@aftertime,@aftersource COLLATE \"C\",@afterid COLLATE \"C\")) "
            +"ORDER BY occurred_at DESC,source COLLATE \"C\" DESC,id COLLATE \"C\" DESC LIMIT @take";
        await using var cmd=new NpgsqlCommand(sql,db) {CommandTimeout=3};
        void Add(string name,NpgsqlDbType type,object? value)=>cmd.Parameters.AddWithValue(name,type,value??DBNull.Value);
        Add("tenant",NpgsqlDbType.Uuid,scope.Tenant); Add("from",NpgsqlDbType.TimestampTz,query.From); Add("to",NpgsqlDbType.TimestampTz,query.To);
        Add("status",NpgsqlDbType.Text,query.Status); Add("app",NpgsqlDbType.Text,query.AppId); Add("request",NpgsqlDbType.Text,query.RequestId);
        Add("detail",NpgsqlDbType.Text,detailId); Add("aftertime",NpgsqlDbType.TimestampTz,after?.Time);
        Add("aftersource",NpgsqlDbType.Text,after?.Source); Add("afterid",NpgsqlDbType.Text,after?.Id); Add("take",NpgsqlDbType.Integer,take);
        Add("models",NpgsqlDbType.Array|NpgsqlDbType.Text,scope.Models); Add("connectors",NpgsqlDbType.Array|NpgsqlDbType.Text,scope.Connectors);
        Add("registry",NpgsqlDbType.Array|NpgsqlDbType.Text,scope.Registry); Add("gateway",NpgsqlDbType.Array|NpgsqlDbType.Text,scope.Gateway);
        var rows=new List<AuditEvent>(); await using var r=await cmd.ExecuteReaderAsync(token);
        while(await r.ReadAsync(token))
        {
            var details=JsonSerializer.Deserialize<Dictionary<string,string?>>(r.GetString(15))??throw new AuditRequestException(503);
            rows.Add(new(r.GetString(0),r.GetString(1),r.IsDBNull(2)?null:r.GetGuid(2),r.GetGuid(3),r.GetString(4),r.GetString(5),r.GetString(6),r.GetString(7),
                r.GetString(8),r.GetString(9),r.GetFieldValue<DateTimeOffset>(10),r.IsDBNull(11)?null:r.GetString(11),r.IsDBNull(12)?null:r.GetString(12),
                r.IsDBNull(13)?null:r.GetGuid(13),r.IsDBNull(14)?null:r.GetString(14),details));
        }
        return rows;
    }
    private static string Projection(string source,Scope scope)
    {
        var global=scope.Tenant is null;
        var isCall=source is "gateway" or "model" or "connector" or "knowledge-search";
        var table=source switch { "gateway"=>"platform_gateway_calls", "model"=>"platform_model_calls", "connector"=>"platform_connector_calls",
            "knowledge-search"=>"platform_knowledge_searches", "registry"=>"platform_registry_audit", "identity"=>"platform_identity_audit",
            "model-configuration"=>"platform_model_configuration_audit", "connector-configuration"=>"platform_connector_configuration_audit",
            "knowledge-document"=>"platform_knowledge_audit", "audit-export"=>"platform_audit_exports", _=>throw new AuditRequestException(400) };
        var id=source=="gateway"?"request_id":"replace("+(isCall?"call_id":"event_id")+"::text,'-','')";
        var actor=isCall?"user_id":"actor_user_id";
        var resourceType=source switch { "gateway" or "registry"=>"'application'", "model" or "model-configuration"=>"'model'",
            "connector" or "connector-configuration"=>"'datasource'", "identity"=>"entity_type", "knowledge-document"=>"'document'", "knowledge-search"=>"'knowledge'", _=>"'audit'" };
        var resource=source switch { "gateway" or "registry"=>"app_id", "model" or "model-configuration"=>"model_id",
            "connector" or "connector-configuration"=>"source_id", "identity"=>"entity_id::text", "knowledge-document"=>"document_id::text", "knowledge-search"=>"'documents'", _=>"'events'" };
        var action=source switch { "gateway"=>"method", "connector"=>"operation", "model"=>"'chat'", "knowledge-search"=>"'search'",
            "model-configuration" or "connector-configuration"=>"'configure'", "audit-export"=>"'export'", _=>"action" };
        var outcome=isCall?"outcome":source is "registry" or "identity"?"result":"'success'";
        var success=source=="gateway"?"outcome='transport_completed' AND http_status BETWEEN 100 AND 399":isCall?"outcome='completed'":$"{outcome}='success'";
        var status=$"CASE WHEN {outcome}='running' THEN 'unfinished' WHEN {success} THEN 'success' ELSE 'failure' END";
        var provenance=isCall||source=="registry";
        var app=provenance?"app_id":"NULL::text"; var release=provenance?"release_version":"NULL::text";
        var run=isCall?"run_id":"NULL::uuid"; var gateway=source=="gateway"?"request_id":isCall?"gateway_request_id":"NULL::text";
        var details=source switch {
            "gateway"=>"jsonb_build_object('elapsedMilliseconds',elapsed_milliseconds::text,'httpStatus',http_status::text)",
            "model"=>"jsonb_build_object('elapsedMilliseconds',elapsed_milliseconds::text,'promptTokens',prompt_tokens::text,'completionTokens',completion_tokens::text,'totalTokens',total_tokens::text)",
            "connector"=>"jsonb_build_object('elapsedMilliseconds',elapsed_milliseconds::text,'rowCount',row_count::text,'datasetId',dataset_id)",
            "knowledge-search"=>"jsonb_build_object('elapsedMilliseconds',elapsed_milliseconds::text,'hitCount',hit_count::text)",
            "model-configuration"=>"jsonb_build_object('revision',revision::text,'enabled',enabled::text,'maxOutputTokens',max_output_tokens::text)",
            "connector-configuration"=>"jsonb_build_object('revision',revision::text,'enabled',enabled::text,'maxRows',max_rows::text,'timeoutSeconds',timeout_seconds::text)",
            "knowledge-document"=>"jsonb_build_object('revision',revision::text)",
            "audit-export"=>"jsonb_build_object('recordCount',record_count::text,'truncated',truncated::text)", _=>"'{}'::jsonb" };
        var permission=source switch { "model" or "model-configuration"=>" AND model_id=ANY(@models)", "connector" or "connector-configuration"=>" AND source_id=ANY(@connectors)",
            "gateway"=>" AND app_id=ANY(@gateway)", "registry"=>global&&scope.Reviewer?"":" AND app_id=ANY(@registry)", _=>"" };
        return $"SELECT {id}::text AS id,'{source}'::text AS source,tenant_id,{actor} AS actor_user_id,{resourceType}::text AS resource_type,{resource}::text AS resource_id,{action}::text AS action,{outcome}::text AS outcome,{status} AS status,request_id,{(isCall?"started_at":"occurred_at")} AS occurred_at,{app} AS app_id,{release} AS release_version,{run} AS run_id,{gateway} AS gateway_request_id,{details}::text AS details FROM public.{table} WHERE {(global?"tenant_id IS NULL":"tenant_id=@tenant")}{permission}";
    }
    private async Task<T> Run<T>(Func<NpgsqlConnection,CancellationToken,Task<T>> action,CancellationToken token)
    {
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(token); budget.CancelAfter(TimeSpan.FromSeconds(5));
        try {
            var value=configuration.GetConnectionString("Platform"); if(string.IsNullOrWhiteSpace(value)) throw new AuditRequestException(503);
            var settings=new NpgsqlConnectionStringBuilder(value){Timeout=3,CommandTimeout=3,IncludeErrorDetail=false};
            await using var db=new NpgsqlConnection(settings.ConnectionString); await db.OpenAsync(budget.Token);
            return await action(db,budget.Token);
        }
        catch(Exception e) when(e is NpgsqlException or IOException or TimeoutException or JsonException or ArgumentException or InvalidOperationException or AuthorizationStoreUnavailableException
            || e is OperationCanceledException && !token.IsCancellationRequested) { throw new AuditRequestException(503); }
    }
}
