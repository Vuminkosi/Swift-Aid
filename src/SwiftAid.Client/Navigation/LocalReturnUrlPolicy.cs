namespace SwiftAid.Client.Navigation;

/// <summary>Validates same-application return paths before client-side navigation.</summary>
public static class LocalReturnUrlPolicy
{
    public static string Resolve(string? candidate, string fallback)
    {
        if (IsLocal(candidate))
        {
            return candidate!;
        }

        return IsLocal(fallback) ? fallback : "/";
    }

    public static string FromAppRelativePath(string? appRelativePath, string fallback)
    {
        if (string.IsNullOrWhiteSpace(appRelativePath))
        {
            return Resolve(null, fallback);
        }

        var localPath = appRelativePath.StartsWith("/", StringComparison.Ordinal)
            ? appRelativePath
            : $"/{appRelativePath}";

        return Resolve(localPath, fallback);
    }

    public static bool IsLocal(string? value) =>
        value is { Length: > 0 and <= 2048 } &&
        value[0] == '/' &&
        (value.Length == 1 || value[1] is not ('/' or '\\')) &&
        !value.Contains('\\') &&
        !value.Any(char.IsControl);
}
