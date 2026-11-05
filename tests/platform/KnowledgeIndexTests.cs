using System.Net;
using System.Text;
using System.Text.Json;
using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Knowledge;
using AssembleAI.Platform.Models;

namespace AssembleAI.Platform.SmokeTests;

public static class KnowledgeIndexTests
{
    private const string Variable="ASSEMBLEAI_KNOWLEDGE_INDEX_TEST_KEY";
    public static async Task RunAsync()
    {
        var previous=Environment.GetEnvironmentVariable(Variable);
        Environment.SetEnvironmentVariable(Variable,"fixture-private-parser-key");
        try
        {
            await UnicodeOverlap();
            Check(KnowledgeEmbeddingClient.ValidQuestion("设备维护\n前先做什么？\t请说明。\r"),"Public multiline questions must remain legal at the private embedding boundary.");
            Check(KnowledgeEmbeddingClient.ValidQuestion(new string('维',1000)) && !KnowledgeEmbeddingClient.ValidQuestion(new string('维',1001)),"All question boundaries must use the same 1000-scalar budget.");
            Check(!KnowledgeEmbeddingClient.ValidQuestion("bad\u0085control") && !KnowledgeEmbeddingClient.ValidQuestion("\ud800"),"Other controls and lone surrogates remain invalid.");
            var supplementary=string.Concat(Enumerable.Repeat(char.ConvertFromUtf32(0x1f600),1200));
            Check(KnowledgeIndexProfile.ValidParents([new(1999,supplementary,"page:1")]),"A legal maximum parent uses Unicode scalars, not UTF-16 code units.");
            Check(!KnowledgeIndexProfile.ValidParents([new(2000,"text","page:1")])
                && !KnowledgeIndexProfile.ValidParents([new(0,new string('维',1201),"page:1")]),"Private parent index and text budgets must match the parser and Python embedding adapter.");
            foreach(var invalid in new[]{"bad\u0001text","bad\u007ftext","bad\u0085text","bad\ud800text"})
                Check(!KnowledgeIndexProfile.ValidParents([new(0,invalid,"page:1")]),"Invalid controls and unpaired surrogates must be rejected before an embedding call.");
            await EmbeddingProtocol();
            DelegationIsolation();
        }
        finally { Environment.SetEnvironmentVariable(Variable,previous); }
        Console.WriteLine("PASS: knowledge index contracts: Unicode overlap, strict normalized vectors, parent segment attribution and isolated live delegation.");
    }
    private static async Task UnicodeOverlap()
    {
        // A valid 900k-codepoint TXT expands through 1200/150 overlap into 2,056,800 UTF-16 units.
        var text=string.Concat(Enumerable.Repeat(char.ConvertFromUtf32(0x1f600),1200));
        var chunks=Enumerable.Range(0,857).Select(i=>new {index=i,text=i==856?text[..2400]:text,locator="lines:1-1"});
        var body=JsonSerializer.Serialize(new {kind="txt",chunks}).Replace("\\uD83D\\uDE00",char.ConvertFromUtf32(0x1f600),StringComparison.OrdinalIgnoreCase);
        using var http=new HttpClient(new Reply(body));
        var parser=new KnowledgeParserClient(http,new() {ParserEndpoint="http://127.0.0.1/internal/parse",ParserKeyEnvironmentVariable=Variable,AllowInsecureLoopback=true});
        Check((await parser.ParseAsync("unicode.txt",[1],default)).Count==857,"Legal supplementary Unicode with chunk overlap must survive the C# parser contract.");
    }
    private static async Task EmbeddingProtocol()
    {
        var vector=new float[384]; vector[0]=1;
        var options=new KnowledgeOptions { IndexEnabled=true,EmbeddingEndpoint="http://127.0.0.1",AllowInsecureLoopback=true,ParserKeyEnvironmentVariable=Variable };
        var parents=new KnowledgeChunk[]{new(7,"设备维护前需要断电。", "page:2")};
        string Result(object segments)=>JsonSerializer.Serialize(new {profile=KnowledgeIndexProfile.Id,dimensions=384,segments});
        object Segment(string text="设备维护前需要断电。",int chunk=7,int segment=0,float[]? values=null,string locator="page:2")=>new {chunkIndex=chunk,segmentIndex=segment,text,locator,embedding=values??vector};
        using(var http=new HttpClient(new Reply(Result(new[]{Segment()}))))
        {
            var segments=await new KnowledgeEmbeddingClient(http,options).IndexAsync(parents,default);
            Check(segments.Count==1 && segments[0].ChunkIndex==7 && segments[0].Embedding.Length==384,"Index output must retain trusted parent attribution and a fixed vector shape.");
        }
        foreach(var body in new[]{Result(new[]{Segment(chunk:8)}),Result(new[]{Segment(text:"forged unrelated content")}),Result(new[]{Segment(text:"设备")}),Result(new[]{Segment(locator:"page:9")}),
            Result(new[]{Segment(segment:1)}),Result(new[]{Segment(),Segment()}),Result(new[]{Segment(values:new float[384])}),Result(new[]{Segment(values:new float[383])}),
            Result(new[]{Segment()}).Replace(KnowledgeIndexProfile.Id,"unknown-profile",StringComparison.Ordinal),Result(new[]{Segment()}).Replace("\"dimensions\":384","\"dimensions\":\"384\"",StringComparison.Ordinal)})
        {
            using var http=new HttpClient(new Reply(body));
            try { await new KnowledgeEmbeddingClient(http,options).IndexAsync(parents,default); throw new InvalidOperationException("Invalid indexing output accepted."); }
            catch(KnowledgeEmbeddingException error)
            {
                Check(error.InnerException is null,"Embedding errors must not expose upstream details.");
                if(body.Contains("unknown-profile",StringComparison.Ordinal)) Check(error.Code=="index_profile_mismatch","A fixed profile mismatch must be an actionable permanent failure.");
            }
        }
        using(var http=new HttpClient(new Reply(JsonSerializer.Serialize(new {profile=KnowledgeIndexProfile.Id,dimensions=384,embedding=vector}))))
            Check((await new KnowledgeEmbeddingClient(http,options).EmbedQueryAsync("维护前怎么办",default)).Length==384,"Query embedding must use the same fixed profile.");
    }
    private static void DelegationIsolation()
    {
        var caller=new ModelCaller(new SubjectIdentity("https://issuer.fixture","subject"),Guid.NewGuid(),Guid.NewGuid(),DateTimeOffset.UtcNow.AddMinutes(1),_=>Task.FromResult(true),default);
        var registry=new KnowledgeDelegationRegistry(TimeProvider.System);
        using var lease=registry.Register(caller)!;
        Check(registry.Resolve(lease.Capability)?.TenantId==caller.TenantId,"Knowledge delegation must retain server-captured identity.");
        var models=new ModelDelegationRegistry(TimeProvider.System);
        using var model=models.Register(caller,["documents"])!;
        Check(registry.Resolve(model.Capability) is null && models.Resolve(lease.Capability,"documents") is null,"Knowledge and model capability namespaces must remain isolated.");
        var captured=registry.Resolve(lease.Capability)!;
        lease.Dispose();
        Check(registry.Resolve(lease.Capability) is null && captured.Lifetime.IsCancellationRequested,"Ending the parent lease must revoke knowledge callbacks and cancel in-flight work.");
    }
    private sealed class Reply(string body):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body,Encoding.UTF8,"application/json")});
    }
    private static void Check(bool value,string message) { if(!value) throw new InvalidOperationException(message); }
}
