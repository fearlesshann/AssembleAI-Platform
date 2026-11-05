using AssembleAI.Platform.Authorization;
using AssembleAI.Platform.Models;

namespace AssembleAI.Platform.Knowledge;

public sealed record KnowledgeSegment(int ChunkIndex,int SegmentIndex,string Text,string Locator,float[] Embedding);
public sealed record KnowledgeHit(string ChunkId,Guid DocumentId,long DocumentRevision,string FileName,string Locator,string Text,double Score);
public sealed record KnowledgeDocumentReference(Guid DocumentId,long Revision);

public interface IKnowledgeIndexStore
{
    Task<IReadOnlyList<KnowledgeChunk>> ReadIndexBatchAsync(KnowledgeJobLease lease,CancellationToken token);
    Task<bool> CompleteIndexBatchAsync(KnowledgeJobLease lease,IReadOnlyList<KnowledgeSegment> segments,CancellationToken token);
}
public interface IKnowledgeBrokerStore
{
    Task<KnowledgeJob?> ReadJobAsync(Guid jobId,CancellationToken token);
}
public interface IKnowledgeSearchStore
{
    Task<IReadOnlyList<KnowledgeHit>> SearchAsync(AccessSnapshot access,float[] embedding,int limit,CancellationToken token);
    Task<bool> CheckDocumentsAsync(AccessSnapshot access,IReadOnlyList<KnowledgeDocumentReference> documents,CancellationToken token);
    Task<Guid> BeginSearchAsync(ModelCaller caller,string requestId,long authorizationVersion,CancellationToken token);
    Task CompleteSearchAsync(Guid callId,string outcome,int count,long elapsedMilliseconds,CancellationToken token);
}

public static class KnowledgeIndexProfile
{
    public const string Id="e5-small-dd476dd0-384-v1";
    public const int Dimensions=384;
    public const int BatchSize=8;
    public const int MaximumSegments=4096;
    public static bool ValidText(string? value,int maximumScalars)
    {
        if(value is null || value.Length is <1 || value.Length>maximumScalars*2 || string.IsNullOrWhiteSpace(value)
            || value.Any(c=>char.IsControl(c) && c is not ('\r' or '\n' or '\t'))
            || value.EnumerateRunes().Count()>maximumScalars) return false;
        try { return new System.Text.UTF8Encoding(false,true).GetByteCount(value)<=maximumScalars*4; }
        catch(System.Text.EncoderFallbackException) { return false; }
    }
    public static bool ValidVector(float[]? value)
    {
        if(value is not {Length:Dimensions}) return false;
        double norm=0;
        foreach(var component in value) { if(!float.IsFinite(component)) return false; norm+=(double)component*component; }
        return Math.Abs(norm-1)<=0.002;
    }
    public static bool ValidParents(IReadOnlyList<KnowledgeChunk>? chunks)=>chunks is {Count:>=1 and <=BatchSize}
        && chunks.All(c=>c is not null) && chunks.Select(c=>c.Index).Distinct().Count()==chunks.Count
        && chunks.All(c=>c.Index is >=0 and <2000 && ValidText(c.Text,1200)
            && c.Locator is {Length:>=1 and <=200} && !c.Locator.Any(char.IsControl));
    public static bool ValidSegments(IReadOnlyList<KnowledgeChunk> chunks,IReadOnlyList<KnowledgeSegment>? segments)
    {
        if(!ValidParents(chunks) || segments is not {Count:>=1 and <=64}) return false;
        var parents=chunks.ToDictionary(c=>c.Index); var next=chunks.ToDictionary(c=>c.Index,_=>0);
        foreach(var segment in segments)
        {
            if(segment is null || !parents.TryGetValue(segment.ChunkIndex,out var parent)
                || segment.SegmentIndex!=next[segment.ChunkIndex]++ || !ValidText(segment.Text,1200)
                || !parent.Text.Contains(segment.Text,StringComparison.Ordinal)
                || segment.Locator!=parent.Locator || !ValidVector(segment.Embedding)) return false;
        }
        return next.Values.All(count=>count>0) && chunks.All(parent=>string.Concat(segments.Where(s=>s.ChunkIndex==parent.Index).Select(s=>s.Text))==parent.Text);
    }
}
public sealed class KnowledgeEmbeddingException(string code="embedding_unavailable") : Exception("Knowledge embedding is unavailable.")
{
    public string Code { get; }=code is "index_profile_mismatch" or "index_too_large"?code:"embedding_unavailable";
}
