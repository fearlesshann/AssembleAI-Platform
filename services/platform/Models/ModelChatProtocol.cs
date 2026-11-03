namespace AssembleAI.Platform.Models;

public sealed record ModelMessage(string Role, string Content);
public sealed record ModelChatRequest(IReadOnlyList<ModelMessage> Messages, int MaxOutputTokens, double? Temperature, bool Stream);
public sealed record ModelUsage(long PromptTokens, long CompletionTokens, long TotalTokens);
public sealed record ModelChatResult(string Content, string? FinishReason, ModelUsage? Usage);
public sealed record ModelStreamEvent(string Type, string? Content = null, string? FinishReason = null, ModelUsage? Usage = null);

public sealed class ModelUpstreamException(string code) : Exception("The model provider request failed.")
{
    public string Code { get; } = code switch
    {
        "upstream_http_error" or "upstream_invalid_response" or "upstream_response_too_large"
            or "upstream_unavailable" or "upstream_incomplete_stream" => code,
        _ => "upstream_unavailable"
    };
}
