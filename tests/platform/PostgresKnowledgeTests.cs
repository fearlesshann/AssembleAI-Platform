using AssembleAI.Platform.Knowledge;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace AssembleAI.Platform.SmokeTests;

// Invoked only after the launcher verifies its disposable database's instance marker.
public static class PostgresKnowledgeTests
{
    public static async Task RunAsync(string connectionString)
    {
        var tenant=Guid.NewGuid(); var other=Guid.NewGuid(); var user=Guid.NewGuid(); var organization=Guid.NewGuid();
        var configuration=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["ConnectionStrings:Platform"]=connectionString }).Build();
        var store=new PostgresKnowledgeStore(configuration);
        await using var db=new NpgsqlConnection(connectionString); await db.OpenAsync();
        try
        {
            await Sql(db,"INSERT INTO public.platform_users(user_id,issuer,subject,display_name) VALUES($1,'https://knowledge.fixture',$2,'Knowledge fixture')",user,user.ToString());
            await Sql(db,"INSERT INTO public.platform_tenants(tenant_id,name) VALUES($1,'Knowledge A'),($2,'Knowledge B')",tenant,other);
            await Sql(db,"INSERT INTO public.tenant_memberships(tenant_id,user_id) VALUES($1,$3),($2,$3)",tenant,other,user);
            await Sql(db,"INSERT INTO public.tenant_organizations(tenant_id,organization_id,name,kind) VALUES($1,$2,'Factory','factory')",tenant,organization);
            Check((await store.ReadCreatorAsync(user,default))?.Subject==user.ToString(),"Worker must resolve the real immutable creator identity.");

            var first=Document(tenant,user,organization);
            var created=await store.CreateAsync(first,Id(),default);
            Check(created.Status=="queued" && created.Revision==1 && created.OrganizationId==organization,"Upload must create durable queued metadata.");
            Check(await store.ReadAsync(other,first.DocumentId,default) is null && (await store.ListAsync(other,default)).Count==0,"Document metadata must stay tenant scoped.");
            Check(await new PostgresKnowledgeStore(configuration).ReadAsync(tenant,first.DocumentId,default)==created,"Metadata must survive store recreation.");
            await store.CreateAsync(first,Id(),default);
            Check(await Count(db,"knowledge_jobs",tenant)==1 && await Count(db,"platform_knowledge_audit",tenant)==1,"Duplicate create must not duplicate outbox intent or audit.");
            await Unavailable(()=>store.CreateAsync(first with {Sha256=new string('b',64)},Id(),default));
            await Unavailable(()=>store.CreateAsync(Document(other,user,organization),Id(),default));
            var job=(await store.PendingAsync(default)).Single(j=>j.DocumentId==first.DocumentId);
            var claims=await Task.WhenAll(store.ClaimAsync(job.JobId,default),store.ClaimAsync(job.JobId,default));
            Check(claims.Count(v=>v is not null)==1,"Concurrent duplicate deliveries must grant only one live lease.");
            var oldLease=claims.Single(v=>v is not null)!;
            await Sql(db,"UPDATE public.knowledge_jobs SET lease_until=now()-interval '1 second' WHERE job_id=$1",job.JobId);
            var lease=await store.ClaimAsync(job.JobId,default);
            Check(lease is not null && lease.LeaseId!=oldLease.LeaseId && lease.Job.Attempts==2,"An expired delivery must get a fresh attempt and fencing token.");
            Check(!await store.CompleteIngestAsync(oldLease,[new(0,"stale secret","page:1")],default),"An expired worker cannot publish chunks after a reclaim.");
            Check(await store.CompleteIngestAsync(lease!,[new(0,"已授权的文档内容","page:1"),new(1,"第二段内容","page:2")],default),"A current worker can persist bounded text with locators.");
            Check((await store.ReadAsync(tenant,first.DocumentId,default))?.Status=="extracted" && await Count(db,"knowledge_chunks",tenant)==2,"Only committed extraction may change the document to extracted.");
            Check(!await store.CompleteIngestAsync(lease!,[new(0,"duplicate","page:1")],default),"A duplicated completion must not overwrite extracted chunks.");
            Check(await store.ClaimAsync(job.JobId,default) is null,"Completed jobs cannot be consumed twice.");

            await VerifyAuditRollback(db,store,Document(tenant,user,organization),user);
            var deletes=await Task.WhenAll(store.DeleteAsync(tenant,first.DocumentId,1,user,Id(),default),store.DeleteAsync(tenant,first.DocumentId,1,user,Id(),default));
            Check(deletes.Count(v=>v)==1,"Concurrent deletion must perform one revision transition and enqueue one purge.");
            var deleted=await store.ReadAsync(tenant,first.DocumentId,default);
            Check(deleted is {Status:"deleted",Revision:2} && await Count(db,"knowledge_chunks",tenant)==0,"Deletion must retain a tombstone and atomically remove extracted text.");
            Check((await store.ListAsync(tenant,default)).All(d=>d.DocumentId!=first.DocumentId),"Recent documents must exclude tombstones.");
            await Unavailable(()=>store.CreateAsync(first,Id(),default));
            var purge=(await store.PendingAsync(default)).Single(j=>j.DocumentId==first.DocumentId&&j.Kind=="purge");
            var purgeLease=(await store.ClaimAsync(purge.JobId,default))!;
            Check(await store.CompletePurgeAsync(purgeLease,default) && !await store.CompletePurgeAsync(purgeLease,default),"Purge acknowledgment is idempotent by refusing terminal replay.");

            var racing=await store.CreateAsync(Document(tenant,user,organization),Id(),default);
            var racingJob=(await store.PendingAsync(default)).Single(j=>j.DocumentId==racing.DocumentId);
            var racingLease=(await store.ClaimAsync(racingJob.JobId,default))!;
            Check(await store.DeleteAsync(tenant,racing.DocumentId,1,user,Id(),default),"Deletion must work during extraction.");
            Check(!await store.CompleteIngestAsync(racingLease,[new(0,"must not resurrect","paragraph:1")],default)
                && !await store.FailAsync(racingLease,default) && await Count(db,"knowledge_chunks",tenant)==0,"Old worker completion or failure cannot resurrect deleted document data.");

            var retryDoc=await store.CreateAsync(Document(tenant,user,null),Id(),default);
            var retry=(await store.PendingAsync(default)).Single(j=>j.DocumentId==retryDoc.DocumentId);
            for(var attempt=1;attempt<=3;attempt++)
            {
                var current=(await store.ClaimAsync(retry.JobId,default))!;
                Check(current.Job.Attempts==attempt,"Retry attempts must increase exactly once per accepted lease.");
                Check(await store.FailAsync(current,default),"A current lease must record a bounded failure.");
                Check(!await store.CompleteIngestAsync(current,[new(0,"late completion","line:1")],default),"Failed attempts cannot later publish content.");
                if(attempt<3)
                {
                    Check(await store.ClaimAsync(retry.JobId,default) is null,"Retry backoff must prevent an immediate re-delivery.");
                    await Sql(db,"UPDATE public.knowledge_jobs SET available_at=now()-interval '1 second' WHERE job_id=$1",retry.JobId);
                }
            }
            Check((await store.ReadAsync(tenant,retryDoc.DocumentId,default)) is {Status:"failed",FailureCode:"ingestion_failed"}
                && await store.ClaimAsync(retry.JobId,default) is null,"Three failures must reach a bounded terminal state with a safe cause.");

            foreach(var code in new[]{"ocr_required","encrypted_document","unsupported_document","empty_document","extracted_text_too_large","document_too_complex","invalid_document"})
            {
                var invalid=await store.CreateAsync(Document(tenant,user,organization),Id(),default);
                var invalidJob=(await store.PendingAsync(default)).Single(j=>j.DocumentId==invalid.DocumentId);
                var invalidLease=(await store.ClaimAsync(invalidJob.JobId,default))!;
                Check(await store.FailAsync(invalidLease,default,code),"A permanent format failure must finish its active lease.");
                Check((await store.ReadAsync(tenant,invalid.DocumentId,default)) is {Status:"failed",FailureCode:var actual} && actual==code
                    && await store.ClaimAsync(invalidJob.JobId,default) is null,"Permanent document failures must preserve a safe actionable cause without retrying unchanged input.");
            }
            var recovered=await store.CreateAsync(Document(tenant,user,null),Id(),default);
            var recoveredJob=(await store.PendingAsync(default)).Single(j=>j.DocumentId==recovered.DocumentId);
            foreach(var transient in new[]{"parser_timeout","parser_unavailable"})
            {
                var current=(await store.ClaimAsync(recoveredJob.JobId,default))!;
                Check(await store.FailAsync(current,default,transient),"Transient parser failures must release their lease.");
                Check((await store.ReadAsync(tenant,recovered.DocumentId,default)) is {Status:"queued",FailureCode:var actual} && actual==transient,"Pending retries must keep their latest safe reason visible.");
                await Sql(db,"UPDATE public.knowledge_jobs SET available_at=now()-interval '1 second' WHERE job_id=$1",recoveredJob.JobId);
            }
            var recoveryLease=(await store.ClaimAsync(recoveredJob.JobId,default))!;
            Check(await store.CompleteIngestAsync(recoveryLease,[new(0,"recovered text","page:1")],default)
                && (await store.ReadAsync(tenant,recovered.DocumentId,default)) is {Status:"extracted",FailureCode:null},"Successful extraction must clear a prior transient failure.");
            Check(await store.DeleteAsync(tenant,recovered.DocumentId,1,user,Id(),default),"Recovered chunks must remain deletable.");
            var unknownFailure=await store.CreateAsync(Document(tenant,user,null),Id(),default);
            var unknownJob=(await store.PendingAsync(default)).Single(j=>j.DocumentId==unknownFailure.DocumentId);
            var unknownLease=(await store.ClaimAsync(unknownJob.JobId,default))!;
            await store.FailAsync(unknownLease,default,"private-knowledge-secret raw exception");
            Check((await store.ReadAsync(tenant,unknownFailure.DocumentId,default))?.FailureCode=="ingestion_failed","Raw parser errors must be replaced with a fixed safe cause.");

            var revoked=await store.CreateAsync(Document(tenant,user,organization),Id(),default);
            var revokedJob=(await store.PendingAsync(default)).Single(j=>j.DocumentId==revoked.DocumentId);
            var revokedLease=(await store.ClaimAsync(revokedJob.JobId,default))!;
            await Sql(db,"UPDATE public.tenant_memberships SET enabled=false WHERE tenant_id=$1 AND user_id=$2",tenant,user);
            Check(!await store.CompleteIngestAsync(revokedLease,[new(0,"private revoked content","page:1")],default),"Membership revocation must reject worker publication.");
            Check(await Count(db,"knowledge_chunks",tenant)==0 && (await store.ReadAsync(tenant,revoked.DocumentId,default))?.Status=="failed","Revoked work must not leave retrievable chunks.");
            await Sql(db,"UPDATE public.tenant_memberships SET enabled=true WHERE tenant_id=$1 AND user_id=$2",tenant,user);
            await Sql(db,"UPDATE public.platform_users SET enabled=false WHERE user_id=$1",user);
            Check(await store.ReadCreatorAsync(user,default) is null,"Disabled creator identities cannot be used for background authorization.");
            await Sql(db,"UPDATE public.platform_users SET enabled=true WHERE user_id=$1",user);
            using var cancelled=new CancellationTokenSource(); cancelled.Cancel();
            try { await store.ListAsync(tenant,cancelled.Token); throw new InvalidOperationException("Cancellation ignored"); } catch(OperationCanceledException) { }
            var bad=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["ConnectionStrings:Platform"]="Password=private-knowledge-secret;InvalidOption=true"}).Build();
            await Unavailable(()=>new PostgresKnowledgeStore(bad).ListAsync(tenant,default));
        }
        finally
        {
            await Sql(db,"DELETE FROM public.knowledge_chunks WHERE tenant_id IN ($1,$2)",tenant,other);
            await Sql(db,"DELETE FROM public.knowledge_jobs WHERE tenant_id IN ($1,$2)",tenant,other);
            await Sql(db,"DELETE FROM public.knowledge_documents WHERE tenant_id IN ($1,$2)",tenant,other);
            await Sql(db,"DELETE FROM public.platform_knowledge_audit WHERE tenant_id IN ($1,$2)",tenant,other);
            await Sql(db,"DELETE FROM public.platform_tenants WHERE tenant_id IN ($1,$2)",tenant,other);
            await Sql(db,"DELETE FROM public.platform_users WHERE user_id=$1",user);
        }
        Console.WriteLine("PASS: PostgreSQL knowledge: isolated documents, atomic outbox/audit, duplicate delivery, expired lease fencing, bounded retries and safe causes, revocation, deletion tombstones and no stale chunk resurrection.");
    }
    private static async Task VerifyAuditRollback(NpgsqlConnection db,PostgresKnowledgeStore store,KnowledgeDocument document,Guid user)
    {
        await Sql(db,"CREATE FUNCTION test_support.fail_knowledge_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'private-knowledge-secret' USING ERRCODE='23514'; END $$");
        try
        {
            await Sql(db,$"CREATE TRIGGER fixture_knowledge_audit BEFORE INSERT ON public.platform_knowledge_audit FOR EACH ROW WHEN (NEW.document_id='{document.DocumentId}') EXECUTE FUNCTION test_support.fail_knowledge_audit()");
            try
            {
                await Unavailable(()=>store.CreateAsync(document,Id(),default));
                Check(await store.ReadAsync(document.TenantId,document.DocumentId,default) is null,"Audit failure must roll back metadata and its ingestion intent.");
                Check(!(await store.PendingAsync(default)).Any(j=>j.DocumentId==document.DocumentId),"Failed uploads cannot leave a worker job.");
            }
            finally { await Sql(db,"DROP TRIGGER fixture_knowledge_audit ON public.platform_knowledge_audit"); }
            await store.CreateAsync(document,Id(),default);
            await Sql(db,$"CREATE TRIGGER fixture_knowledge_audit BEFORE INSERT ON public.platform_knowledge_audit FOR EACH ROW WHEN (NEW.document_id='{document.DocumentId}') EXECUTE FUNCTION test_support.fail_knowledge_audit()");
            try
            {
                await Unavailable(()=>store.DeleteAsync(document.TenantId,document.DocumentId,1,user,Id(),default));
                Check((await store.ReadAsync(document.TenantId,document.DocumentId,default)) is {Status:"queued",Revision:1},"Delete audit failure must retain the original revision and queued work.");
            }
            finally { await Sql(db,"DROP TRIGGER fixture_knowledge_audit ON public.platform_knowledge_audit"); }
        }
        finally { await Sql(db,"DROP FUNCTION test_support.fail_knowledge_audit()"); }
    }
    private static KnowledgeDocument Document(Guid tenant,Guid user,Guid? organization)=>new(tenant,Guid.NewGuid(),"fixture.pdf","application/pdf",128,new string('a',64),organization,"queued",1,user,DateTimeOffset.UtcNow);
    private static string Id()=>Guid.NewGuid().ToString("N");
    private static void Check(bool value,string message) { if(!value) throw new InvalidOperationException(message); }
    private static async Task Unavailable(Func<Task> action)
    { try { await action(); throw new InvalidOperationException("Expected bounded storage failure."); } catch(KnowledgeStoreUnavailableException error) { Check(error.InnerException is null && !error.ToString().Contains("private-knowledge-secret"),"Storage failures must not expose credentials or raw database errors."); } }
    private static async Task Sql(NpgsqlConnection db,string text,params object[] values)
    { await using var command=new NpgsqlCommand(text,db); foreach(var value in values) command.Parameters.AddWithValue(value); await command.ExecuteNonQueryAsync(); }
    private static async Task<long> Count(NpgsqlConnection db,string table,Guid tenant)
    { await using var command=new NpgsqlCommand($"SELECT count(*) FROM public.{table} WHERE tenant_id=$1",db); command.Parameters.AddWithValue(tenant); return (long)(await command.ExecuteScalarAsync())!; }
}
