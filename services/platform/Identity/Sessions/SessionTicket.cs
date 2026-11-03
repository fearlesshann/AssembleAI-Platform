using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.WebUtilities;

namespace AssembleAI.Platform.Identity.Sessions;

internal static class SessionTicket
{
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromHours(8);

    internal static string NewKey() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    internal static Snapshot Copy(AuthenticationTicket ticket, DateTimeOffset now, DateTimeOffset? deadline = null)
    {
        try
        {
            var copy = TicketSerializer.Default.Deserialize(TicketSerializer.Default.Serialize(ticket))
                ?? throw new BffSessionStoreException();
            var expires = (copy.Properties.ExpiresUtc ?? now.Add(MaximumLifetime)).ToUniversalTime();
            if (expires > now.Add(MaximumLifetime)) expires = now.Add(MaximumLifetime);
            if (deadline.HasValue && expires > deadline.Value) expires = deadline.Value;
            copy.Properties.ExpiresUtc = expires;
            // AuthenticationProperties stores UTC timestamps at whole-second precision.
            // Use that same deadline for metadata so a store never outlives its ticket.
            return new Snapshot(TicketSerializer.Default.Serialize(copy), copy.Properties.ExpiresUtc!.Value);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException)
        {
            // Serialization errors can include ticket material; do not retain them as inner exceptions.
            throw new BffSessionStoreException();
        }
    }

    internal sealed record Snapshot(byte[] Bytes, DateTimeOffset ExpiresUtc);
}
