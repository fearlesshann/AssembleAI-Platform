using Npgsql;
using NpgsqlTypes;
using AssembleAI.Platform.Authorization;

namespace AssembleAI.Platform.Knowledge;

public sealed class PostgresKnowledgeStore(IConfiguration configuration) : IKnowledgeStore, IKnowledgeJobStore
{
    private const string Columns = "tenant_id,document_id,file_name,media_type,size_bytes,sha256,organization_id,status,revision,created_by,created_at,failure_code";
    public Task<KnowledgeDocument> CreateAsync(KnowledgeDocument document, string requestId, CancellationToken token) => Run(async (db, tx, ct) =>
    {
        Validate(document.TenantId, document.DocumentId);
        if (document.Status != "queued" || document.Revision != 1 || document.CreatedBy == Guid.Empty || document.FailureCode is not null
            || document.FileName is not {Length: >=1 and <=255} || string.IsNullOrWhiteSpace(document.FileName)
            || document.FileName.Any(char.IsControl) || document.FileName.IndexOfAny(['/', '\\', ':']) >= 0
            || document.MediaType is not ("text/plain" or "text/markdown" or "application/pdf" or "application/vnd.openxmlformats-officedocument.wordprocessingml.document")
            || document.SizeBytes is <1 or >5242880 || !Hex(document.Sha256,64) || !Hex(requestId,32)
            || document.OrganizationId == Guid.Empty) throw new KnowledgeStoreUnavailableException();
        if (!await Active(db,tx,document.TenantId,document.CreatedBy,document.OrganizationId,ct)) throw new KnowledgeStoreUnavailableException();
        await using var insert = Command(db,tx,$"""
            INSERT INTO public.knowledge_documents({Columns}) VALUES($1,$2,$3,$4,$5,$6,$7,'queued',1,$8,now(),NULL)
            ON CONFLICT(tenant_id,document_id) DO NOTHING RETURNING {Columns}
            """, document.TenantId,document.DocumentId,document.FileName,document.MediaType,document.SizeBytes,document.Sha256);
        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType=NpgsqlDbType.Uuid,Value=(object?)document.OrganizationId??DBNull.Value });
        insert.Parameters.AddWithValue(document.CreatedBy);
        KnowledgeDocument? created;
        await using (var reader = await insert.ExecuteReaderAsync(ct)) created = await reader.ReadAsync(ct) ? Read(reader) : null;
        if (created is null)
        {
            var existing = await Find(db,tx,document.TenantId,document.DocumentId,ct);
            if (existing is null || existing.Status=="deleted" || existing.FileName!=document.FileName || existing.MediaType!=document.MediaType
                || existing.SizeBytes!=document.SizeBytes || existing.Sha256!=document.Sha256 || existing.OrganizationId!=document.OrganizationId
                || existing.CreatedBy!=document.CreatedBy) throw new KnowledgeStoreUnavailableException();
            return existing;
        }
        await Enqueue(db,tx,created,"ingest",ct);
        await Audit(db,tx,created,document.CreatedBy,"create",requestId,ct);
        return created;
    },token);

    public Task<KnowledgeDocument?> ReadAsync(Guid tenant, Guid document, CancellationToken token) => Run(async (db,tx,ct) =>
    { Validate(tenant,document); return await Find(db,tx,tenant,document,ct); }, token);

    public Task<IReadOnlyList<KnowledgeDocument>> ListAsync(Guid tenant, CancellationToken token) => Run<IReadOnlyList<KnowledgeDocument>>(async (db,tx,ct) =>
    {
        if (tenant==Guid.Empty) throw new KnowledgeStoreUnavailableException();
        await using var command=Command(db,tx,$"SELECT {Columns} FROM public.knowledge_documents WHERE tenant_id=$1 AND status<>'deleted' ORDER BY created_at DESC,document_id DESC LIMIT 100",tenant);
        await using var reader=await command.ExecuteReaderAsync(ct); var items=new List<KnowledgeDocument>();
        while (await reader.ReadAsync(ct)) items.Add(Read(reader));
        return items;
    },token);

    public Task<bool> DeleteAsync(Guid tenant, Guid document, long expectedRevision, Guid actor, string requestId, CancellationToken token) => Run(async (db,tx,ct) =>
    {
        Validate(tenant,document);
        if (expectedRevision is <1 or long.MaxValue || actor==Guid.Empty || !Hex(requestId,32)) throw new KnowledgeStoreUnavailableException();
        var current=await Find(db,tx,tenant,document,ct,locked:true);
        if (current is null || current.Status=="deleted" || current.Revision!=expectedRevision) return false;
        if (!await Active(db,tx,tenant,actor,current.OrganizationId,ct)) throw new KnowledgeStoreUnavailableException();
        await Write(db,tx,"UPDATE public.knowledge_documents SET status='deleted',revision=revision+1 WHERE tenant_id=$1 AND document_id=$2",ct,tenant,document);
        await Write(db,tx,"UPDATE public.knowledge_jobs SET status='cancelled',lease_id=NULL,lease_until=NULL WHERE tenant_id=$1 AND document_id=$2 AND kind='ingest' AND status IN ('queued','running')",ct,tenant,document);
        await Write(db,tx,"DELETE FROM public.knowledge_chunks WHERE tenant_id=$1 AND document_id=$2",ct,tenant,document);
        var deleted=current with { Status="deleted",Revision=current.Revision+1 };
        await Enqueue(db,tx,deleted,"purge",ct);
        await Audit(db,tx,deleted,actor,"delete",requestId,ct);
        return true;
    },token);

    public Task<IReadOnlyList<KnowledgeJob>> PendingAsync(CancellationToken token) => Run<IReadOnlyList<KnowledgeJob>>(async (db,tx,ct) =>
    {
        await using var command=Command(db,tx,"""
            SELECT job_id,tenant_id,document_id,kind,document_revision,status,attempts FROM public.knowledge_jobs
            WHERE (status='queued' AND available_at<=now()) OR (status='running' AND lease_until<=now())
            ORDER BY available_at,created_at,job_id LIMIT 100
            """);
        await using var reader=await command.ExecuteReaderAsync(ct); var items=new List<KnowledgeJob>();
        while(await reader.ReadAsync(ct)) items.Add(ReadJob(reader)); return items;
    },token);

    public Task<SubjectIdentity?> ReadCreatorAsync(Guid userId,CancellationToken token) => Run<SubjectIdentity?>(async (db,tx,ct) =>
    {
        if(userId==Guid.Empty) throw new KnowledgeStoreUnavailableException();
        await using var command=Command(db,tx,"SELECT issuer,subject FROM public.platform_users WHERE user_id=$1 AND enabled",userId);
        await using var reader=await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)?new(reader.GetString(0),reader.GetString(1)):null;
    },token);

    public Task<KnowledgeJobLease?> ClaimAsync(Guid jobId,CancellationToken token) => Run<KnowledgeJobLease?>(async (db,tx,ct) =>
    {
        if (jobId==Guid.Empty) throw new KnowledgeStoreUnavailableException();
        var job=await FindJob(db,tx,jobId,ct);
        if (job is null) return null;
        var document=await Find(db,tx,job.TenantId,job.DocumentId,ct,locked:true);
        await using var command=Command(db,tx,"""
            SELECT job_id,tenant_id,document_id,kind,document_revision,status,attempts FROM public.knowledge_jobs
            WHERE job_id=$1 AND ((status='queued' AND available_at<=now()) OR (status='running' AND lease_until<=now())) FOR UPDATE
            """,jobId);
        await using(var reader=await command.ExecuteReaderAsync(ct)) job=await reader.ReadAsync(ct)?ReadJob(reader):null;
        if(job is null) return null;
        if (document is null || document.Revision!=job.DocumentRevision || (job.Kind=="ingest" ? document.Status=="deleted" : document.Status!="deleted"))
        { await EndJob(db,tx,jobId,"cancelled",ct); return null; }
        if(job.Attempts>=3 || job.Kind=="ingest" && !await Active(db,tx,document.TenantId,document.CreatedBy,document.OrganizationId,ct))
        {
            await EndJob(db,tx,jobId,"failed",ct);
            if(job.Kind=="ingest") await Write(db,tx,"UPDATE public.knowledge_documents SET status='failed',failure_code=COALESCE(failure_code,'ingestion_failed') WHERE tenant_id=$1 AND document_id=$2",ct,job.TenantId,job.DocumentId);
            return null;
        }
        var lease=Guid.NewGuid();
        await Write(db,tx,"UPDATE public.knowledge_jobs SET status='running',attempts=attempts+1,lease_id=$2,lease_until=now()+interval '60 seconds' WHERE job_id=$1",ct,jobId,lease);
        if(job.Kind=="ingest") await Write(db,tx,"UPDATE public.knowledge_documents SET status='processing' WHERE tenant_id=$1 AND document_id=$2",ct,job.TenantId,job.DocumentId);
        return new(job with { Status="running",Attempts=job.Attempts+1 },lease);
    },token);

    public Task<bool> CompleteIngestAsync(KnowledgeJobLease lease,IReadOnlyList<KnowledgeChunk> chunks,CancellationToken token) => Run(async (db,tx,ct) =>
    {
        if(chunks is null || chunks.Count is <1 or >4096
            || chunks.Where((c,i)=>c is null || c.Index!=i || string.IsNullOrWhiteSpace(c.Text) || c.Text.Length>8192
                || string.IsNullOrWhiteSpace(c.Locator) || c.Locator.Length>256).Any()
            || chunks.Sum(c=>(long)c.Text.Length)>8388608) throw new KnowledgeStoreUnavailableException();
        var document=await LockLease(db,tx,lease,"ingest",ct);
        if(document is null) return false;
        if(!await Active(db,tx,document.TenantId,document.CreatedBy,document.OrganizationId,ct))
        {
            await EndJob(db,tx,lease.Job.JobId,"failed",ct);
            await Write(db,tx,"UPDATE public.knowledge_documents SET status='failed',failure_code='ingestion_failed' WHERE tenant_id=$1 AND document_id=$2",ct,document.TenantId,document.DocumentId);
            return false;
        }
        await Write(db,tx,"DELETE FROM public.knowledge_chunks WHERE tenant_id=$1 AND document_id=$2",ct,document.TenantId,document.DocumentId);
        await using(var batch=new NpgsqlBatch(db,tx))
        {
            foreach(var chunk in chunks)
            {
                var insert=new NpgsqlBatchCommand("INSERT INTO public.knowledge_chunks(tenant_id,document_id,document_revision,chunk_index,locator,content) VALUES($1,$2,$3,$4,$5,$6)");
                foreach(var value in new object[]{document.TenantId,document.DocumentId,document.Revision,chunk.Index,chunk.Locator,chunk.Text}) insert.Parameters.AddWithValue(value);
                batch.BatchCommands.Add(insert);
            }
            await batch.ExecuteNonQueryAsync(ct);
        }
        await Write(db,tx,"UPDATE public.knowledge_documents SET status='extracted',failure_code=NULL WHERE tenant_id=$1 AND document_id=$2",ct,document.TenantId,document.DocumentId);
        await EndJob(db,tx,lease.Job.JobId,"completed",ct);
        return true;
    },token);

    public Task<bool> CompletePurgeAsync(KnowledgeJobLease lease,CancellationToken token) => Run(async (db,tx,ct) =>
    {
        if(await LockLease(db,tx,lease,"purge",ct) is null) return false;
        await EndJob(db,tx,lease.Job.JobId,"completed",ct); return true;
    },token);

    public Task<bool> FailAsync(KnowledgeJobLease lease,CancellationToken token,string failureCode="ingestion_failed") => Run(async (db,tx,ct) =>
    {
        var document=await LockLease(db,tx,lease,lease.Job.Kind,ct); if(document is null) return false;
        var code=SafeFailure(failureCode);
        var permanent=lease.Job.Kind=="ingest" && code is not ("ingestion_failed" or "parser_timeout" or "parser_unavailable");
        if(lease.Job.Attempts>=3 || permanent)
        {
            await EndJob(db,tx,lease.Job.JobId,"failed",ct);
            if(lease.Job.Kind=="ingest") await Write(db,tx,"UPDATE public.knowledge_documents SET status='failed',failure_code=$3 WHERE tenant_id=$1 AND document_id=$2",ct,document.TenantId,document.DocumentId,code);
        }
        else
        {
            await Write(db,tx,"UPDATE public.knowledge_jobs SET status='queued',lease_id=NULL,lease_until=NULL,available_at=now()+interval '30 seconds' WHERE job_id=$1",ct,lease.Job.JobId);
            if(lease.Job.Kind=="ingest") await Write(db,tx,"UPDATE public.knowledge_documents SET status='queued',failure_code=$3 WHERE tenant_id=$1 AND document_id=$2",ct,document.TenantId,document.DocumentId,code);
        }
        return true;
    },token);

    private static async Task<KnowledgeDocument?> LockLease(NpgsqlConnection db,NpgsqlTransaction tx,KnowledgeJobLease lease,string kind,CancellationToken token)
    {
        if(lease is null || lease.LeaseId==Guid.Empty || kind is not ("ingest" or "purge") || lease.Job.Kind!=kind) return null;
        var document=await Find(db,tx,lease.Job.TenantId,lease.Job.DocumentId,token,locked:true);
        if(document is null || document.Revision!=lease.Job.DocumentRevision || (kind=="ingest" ? document.Status!="processing" : document.Status!="deleted")) return null;
        await using var command=Command(db,tx,"""
            SELECT job_id FROM public.knowledge_jobs WHERE job_id=$1 AND tenant_id=$2 AND document_id=$3
            AND kind=$4 AND document_revision=$5 AND attempts=$6 AND status='running' AND lease_id=$7 AND lease_until>now() FOR UPDATE
            """,lease.Job.JobId,lease.Job.TenantId,lease.Job.DocumentId,kind,lease.Job.DocumentRevision,lease.Job.Attempts,lease.LeaseId);
        return await command.ExecuteScalarAsync(token) is Guid ? document:null;
    }
    private static async Task<KnowledgeJob?> FindJob(NpgsqlConnection db,NpgsqlTransaction tx,Guid job,CancellationToken token)
    {
        await using var command=Command(db,tx,"SELECT job_id,tenant_id,document_id,kind,document_revision,status,attempts FROM public.knowledge_jobs WHERE job_id=$1",job);
        await using var reader=await command.ExecuteReaderAsync(token); return await reader.ReadAsync(token)?ReadJob(reader):null;
    }
    private static KnowledgeJob ReadJob(NpgsqlDataReader reader)=>new(reader.GetGuid(0),reader.GetGuid(1),reader.GetGuid(2),reader.GetString(3),reader.GetInt64(4),reader.GetString(5),reader.GetInt32(6));
    private static KnowledgeDocument Read(NpgsqlDataReader reader)=>new(reader.GetGuid(0),reader.GetGuid(1),reader.GetString(2),reader.GetString(3),reader.GetInt64(4),reader.GetString(5),reader.IsDBNull(6)?null:reader.GetGuid(6),reader.GetString(7),reader.GetInt64(8),reader.GetGuid(9),reader.GetFieldValue<DateTimeOffset>(10),reader.IsDBNull(11)?null:reader.GetString(11));
    private static async Task<KnowledgeDocument?> Find(NpgsqlConnection db,NpgsqlTransaction tx,Guid tenant,Guid document,CancellationToken token,bool locked=false)
    {
        await using var command=Command(db,tx,$"SELECT {Columns} FROM public.knowledge_documents WHERE tenant_id=$1 AND document_id=$2"+(locked?" FOR UPDATE":""),tenant,document);
        await using var reader=await command.ExecuteReaderAsync(token); return await reader.ReadAsync(token)?Read(reader):null;
    }
    private static async Task<bool> Active(NpgsqlConnection db,NpgsqlTransaction tx,Guid tenant,Guid actor,Guid? organization,CancellationToken token)
    {
        await using var command=Command(db,tx,"""
            SELECT EXISTS(SELECT 1 FROM public.platform_tenants t
            JOIN public.tenant_memberships m ON m.tenant_id=t.tenant_id AND m.user_id=$2 AND m.enabled
            JOIN public.platform_users u ON u.user_id=m.user_id AND u.enabled
            WHERE t.tenant_id=$1 AND t.enabled AND ($3::uuid IS NULL OR EXISTS(SELECT 1 FROM public.tenant_organizations o
                WHERE o.tenant_id=t.tenant_id AND o.organization_id=$3 AND o.enabled)))
            """,tenant,actor);
        command.Parameters.Add(new NpgsqlParameter{NpgsqlDbType=NpgsqlDbType.Uuid,Value=(object?)organization??DBNull.Value});
        return await command.ExecuteScalarAsync(token) is true;
    }
    private static Task Enqueue(NpgsqlConnection db,NpgsqlTransaction tx,KnowledgeDocument document,string kind,CancellationToken token)=>Write(db,tx,
        "INSERT INTO public.knowledge_jobs(job_id,tenant_id,document_id,kind,document_revision) VALUES($1,$2,$3,$4,$5)",token,Guid.NewGuid(),document.TenantId,document.DocumentId,kind,document.Revision);
    private static Task Audit(NpgsqlConnection db,NpgsqlTransaction tx,KnowledgeDocument document,Guid actor,string action,string request,CancellationToken token)=>Write(db,tx,
        "INSERT INTO public.platform_knowledge_audit(event_id,tenant_id,document_id,actor_user_id,action,revision,request_id) VALUES($1,$2,$3,$4,$5,$6,$7)",token,Guid.NewGuid(),document.TenantId,document.DocumentId,actor,action,document.Revision,request);
    private static Task EndJob(NpgsqlConnection db,NpgsqlTransaction tx,Guid job,string status,CancellationToken token)=>Write(db,tx,"UPDATE public.knowledge_jobs SET status=$2,lease_id=NULL,lease_until=NULL WHERE job_id=$1",token,job,status);
    private static void Validate(Guid tenant,Guid document) { if(tenant==Guid.Empty || document==Guid.Empty) throw new KnowledgeStoreUnavailableException(); }
    private static bool Hex(string value,int length)=>value is not null && value.Length==length && value.All(c=>c is >= 'a' and <= 'f' or >= '0' and <= '9');
    private static string SafeFailure(string value)=>value is "ocr_required" or "encrypted_document" or "unsupported_document"
        or "empty_document" or "extracted_text_too_large" or "document_too_complex" or "invalid_document"
        or "parser_timeout" or "parser_unavailable" ? value : "ingestion_failed";
    private static NpgsqlCommand Command(NpgsqlConnection db,NpgsqlTransaction tx,string sql,params object[] values)
    { var command=new NpgsqlCommand(sql,db,tx){CommandTimeout=3}; foreach(var value in values) command.Parameters.AddWithValue(value); return command; }
    private static async Task Write(NpgsqlConnection db,NpgsqlTransaction tx,string sql,CancellationToken token,params object[] values)
    { await using var command=Command(db,tx,sql,values); await command.ExecuteNonQueryAsync(token); }
    private async Task<T> Run<T>(Func<NpgsqlConnection,NpgsqlTransaction,CancellationToken,Task<T>> action,CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); using var budget=CancellationTokenSource.CreateLinkedTokenSource(token); budget.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var value=configuration.GetConnectionString("Platform"); if(string.IsNullOrWhiteSpace(value)) throw new KnowledgeStoreUnavailableException();
            var settings=new NpgsqlConnectionStringBuilder(value){Timeout=3,CommandTimeout=3,IncludeErrorDetail=false};
            await using var db=new NpgsqlConnection(settings.ConnectionString); await db.OpenAsync(budget.Token);
            await using var tx=await db.BeginTransactionAsync(budget.Token); var result=await action(db,tx,budget.Token);
            await tx.CommitAsync(budget.Token); return result;
        }
        catch(Exception error) when (!(error is OperationCanceledException && token.IsCancellationRequested)
            && error is NpgsqlException or IOException or TimeoutException or ArgumentException or InvalidOperationException or OperationCanceledException)
        { throw new KnowledgeStoreUnavailableException(); }
    }
}
