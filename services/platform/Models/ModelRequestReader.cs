using System.Text;
using System.Text.Json;

namespace AssembleAI.Platform.Models;

internal static class ModelRequestReader
{
    internal static async Task<JsonDocument?> ReadAsync(HttpRequest request, CancellationToken token)
    {
        if (request.ContentLength > 65536 || request.Headers.ContentEncoding.Count != 0
            || !request.HasJsonContentType()) return null;
        await using var buffer = new MemoryStream();
        var bytes = new byte[4096];
        int count;
        while ((count = await request.Body.ReadAsync(bytes, token)) != 0)
        {
            if (buffer.Length + count > 65536) return null;
            await buffer.WriteAsync(bytes.AsMemory(0, count), token);
        }
        try { return JsonDocument.Parse(buffer.ToArray(), new() { MaxDepth = 8 }); }
        catch (JsonException) { return null; }
    }
    internal static bool Fields(JsonElement body, params string[] allowed) => body.ValueKind == JsonValueKind.Object
        && body.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == body.EnumerateObject().Count()
        && body.EnumerateObject().All(p => allowed.Contains(p.Name, StringComparer.Ordinal));
    internal static ModelChatRequest? Chat(JsonElement body, int maximum)
    {
        if (!Fields(body, "messages", "maxOutputTokens", "temperature", "stream")
            || !body.TryGetProperty("messages", out var items) || items.ValueKind != JsonValueKind.Array
            || items.GetArrayLength() is < 1 or > 64) return null;
        var messages = new List<ModelMessage>();
        foreach (var message in items.EnumerateArray())
        {
            if (!Fields(message, "role", "content") || !message.TryGetProperty("role", out var role)
                || role.ValueKind != JsonValueKind.String || role.GetString() is not ("system" or "user" or "assistant")
                || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(content.GetString())) return null;
            messages.Add(new(role.GetString()!, content.GetString()!));
        }
        var output = maximum;
        if (body.TryGetProperty("maxOutputTokens", out var max) && (max.ValueKind != JsonValueKind.Number
            || !max.TryGetInt32(out output) || output < 1 || output > maximum)) return null;
        double? temperature = null;
        if (body.TryGetProperty("temperature", out var temp))
        {
            if (temp.ValueKind != JsonValueKind.Number || !temp.TryGetDouble(out var value) || !double.IsFinite(value) || value is < 0 or > 2) return null;
            temperature = value;
        }
        var stream = false;
        if (body.TryGetProperty("stream", out var streaming))
        {
            if (streaming.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
            stream = streaming.GetBoolean();
        }
        return new(messages, output, temperature, stream);
    }
}
