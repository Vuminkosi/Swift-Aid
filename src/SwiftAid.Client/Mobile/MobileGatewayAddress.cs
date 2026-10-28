namespace SwiftAid.Client.Mobile;

/// <summary>
/// Resolves the gateway/authority base address for the MAUI host.
/// On the Android emulator, a loopback host (localhost, 127.0.0.1, ::1) never
/// reaches the developer machine, so it is rewritten to the emulator alias
/// 10.0.2.2. Everywhere else the configured address is used untouched; a
/// loopback address on a physical Android device fails fast with guidance
/// instead of silently pointing at the device itself.
/// </summary>
public static class MobileGatewayAddress
{
    /// <summary>Android emulator alias for the host loopback interface.</summary>
    public const string AndroidEmulatorLoopback = "10.0.2.2";

    /// <summary>
    /// Resolves a configured HTTPS base address, or null when unconfigured.
    /// </summary>
    /// <param name="configured">The configured address text (may be null/empty).</param>
    /// <param name="settingName">Configuration key, used in error messages.</param>
    /// <param name="rewriteLoopbackForAndroidEmulator">True on the Android emulator.</param>
    /// <exception cref="InvalidOperationException">The address is not a bare HTTPS URL.</exception>
    public static Uri? Resolve(string? configured, string settingName, bool rewriteLoopbackForAndroidEmulator)
    {
        if (string.IsNullOrWhiteSpace(settingName))
        {
            throw new ArgumentException("A configuration setting name is required.", nameof(settingName));
        }

        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        if (!Uri.TryCreate(configured, UriKind.Absolute, out var address) ||
            address.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(address.UserInfo) ||
            !string.IsNullOrEmpty(address.Query) ||
            !string.IsNullOrEmpty(address.Fragment))
        {
            throw new InvalidOperationException($"{settingName} must be an HTTPS URL without credentials, query, or fragment.");
        }

        if (rewriteLoopbackForAndroidEmulator && address.IsLoopback)
        {
            address = new UriBuilder(address) { Host = AndroidEmulatorLoopback }.Uri;
        }

        return address;
    }

    /// <summary>
    /// True when the configured text parses to a loopback URI. Used by the host
    /// to fail fast on physical devices, where loopback can never be correct.
    /// </summary>
    public static bool IsLoopbackConfigured(string? configured) =>
        !string.IsNullOrWhiteSpace(configured) &&
        Uri.TryCreate(configured, UriKind.Absolute, out var address) &&
        address.IsLoopback;
}
