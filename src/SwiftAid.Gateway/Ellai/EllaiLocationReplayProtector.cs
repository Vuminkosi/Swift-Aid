using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using SwiftAid.Application.Ellai;

namespace SwiftAid.Gateway.Ellai;

/// <summary>Protects location search inputs with the Gateway's durable data-protection key ring.</summary>
public sealed class EllaiLocationReplayProtector(IDataProtectionProvider provider) : IEllaiLocationReplayProtector
{
    public string Protect(EllaiToolContext context, IReadOnlyDictionary<string, string> arguments)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(arguments);
        if (data.Length > 2048) throw new ArgumentException("Location search arguments are too large.", nameof(arguments));
        return Convert.ToBase64String(Protector(context).Protect(data));
    }

    public IReadOnlyDictionary<string, string>? Unprotect(EllaiToolContext context, string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096) return null;
        try
        {
            var data = Protector(context).Unprotect(Convert.FromBase64String(token));
            if (data.Length > 2048) return null;
            return JsonSerializer.Deserialize<Dictionary<string, string>>(data);
        }
        catch (Exception error) when (error is CryptographicException or FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    private IDataProtector Protector(EllaiToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.SessionId) || string.IsNullOrWhiteSpace(context.Actor?.ActorId))
            throw new ArgumentException("A session and actor are required.", nameof(context));
        return provider.CreateProtector("SwiftAid.Ellai.LocationReplay.v1", context.SessionId, context.Actor.ActorId);
    }
}
