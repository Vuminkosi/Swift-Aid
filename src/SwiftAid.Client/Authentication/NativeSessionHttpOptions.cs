namespace SwiftAid.Client.Authentication;

/// <summary>Validated Gateway origin and base path for a native authenticated HTTP client.</summary>
public sealed class NativeSessionHttpOptions
{
    private readonly bool _allowInsecureTransport;

    public NativeSessionHttpOptions(Uri gatewayBaseAddress, bool allowInsecureTransport = false)
    {
        ArgumentNullException.ThrowIfNull(gatewayBaseAddress);

        if (!gatewayBaseAddress.IsAbsoluteUri ||
            (gatewayBaseAddress.Scheme != Uri.UriSchemeHttps &&
                !(allowInsecureTransport && gatewayBaseAddress.Scheme == Uri.UriSchemeHttp)) ||
            gatewayBaseAddress.UserInfo.Length != 0 ||
            gatewayBaseAddress.Query.Length != 0 ||
            gatewayBaseAddress.Fragment.Length != 0)
        {
            throw new ArgumentException(
                "The notification gateway address must be an absolute HTTPS URL without credentials, query, or fragment. HTTP requires an explicit development-only override.",
                nameof(gatewayBaseAddress));
        }

        _allowInsecureTransport = allowInsecureTransport;
        GatewayBaseAddress = EnsureTrailingSlash(gatewayBaseAddress);
    }

    public Uri GatewayBaseAddress { get; }

    public bool IsWithinConfiguredGateway(Uri destination)
    {
        if (!destination.IsAbsoluteUri ||
            destination.Scheme != GatewayBaseAddress.Scheme ||
            (destination.Scheme != Uri.UriSchemeHttps && !_allowInsecureTransport) ||
            destination.UserInfo.Length != 0 ||
            !string.Equals(destination.IdnHost, GatewayBaseAddress.IdnHost, StringComparison.OrdinalIgnoreCase) ||
            destination.Port != GatewayBaseAddress.Port)
        {
            return false;
        }

        var basePath = GatewayBaseAddress.AbsolutePath;
        if (basePath == "/")
        {
            return true;
        }

        var pathPrefix = basePath.EndsWith("/", StringComparison.Ordinal)
            ? basePath
            : basePath + "/";
        return destination.AbsolutePath.StartsWith(pathPrefix, StringComparison.Ordinal) ||
            string.Equals(
                destination.AbsolutePath.TrimEnd('/'),
                basePath.TrimEnd('/'),
                StringComparison.Ordinal);
    }

    private static Uri EnsureTrailingSlash(Uri uri)
    {
        if (uri.AbsolutePath.EndsWith("/", StringComparison.Ordinal))
        {
            return uri;
        }

        return new Uri(uri.AbsoluteUri + "/", UriKind.Absolute);
    }
}
