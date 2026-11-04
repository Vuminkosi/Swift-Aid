namespace SwiftAid.Client.Authentication;

/// <summary>Read-only access to the active native session for authenticated API clients.</summary>
public interface INativeSessionReader
{
    Task<NativeLoginSession?> ReadAsync(CancellationToken cancellationToken = default);
}
