using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AssembleAI.Platform.Knowledge;

public sealed class KnowledgeEmbeddingClient(HttpClient client,KnowledgeOptions options)
{
    public async Task<IReadOnlyList<KnowledgeSegment>> IndexAsync(IReadOnlyList<KnowledgeChunk> chunks,CancellationToken token)
    {
        if(!KnowledgeIndexProfile.ValidParents(chunks)) throw new KnowledgeEmbeddingException();
        using var json=await Send("/internal/index",new {chunks=chunks.Select(c=>new {index=c.Index,text=c.Text,locator=c.Locator})},token);
        try
        {
            var root=json.RootElement;
            if(!Fields(root,"profile","dimensions","segments")) throw new KnowledgeEmbeddingException();
            if(!Profile(root)) throw new KnowledgeEmbeddingException("index_profile_mismatch");
            if(root.GetProperty("segments") is not {ValueKind:JsonValueKind.Array} values || values.GetArrayLength() is <1 or >64) throw new KnowledgeEmbeddingException();
            var result=new List<KnowledgeSegment>();
            foreach(var value in values.EnumerateArray())
            {
                if(!Fields(value,"chunkIndex","segmentIndex","text","locator","embedding")) throw new KnowledgeEmbeddingException();
                result.Add(new(value.GetProperty("chunkIndex").GetInt32(),value.GetProperty("segmentIndex").GetInt32(),
                    value.GetProperty("text").GetString()!,value.GetProperty("locator").GetString()!,Vector(value.GetProperty("embedding"))));
            }
            if(!KnowledgeIndexProfile.ValidSegments(chunks,result)) throw new KnowledgeEmbeddingException();
            return result;
        }
        catch(Exception error) when(error is InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
        { throw new KnowledgeEmbeddingException(); }
    }
    public async Task<float[]> EmbedQueryAsync(string query,CancellationToken token)
    {
        if(!ValidQuestion(query)) throw new KnowledgeEmbeddingException();
        using var json=await Send("/internal/embed-query",new {query},token);
        try
        {
            if(!Fields(json.RootElement,"profile","dimensions","embedding")) throw new KnowledgeEmbeddingException();
            if(!Profile(json.RootElement)) throw new KnowledgeEmbeddingException("index_profile_mismatch");
            return Vector(json.RootElement.GetProperty("embedding"));
        }
        catch(Exception error) when(error is InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
        { throw new KnowledgeEmbeddingException(); }
    }
    public static bool ValidQuestion(string? value)=>KnowledgeIndexProfile.ValidText(value,1000);
    private async Task<JsonDocument> Send(string path,object body,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(!options.IndexEnabled || !Uri.TryCreate(options.EmbeddingEndpoint,UriKind.Absolute,out var endpoint)
            || endpoint.AbsolutePath!="/" || endpoint.UserInfo.Length!=0 || endpoint.Query.Length!=0 || endpoint.Fragment.Length!=0
            || !(endpoint.Scheme=="https" || endpoint.Scheme=="http" && endpoint.IsLoopback && options.AllowInsecureLoopback)
            || options.ParserKeyEnvironmentVariable is null || !Regex.IsMatch(options.ParserKeyEnvironmentVariable,"\\A[A-Za-z_][A-Za-z0-9_]{0,127}\\z")) throw new KnowledgeEmbeddingException();
        var key=Environment.GetEnvironmentVariable(options.ParserKeyEnvironmentVariable);
        if(key is not {Length:>=16 and <=256} || key.Any(c=>c is <'!' or >'~')) throw new KnowledgeEmbeddingException();
        using var request=new HttpRequestMessage(HttpMethod.Post,new Uri(endpoint,path));
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",key);
        request.Content=JsonContent.Create(body);
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(token); budget.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,budget.Token);
            const int maximum=2*1024*1024;
            if(!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType!="application/json"
                || response.Content.Headers.ContentEncoding.Count!=0 || response.Content.Headers.ContentLength>maximum) throw new KnowledgeEmbeddingException();
            await using var stream=await response.Content.ReadAsStreamAsync(budget.Token);
            using var bytes=new MemoryStream(); var buffer=new byte[8192]; int read;
            while((read=await stream.ReadAsync(buffer,budget.Token))!=0)
            { if(bytes.Length+read>maximum) throw new KnowledgeEmbeddingException(); bytes.Write(buffer,0,read); }
            return JsonDocument.Parse(bytes.ToArray(),new(){MaxDepth=6});
        }
        catch(Exception error) when(error is HttpRequestException or IOException or JsonException or InvalidOperationException
            || error is OperationCanceledException && !token.IsCancellationRequested) { throw new KnowledgeEmbeddingException(); }
    }
    private static bool Profile(JsonElement root)=>root.GetProperty("profile").ValueKind==JsonValueKind.String
        && root.GetProperty("profile").GetString()==KnowledgeIndexProfile.Id && root.GetProperty("dimensions").ValueKind==JsonValueKind.Number
        && root.GetProperty("dimensions").TryGetInt32(out var dimensions) && dimensions==KnowledgeIndexProfile.Dimensions;
    private static float[] Vector(JsonElement value)
    {
        if(value.ValueKind!=JsonValueKind.Array || value.GetArrayLength()!=KnowledgeIndexProfile.Dimensions) throw new KnowledgeEmbeddingException();
        var vector=value.EnumerateArray().Select(v=>v.GetSingle()).ToArray();
        if(!KnowledgeIndexProfile.ValidVector(vector)) throw new KnowledgeEmbeddingException();
        return vector;
    }
    internal static bool Fields(JsonElement value,params string[] fields)=>value.ValueKind==JsonValueKind.Object
        && value.EnumerateObject().Count()==fields.Length && value.EnumerateObject().Select(p=>p.Name).Distinct(StringComparer.Ordinal).Count()==fields.Length
        && value.EnumerateObject().All(p=>fields.Contains(p.Name,StringComparer.Ordinal));
}
