using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Models;
using AssembleAI.Platform.Registry;
using Npgsql;
using NpgsqlTypes;

namespace AssembleAI.Platform.Knowledge;

public sealed partial class PostgresKnowledgeStore
{
    // Both resource and organization predicates are evaluated on the same grant row.
    private const string PermissionSql="""
        EXISTS(SELECT 1 FROM public.tenant_member_roles mr
            JOIN public.tenant_roles r ON r.tenant_id=mr.tenant_id AND r.role_id=mr.role_id AND r.enabled
            JOIN public.role_permission_grants g ON g.tenant_id=r.tenant_id AND g.role_id=r.role_id AND g.enabled
            WHERE mr.tenant_id=d.tenant_id AND mr.user_id=$2 AND g.permission=ANY($3::text[])
                AND ((g.resource_type='knowledge' AND g.resource_id='documents')
                    OR (g.resource_type='document' AND g.resource_id=replace(d.document_id::text,'-','')))
                AND (g.tenant_wide OR (d.organization_id IS NOT NULL AND EXISTS(
                    SELECT 1 FROM public.grant_organizations go WHERE go.tenant_id=g.tenant_id
                        AND go.grant_id=g.grant_id AND go.organization_id=d.organization_id))))
        """;
    private const string EnabledDocumentSql="""
        (d.organization_id IS NULL OR EXISTS(SELECT 1 FROM public.tenant_organizations o
            WHERE o.tenant_id=d.tenant_id AND o.organization_id=d.organization_id AND o.enabled))
        """;
    private static async Task<bool> Manage(NpgsqlConnection db,NpgsqlTransaction tx,KnowledgeDocument document,CancellationToken token)
    {
        await using var command=Command(db,tx,$"""
            SELECT EXISTS(SELECT 1 FROM public.knowledge_documents d
                JOIN public.platform_tenants t ON t.tenant_id=d.tenant_id AND t.enabled
                JOIN public.tenant_memberships m ON m.tenant_id=t.tenant_id AND m.user_id=$2 AND m.enabled
                JOIN public.platform_users u ON u.user_id=m.user_id AND u.enabled
                WHERE d.tenant_id=$1 AND d.document_id=$4 AND d.status<>'deleted' AND {EnabledDocumentSql} AND {PermissionSql})
            """,document.TenantId,document.CreatedBy,new[]{"knowledge:manage"},document.DocumentId);
        return await command.ExecuteScalarAsync(token) is true;
    }
    public Task<IReadOnlyList<KnowledgeChunk>> ReadIndexBatchAsync(KnowledgeJobLease lease,CancellationToken token)=>Run<IReadOnlyList<KnowledgeChunk>>(async(db,tx,ct)=>
    {
        var document=await LockLease(db,tx,lease,"embed",ct);
        if(document is null || !await Manage(db,tx,document,ct)) return [];
        return await IndexBatch(db,tx,lease,ct);
    },token);
    private static async Task<IReadOnlyList<KnowledgeChunk>> IndexBatch(NpgsqlConnection db,NpgsqlTransaction tx,KnowledgeJobLease lease,CancellationToken token)
    {
        await using var command=Command(db,tx,"""
            SELECT chunk_index,content,locator FROM public.knowledge_chunks
            WHERE tenant_id=$1 AND document_id=$2 AND document_revision=$3 AND chunk_index >= $4
            ORDER BY chunk_index LIMIT 8
            """,lease.Job.TenantId,lease.Job.DocumentId,lease.Job.DocumentRevision,lease.Job.NextChunkIndex);
        await using var reader=await command.ExecuteReaderAsync(token); var chunks=new List<KnowledgeChunk>();
        while(await reader.ReadAsync(token)) chunks.Add(new(reader.GetInt32(0),reader.GetString(1),reader.GetString(2)));
        return chunks;
    }
    public Task<bool> CompleteIndexBatchAsync(KnowledgeJobLease lease,IReadOnlyList<KnowledgeSegment> segments,CancellationToken token)=>Run(async(db,tx,ct)=>
    {
        var document=await LockLease(db,tx,lease,"embed",ct);
        if(document is null) return false;
        if(!await Manage(db,tx,document,ct))
        {
            await EndJob(db,tx,lease.Job.JobId,"failed",ct);
            await Write(db,tx,"UPDATE public.knowledge_documents SET status='failed',failure_code='ingestion_failed' WHERE tenant_id=$1 AND document_id=$2",ct,document.TenantId,document.DocumentId);
            return false;
        }
        var parents=await IndexBatch(db,tx,lease,ct);
        if(!KnowledgeIndexProfile.ValidSegments(parents,segments) || parents[0].Index!=lease.Job.NextChunkIndex
            || parents.Where((parent,i)=>parent.Index!=lease.Job.NextChunkIndex+i).Any()) throw new KnowledgeStoreUnavailableException();
        await using(var count=Command(db,tx,"SELECT count(*) FROM public.knowledge_embeddings WHERE tenant_id=$1 AND document_id=$2",document.TenantId,document.DocumentId))
            if((long)(await count.ExecuteScalarAsync(ct))!+segments.Count>KnowledgeIndexProfile.MaximumSegments) throw new KnowledgeEmbeddingException("index_too_large");
        await using(var batch=new NpgsqlBatch(db,tx))
        {
            foreach(var segment in segments)
            {
                var command=new NpgsqlBatchCommand("""
                    INSERT INTO public.knowledge_embeddings(tenant_id,document_id,document_revision,chunk_index,segment_index,profile,content,locator,embedding)
                    VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9)
                    """);
                foreach(var value in new object[]{document.TenantId,document.DocumentId,document.Revision,segment.ChunkIndex,segment.SegmentIndex,KnowledgeIndexProfile.Id,segment.Text,segment.Locator,segment.Embedding}) command.Parameters.AddWithValue(value);
                batch.BatchCommands.Add(command);
            }
            await batch.ExecuteNonQueryAsync(ct);
        }
        var next=parents[^1].Index+1;
        await using var remaining=Command(db,tx,"SELECT EXISTS(SELECT 1 FROM public.knowledge_chunks WHERE tenant_id=$1 AND document_id=$2 AND document_revision=$3 AND chunk_index >= $4)",document.TenantId,document.DocumentId,document.Revision,next);
        if(await remaining.ExecuteScalarAsync(ct) is true)
            await Write(db,tx,"UPDATE public.knowledge_jobs SET status='queued',attempts=0,next_chunk_index=$2,lease_id=NULL,lease_until=NULL,available_at=now() WHERE job_id=$1",ct,lease.Job.JobId,next);
        else
        {
            await Write(db,tx,"UPDATE public.knowledge_jobs SET next_chunk_index=$2 WHERE job_id=$1",ct,lease.Job.JobId,next);
            await EndJob(db,tx,lease.Job.JobId,"completed",ct);
            await Write(db,tx,"UPDATE public.knowledge_documents SET status='ready',failure_code=NULL WHERE tenant_id=$1 AND document_id=$2",ct,document.TenantId,document.DocumentId);
        }
        return true;
    },token);

    public Task<IReadOnlyList<KnowledgeHit>> SearchAsync(AccessSnapshot access,float[] embedding,int limit,CancellationToken token)=>Run<IReadOnlyList<KnowledgeHit>>(async(db,tx,ct)=>
    {
        if(access is null || access.TenantId==Guid.Empty || access.UserId==Guid.Empty || access.AuthorizationVersion<1
            || limit is <1 or >5 || !KnowledgeIndexProfile.ValidVector(embedding)) throw new KnowledgeStoreUnavailableException();
        await using var command=Command(db,tx,$"""
            WITH authorized AS MATERIALIZED(
                SELECT e.*,d.file_name FROM public.knowledge_embeddings e
                JOIN public.knowledge_documents d ON d.tenant_id=e.tenant_id AND d.document_id=e.document_id
                    AND d.revision=e.document_revision AND d.status='ready' AND d.index_profile=e.profile
                JOIN public.platform_tenants t ON t.tenant_id=d.tenant_id AND t.enabled AND t.authorization_version=$5
                JOIN public.tenant_memberships m ON m.tenant_id=t.tenant_id AND m.user_id=$2 AND m.enabled
                JOIN public.platform_users u ON u.user_id=m.user_id AND u.enabled
                WHERE d.tenant_id=$1 AND e.profile=$6 AND {EnabledDocumentSql} AND {PermissionSql}
            )
            SELECT document_id,document_revision,chunk_index,segment_index,file_name,locator,content,
                1-(embedding::public.vector(384) OPERATOR(public.<=>) $4::real[]::public.vector(384)) AS score
            FROM authorized ORDER BY score DESC,document_id,chunk_index,segment_index LIMIT $7
            """,access.TenantId,access.UserId,new[]{"knowledge:read","knowledge:manage"},embedding,access.AuthorizationVersion,KnowledgeIndexProfile.Id,limit);
        await using var reader=await command.ExecuteReaderAsync(ct); var hits=new List<KnowledgeHit>();
        while(await reader.ReadAsync(ct))
        {
            var document=reader.GetGuid(0); var revision=reader.GetInt64(1); var score=reader.GetDouble(7);
            if(!double.IsFinite(score)) throw new KnowledgeStoreUnavailableException();
            hits.Add(new($"{document:N}-{revision}-{reader.GetInt32(2)}-{reader.GetInt32(3)}",document,revision,
                reader.GetString(4),reader.GetString(5),reader.GetString(6),Math.Clamp(score,-1,1)));
        }
        return hits;
    },token);
    public Task<bool> CheckDocumentsAsync(AccessSnapshot access,IReadOnlyList<KnowledgeDocumentReference> documents,CancellationToken token)=>Run(async(db,tx,ct)=>
    {
        if(access is null || access.UserId==Guid.Empty || access.TenantId==Guid.Empty || access.AuthorizationVersion<1
            || documents is null || documents.Count>5 || documents.Any(d=>d is null || d.DocumentId==Guid.Empty || d.Revision<1)
            || documents.Select(d=>d.DocumentId).Distinct().Count()!=documents.Count) throw new KnowledgeStoreUnavailableException();
        await using var command=Command(db,tx,$"""
            SELECT EXISTS(SELECT 1 FROM public.platform_tenants t
                JOIN public.tenant_memberships m ON m.tenant_id=t.tenant_id AND m.user_id=$2 AND m.enabled
                JOIN public.platform_users u ON u.user_id=m.user_id AND u.enabled
                WHERE t.tenant_id=$1 AND t.enabled AND t.authorization_version=$6)
            AND NOT EXISTS(SELECT 1 FROM unnest($4::uuid[],$5::bigint[]) requested(document_id,revision)
                WHERE NOT EXISTS(SELECT 1 FROM public.knowledge_documents d
                    WHERE d.tenant_id=$1 AND d.document_id=requested.document_id AND d.revision=requested.revision
                        AND d.status='ready' AND d.index_profile=$7 AND {EnabledDocumentSql} AND {PermissionSql}))
            """,access.TenantId,access.UserId,new[]{"knowledge:read","knowledge:manage"},documents.Select(d=>d.DocumentId).ToArray(),
                documents.Select(d=>d.Revision).ToArray(),access.AuthorizationVersion,KnowledgeIndexProfile.Id);
        return await command.ExecuteScalarAsync(ct) is true;
    },token);

    public Task<Guid> BeginSearchAsync(ModelCaller caller,string requestId,long authorizationVersion,CancellationToken token)=>Run(async(db,tx,ct)=>
    {
        if(caller.TenantId==Guid.Empty || caller.UserId==Guid.Empty || !Hex(requestId,32) || authorizationVersion<1
            || caller.Source is {} source && (!ManifestValidator.IsAppId(source.AppId) || !ManifestValidator.IsVersion(source.ReleaseVersion)
                || source.RunId==Guid.Empty || !Hex(source.GatewayRequestId,32))) throw new KnowledgeStoreUnavailableException();
        var id=Guid.NewGuid();
        await using var command=Command(db,tx,"""
            INSERT INTO public.platform_knowledge_searches(call_id,tenant_id,user_id,request_id,authorization_version,profile,outcome,app_id,release_version,run_id,gateway_request_id)
            VALUES($1,$2,$3,$4,$5,$6,'running',$7,$8,$9,$10)
            """,id,caller.TenantId,caller.UserId,requestId,authorizationVersion,KnowledgeIndexProfile.Id);
        command.Parameters.Add(new NpgsqlParameter{NpgsqlDbType=NpgsqlDbType.Text,Value=(object?)caller.Source?.AppId??DBNull.Value});
        command.Parameters.Add(new NpgsqlParameter{NpgsqlDbType=NpgsqlDbType.Text,Value=(object?)caller.Source?.ReleaseVersion??DBNull.Value});
        command.Parameters.Add(new NpgsqlParameter{NpgsqlDbType=NpgsqlDbType.Uuid,Value=(object?)caller.Source?.RunId??DBNull.Value});
        command.Parameters.Add(new NpgsqlParameter{NpgsqlDbType=NpgsqlDbType.Text,Value=(object?)caller.Source?.GatewayRequestId??DBNull.Value});
        await command.ExecuteNonQueryAsync(ct); return id;
    },token);
    public Task CompleteSearchAsync(Guid callId,string outcome,int count,long elapsedMilliseconds,CancellationToken token)=>Run(async(db,tx,ct)=>
    {
        if(callId==Guid.Empty || outcome is not("completed" or "revoked" or "cancelled" or "timeout" or "failed")
            || count is <0 or >5 || elapsedMilliseconds<0) throw new KnowledgeStoreUnavailableException();
        await using var command=Command(db,tx,"""
            UPDATE public.platform_knowledge_searches SET outcome=$2,hit_count=$3,elapsed_milliseconds=$4,finished_at=now()
            WHERE call_id=$1 AND outcome='running' AND finished_at IS NULL
            """,callId,outcome,count,elapsedMilliseconds);
        if(await command.ExecuteNonQueryAsync(ct)!=1) throw new KnowledgeStoreUnavailableException();
        return true;
    },token);
}
