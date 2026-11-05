using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Knowledge;
using AssembleAI.Platform.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AssembleAI.Platform.SmokeTests;

public static class KnowledgeDelegationTests
{
    public static async Task RunAsync()
    {
        const string variable="ASSEMBLEAI_KNOWLEDGE_DELEGATION_TEST_KEY";
        var previous=Environment.GetEnvironmentVariable(variable); Environment.SetEnvironmentVariable(variable,"fixture-private-parser-key");
        var state=new State(); var builder=WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        var options=new KnowledgeOptions {Enabled=true,IndexEnabled=true,EmbeddingEndpoint="http://127.0.0.1",AllowInsecureLoopback=true,ParserKeyEnvironmentVariable=variable};
        builder.Services.AddSingleton(options); builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<KnowledgeDelegationRegistry>(); builder.Services.AddSingleton<KnowledgeSearchAdmission>();
        builder.Services.AddSingleton<IAuthorizationStore>(state); builder.Services.AddSingleton<IKnowledgeSearchStore>(state);
        using var embeddingHttp=new HttpClient(new Embeddings(state)); builder.Services.AddSingleton(new KnowledgeEmbeddingClient(embeddingHttp,options));
        await using var app=builder.Build(); app.MapKnowledgeDelegationEndpoints(); await app.StartAsync();
        using var browser=new HttpClient {BaseAddress=new(app.Urls.Single())};
        var registry=app.Services.GetRequiredService<KnowledgeDelegationRegistry>();
        using var lease=registry.Register(state.Caller)!;
        try
        {
            using(var response=await Send("search",new{question="维护前怎么办",limit=2},null)) await Status(response,401);
            using(var response=await Send("search",new{question="维护前怎么办",limit=2},new string('a',64))) await Status(response,403);
            var models=new ModelDelegationRegistry(TimeProvider.System); using var model=models.Register(state.Caller,["documents"])!;
            using(var response=await Send("search",new{question="维护前怎么办",limit=2},model.Capability)) await Status(response,403);
            using(var response=await Send("search",new{question="维护前怎么办",limit=2},lease.Capability))
            {
                await Status(response,200);
                using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Check(json.RootElement.GetProperty("authorizationVersion").GetInt64()==1 && json.RootElement.GetProperty("profile").GetString()==KnowledgeIndexProfile.Id
                    && json.RootElement.GetProperty("hits")[0].GetProperty("documentId").GetGuid()==state.Document,"Search must return guarded, attributable hits.");
            }
            Check(state.Captured?.Source==state.Caller.Source && state.Outcome=="completed" && state.ParentChecks==2,"Successful search must audit trusted source and check parent only at entry/exit.");
            using(var response=await Send("search",new{question="维护前\n怎么办？\t请说明。\r",limit=1},lease.Capability)) await Status(response,200);
            using(var response=await Send("search",new{question=new string('维',1000),limit=1},lease.Capability)) await Status(response,200);
            using(var response=await Send("search",new{question=new string('维',1001),limit=1},lease.Capability)) await Status(response,400);
            var check=new {authorizationVersion=1,profile=KnowledgeIndexProfile.Id,documents=new[]{new{documentId=state.Document,revision=1}}};
            state.ParentChecks=0;
            using(var response=await Send("check",check,lease.Capability)) await Status(response,200);
            Check(state.ParentChecks==2,"Document check must not multiply expensive parent checks.");
            state.BeforeDocumentCheck=()=>state.ParentAlive=false;
            using(var response=await Send("check",check,lease.Capability)) await Status(response,403);
            state.BeforeDocumentCheck=null; state.ParentAlive=true;
            // Parent failure revokes the registry entry, so each scenario needs a fresh live delegation.
            using var second=registry.Register(state.Caller)!;
            state.DocumentsValid=false;
            using(var response=await Send("check",check,second.Capability)) await Status(response,403);
            state.DocumentsValid=true; state.Version=2;
            using(var response=await Send("check",check,second.Capability)) await Status(response,403);
            state.Version=1;
            using(var response=await Send("check",new{authorizationVersion=1,profile=KnowledgeIndexProfile.Id,documents=Array.Empty<object>()},second.Capability)) await Status(response,200);
            foreach(var body in new[]{"{\"question\":\"x\",\"question\":\"x\",\"limit\":1}","{\"question\":\"x\",\"limit\":\"1\"}","{\"question\":\"x\",\"limit\":6}","{\"question\":\"\\uD800\",\"limit\":1}"})
            { using var request=new HttpRequestMessage(HttpMethod.Post,"/internal/knowledge/search"){Content=new StringContent(body,Encoding.UTF8,"application/json")}; request.Headers.Authorization=new("Bearer",second.Capability); using var response=await browser.SendAsync(request); await Status(response,400); }
            var gate=state.HoldEmbedding=new();
            var pending=Send("search",new{question="维护前怎么办",limit=1},second.Capability);
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); state.Version++;
            gate.Release.TrySetResult(); using(var response=await pending) await Status(response,403);
            Check(state.Outcome=="revoked","Revocation during embedding must prevent result delivery and record a safe outcome.");
        }
        finally { state.HoldEmbedding?.Release.TrySetResult(); await app.StopAsync(); Environment.SetEnvironmentVariable(variable,previous); }
        Console.WriteLine("PASS: knowledge delegation HTTP: isolated capability, exact DTOs, trusted audit source, two parent checks, current authorization/document versions and mid-embedding revocation.");
        Task<HttpResponseMessage> Send(string path,object value,string? capability)
        {
            var request=new HttpRequestMessage(HttpMethod.Post,"/internal/knowledge/"+path){Content=JsonContent.Create(value)};
            if(capability is not null) request.Headers.Authorization=new("Bearer",capability);
            return browser.SendAsync(request);
        }
    }
    private static async Task Status(HttpResponseMessage response,int expected)
    { Check((int)response.StatusCode==expected,$"Knowledge delegation HTTP expected {expected}, received {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}"); Check(response.Headers.CacheControl?.NoStore==true,"Callbacks must not be cacheable."); }
    private static void Check(bool value,string message) { if(!value) throw new InvalidOperationException(message); }
    private sealed class Gate { public TaskCompletionSource Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously); public TaskCompletionSource Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously); }
    private sealed class Embeddings(State state):HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            if(state.HoldEmbedding is {} gate) { gate.Entered.TrySetResult(); await gate.Release.Task.WaitAsync(token); }
            var vector=new float[384];vector[0]=1;
            return new(HttpStatusCode.OK){Content=JsonContent.Create(new{profile=KnowledgeIndexProfile.Id,dimensions=384,embedding=vector})};
        }
    }
    private sealed class State:IAuthorizationStore,IKnowledgeSearchStore
    {
        public Guid Tenant=Guid.NewGuid(),User=Guid.NewGuid(),Document=Guid.NewGuid(); public long Version=1;
        public bool ParentAlive=true,DocumentsValid=true; public int ParentChecks; public ModelCaller? Captured; public string? Outcome; public Gate? HoldEmbedding; public Action? BeforeDocumentCheck;
        public ModelCaller Caller=>new(new("https://issuer.fixture","subject"),Tenant,User,DateTimeOffset.UtcNow.AddMinutes(2),_=>{ParentChecks++;return Task.FromResult(ParentAlive);},default,new("knowledge","0.1.0",Document,new string('a',32)));
        public Task<IReadOnlyList<TenantMembership>> ListTenantsAsync(SubjectIdentity identity,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
        public Task<AccessSnapshot?> ReadAccessAsync(SubjectIdentity identity,Guid tenantId,CancellationToken cancellationToken=default)=>Task.FromResult<AccessSnapshot?>(new(User,Tenant,Version,[new("knowledge:read","knowledge","documents",true,new HashSet<Guid>())],new HashSet<Guid>()));
        public Task<IReadOnlyList<KnowledgeHit>> SearchAsync(AccessSnapshot access,float[] vector,int limit,CancellationToken token)=>Task.FromResult<IReadOnlyList<KnowledgeHit>>([new($"{Document:N}-1-0-0",Document,1,"fixture.txt","lines:1-1","先断电",1)]);
        public Task<bool> CheckDocumentsAsync(AccessSnapshot access,IReadOnlyList<KnowledgeDocumentReference> documents,CancellationToken token)
        { BeforeDocumentCheck?.Invoke();return Task.FromResult(DocumentsValid && access.AuthorizationVersion==Version && documents.All(d=>d.DocumentId==Document && d.Revision==1)); }
        public Task<Guid> BeginSearchAsync(ModelCaller caller,string requestId,long version,CancellationToken token) { Captured=caller;return Task.FromResult(Guid.NewGuid()); }
        public Task CompleteSearchAsync(Guid callId,string outcome,int count,long elapsed,CancellationToken token) { Outcome=outcome;return Task.CompletedTask; }
    }
}
