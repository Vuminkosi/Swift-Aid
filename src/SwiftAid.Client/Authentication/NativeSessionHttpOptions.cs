namespace SwiftAid.Client.Authentication;

/// <summary>Validated HTTPS origin and base path for a native authenticated HTTP client.</summary>
public sealed class NativeSessionHttpOptions
{
    public NativeSessionHttpOptions(Uri gatewayBaseAddress)
    {
        ArgumentNullException.ThrowIfNull(gatewayBaseAddress);

        if (!gatewayBaseAddress.IsAbsoluteUri ||
            gatewayBaseAddress.Scheme != Uri.UriSchemeHttps ||
            gatewayBaseAddress.UserInfo.Length != 0 ||
            gatewayBaseAddress.Query.Length != 0 ||
            gatewayBaseAddress.Fragment.Length != 0)
        {
            throw new ArgumentException(
                "The notification gateway address must be an absolute HTTPS URL without credentials, query, or fragment.",
                nameof(gatewayBaseAddress));
        }

        GatewayBaseAddress = EnsureTrailingSlash(gatewayBaseAddress);
    }

    public Uri GatewayBaseAddress { get; }

    public bool IsWithinConfiguredGateway(Uri destination)
    {
        if (!destination.IsAbsoluteUri ||
            destination.Scheme != Uri.UriSchemeHttps ||
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
