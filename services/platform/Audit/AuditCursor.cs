using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace AssembleAI.Platform.Audit;

public sealed record AuditPosition(DateTimeOffset Time, string Source, string Id);
public sealed class AuditCursor(IDataProtectionProvider protection, TimeProvider clock)
{
    private readonly IDataProtector protector = protection.CreateProtector("AssembleAI.Audit.Cursor.v1");
    private sealed record Envelope(string Binding, DateTimeOffset Expires, AuditPosition Position);
    public string Encode(string binding, AuditPosition position) => protector.Protect(JsonSerializer.Serialize(new Envelope(binding, clock.GetUtcNow().AddMinutes(15), position)));
    public AuditPosition Decode(string text, string binding)
    {
        try
        {
            var value = JsonSerializer.Deserialize<Envelope>(protector.Unprotect(text));
            if (value is null || value.Binding != binding || value.Expires <= clock.GetUtcNow() || value.Position is null
                || !AuditQuery.Sources.Contains(value.Position.Source) || value.Position.Id.Length != 32) throw new AuditRequestException(400);
            return value.Position;
        }
        catch (Exception error) when (error is CryptographicException or JsonException or ArgumentException or FormatException)
        { throw new AuditRequestException(400); }
    }
}
