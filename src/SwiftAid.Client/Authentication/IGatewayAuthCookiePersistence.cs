namespace SwiftAid.Client.Authentication;

/// <summary>Stores only the persistent Gateway auth cookie in platform-protected storage.</summary>
public interface IGatewayAuthCookiePersistence
{
    Task<GatewayAuthCookieSnapshot?> ReadAsync(CancellationToken cancellationToken = default);

    Task WriteAsync(GatewayAuthCookieSnapshot snapshot, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}
