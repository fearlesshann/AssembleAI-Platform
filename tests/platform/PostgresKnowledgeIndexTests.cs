using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Knowledge;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace AssembleAI.Platform.SmokeTests;

public static class PostgresKnowledgeIndexTests
{
    public static async Task RunAsync(string connectionString)
    {
        var tenant=Guid.NewGuid(); var user=Guid.NewGuid(); var role=Guid.NewGuid(); var grant=Guid.NewGuid();
        var factory=Guid.NewGuid(); var other=Guid.NewGuid(); var subject=new SubjectIdentity("https://index.fixture",user.ToString());
        var configuration=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["ConnectionStrings:Platform"]=connectionString,["Knowledge:IndexEnabled"]="true"}).Build();
        var store=new PostgresKnowledgeStore(configuration); var authorization=new PostgresAuthorizationStore(configuration);
        await using var db=new NpgsqlConnection(connectionString); await db.OpenAsync();
        try
        {
            await Sql(db,"INSERT INTO public.platform_users(user_id,issuer,subject,display_name) VALUES($1,$2,$3,'Index fixture')",user,subject.Issuer,subject.Subject);
            await Sql(db,"INSERT INTO public.platform_tenants(tenant_id,name) VALUES($1,'Index fixture')",tenant);
            await Sql(db,"INSERT INTO public.tenant_memberships(tenant_id,user_id) VALUES($1,$2)",tenant,user);
            await Sql(db,"INSERT INTO public.tenant_organizations(tenant_id,organization_id,name,kind) VALUES($1,$2,'A','factory'),($1,$3,'B','factory')",tenant,factory,other);
            await Sql(db,"INSERT INTO public.tenant_roles(tenant_id,role_id,name) VALUES($1,$2,'Index fixture')",tenant,role);
            await Sql(db,"INSERT INTO public.tenant_member_roles(tenant_id,user_id,role_id) VALUES($1,$2,$3)",tenant,user,role);
            await Sql(db,"INSERT INTO public.role_permission_grants(tenant_id,grant_id,role_id,permission,resource_type,resource_id,tenant_wide) VALUES($1,$2,$3,'knowledge:manage','knowledge','documents',true)",tenant,grant,role);

            var first=await Create(factory,9);
            var firstJob=(await store.PendingAsync(default)).Single(j=>j.DocumentId==first.DocumentId && j.Kind=="embed");
            var lease=(await store.ClaimAsync(firstJob.JobId,default))!;
            var batch=await store.ReadIndexBatchAsync(lease,default);
            Check(batch.Count==8,"Each lease must expose at most eight original chunks.");
            Check(await store.CompleteIndexBatchAsync(lease,Segments(batch,0),default),"First bounded index batch must commit.");
            Check(!await store.CompleteIndexBatchAsync(lease,Segments(batch,0),default),"Duplicate batch completion must be fenced.");
            Check((await store.ReadAsync(tenant,first.DocumentId,default))?.Status=="indexing","A partial index must never become searchable.");
            lease=(await store.ClaimAsync(firstJob.JobId,default))!;
            Check(lease.Job.Attempts==1 && lease.Job.NextChunkIndex==8,"Successful progress resets consecutive retries and resumes at the next parent.");
            batch=await store.ReadIndexBatchAsync(lease,default);
            Check(batch.Count==1 && await store.CompleteIndexBatchAsync(lease,Segments(batch,0),default),"Final batch must complete.");
            Check((await store.ReadAsync(tenant,first.DocumentId,default)) is {Status:"ready",IndexProfile:KnowledgeIndexProfile.Id},"Ready requires complete vectors of the fixed profile.");

            var second=await Create(other,1); await Index(second,1);
            await Sql(db,"UPDATE public.role_permission_grants SET tenant_wide=false WHERE tenant_id=$1 AND grant_id=$2",tenant,grant);
            await Sql(db,"INSERT INTO public.grant_organizations(tenant_id,grant_id,organization_id) VALUES($1,$2,$3)",tenant,grant,factory);
            var access=(await authorization.ReadAccessAsync(subject,tenant))!;
            var hits=await store.SearchAsync(access,Vector(1),1,default);
            Check(hits.Count==1 && hits[0].DocumentId==first.DocumentId,"Unauthorized best vectors must be filtered before topK ranking.");
            var exact=Guid.NewGuid();
            await Sql(db,"INSERT INTO public.role_permission_grants(tenant_id,grant_id,role_id,permission,resource_type,resource_id,tenant_wide) VALUES($1,$2,$3,'knowledge:read','document',$4,false)",tenant,exact,role,second.DocumentId.ToString("N"));
            await Sql(db,"INSERT INTO public.grant_organizations(tenant_id,grant_id,organization_id) VALUES($1,$2,$3)",tenant,exact,factory);
            access=(await authorization.ReadAccessAsync(subject,tenant))!;
            Check((await store.SearchAsync(access,Vector(1),1,default)).Single().DocumentId==first.DocumentId,"Document identity from one grant cannot borrow another organization's scope.");
            await Sql(db,"UPDATE public.grant_organizations SET organization_id=$3 WHERE tenant_id=$1 AND grant_id=$2",tenant,exact,other);
            Check((await store.SearchAsync(access,Vector(1),1,default)).Count==0,"An old authorization version cannot drive a query.");
            access=(await authorization.ReadAccessAsync(subject,tenant))!;
            Check((await store.SearchAsync(access,Vector(1),1,default)).Single().DocumentId==second.DocumentId,"An exact document grant permits only its matching organization.");

            var pending=await Create(factory,1);
            var pendingJob=(await store.PendingAsync(default)).Single(j=>j.DocumentId==pending.DocumentId && j.Kind=="embed");
            var old=(await store.ClaimAsync(pendingJob.JobId,default))!;
            var oldBatch=await store.ReadIndexBatchAsync(old,default);
            Check(await store.DeleteAsync(tenant,pending.DocumentId,1,user,Id(),default),"Deleting an indexing document must succeed.");
            Check(!await store.CompleteIndexBatchAsync(old,Segments(oldBatch,0),default),"Deleted documents cannot be resurrected by an embedding worker.");
            var revoked=await Create(factory,1);
            var revokedJob=(await store.PendingAsync(default)).Single(j=>j.DocumentId==revoked.DocumentId && j.Kind=="embed");
            var revokedLease=(await store.ClaimAsync(revokedJob.JobId,default))!;
            var revokedBatch=await store.ReadIndexBatchAsync(revokedLease,default);
            await Sql(db,"UPDATE public.role_permission_grants SET enabled=false WHERE tenant_id=$1 AND grant_id=$2",tenant,grant);
            Check(!await store.CompleteIndexBatchAsync(revokedLease,Segments(revokedBatch,0),default),"Current manage-grant revocation must fence publication inside the repository.");
        }
        finally
        {
            await Sql(db,"DELETE FROM public.knowledge_embeddings WHERE tenant_id=$1",tenant);
            await Sql(db,"DELETE FROM public.knowledge_chunks WHERE tenant_id=$1",tenant);
            await Sql(db,"DELETE FROM public.knowledge_jobs WHERE tenant_id=$1",tenant);
            await Sql(db,"DELETE FROM public.knowledge_documents WHERE tenant_id=$1",tenant);
            await Sql(db,"DELETE FROM public.platform_knowledge_audit WHERE tenant_id=$1",tenant);
            await Sql(db,"DELETE FROM public.platform_knowledge_searches WHERE tenant_id=$1",tenant);
            await Sql(db,"DELETE FROM public.platform_tenants WHERE tenant_id=$1",tenant);
            await Sql(db,"DELETE FROM public.platform_users WHERE user_id=$1",user);
        }
        Console.WriteLine("PASS: PostgreSQL knowledge indexing: bounded resumable batches, exact vector ranking after ACL, authorization versions, precise resource scopes and deletion/revocation fences.");

        async Task<KnowledgeDocument> Create(Guid org,int count)
        {
            var doc=await store.CreateAsync(new(tenant,Guid.NewGuid(),"fixture.txt","text/plain",1,new string('a',64),org,"queued",1,user,DateTimeOffset.UtcNow),Id(),default);
            var job=(await store.PendingAsync(default)).Single(j=>j.DocumentId==doc.DocumentId && j.Kind=="ingest");
            var lease=(await store.ClaimAsync(job.JobId,default))!;
            Check(await store.CompleteIngestAsync(lease,Enumerable.Range(0,count).Select(i=>new KnowledgeChunk(i,"授权文档片段 "+i,"lines:"+(i+1))).ToArray(),default),"Extraction must atomically enqueue embedding.");
            return doc;
        }
        async Task Index(KnowledgeDocument doc,int component)
        {
            var job=(await store.PendingAsync(default)).Single(j=>j.DocumentId==doc.DocumentId && j.Kind=="embed");
            var lease=(await store.ClaimAsync(job.JobId,default))!;
            Check(await store.CompleteIndexBatchAsync(lease,Segments(await store.ReadIndexBatchAsync(lease,default),component),default),"Index fixture must complete.");
        }
    }
    private static IReadOnlyList<KnowledgeSegment> Segments(IReadOnlyList<KnowledgeChunk> chunks,int component)=>chunks.Select(c=>new KnowledgeSegment(c.Index,0,c.Text,c.Locator,Vector(component))).ToArray();
    private static float[] Vector(int component) { var value=new float[384]; value[component]=1; return value; }
    private static string Id()=>Guid.NewGuid().ToString("N");
    private static async Task Sql(NpgsqlConnection db,string sql,params object[] args)
    { await using var command=new NpgsqlCommand(sql,db); foreach(var value in args)command.Parameters.AddWithValue(value); await command.ExecuteNonQueryAsync(); }
    private static void Check(bool value,string message) { if(!value)throw new InvalidOperationException(message); }
}
