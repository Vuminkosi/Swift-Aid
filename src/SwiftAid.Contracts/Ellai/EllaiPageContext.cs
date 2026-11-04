namespace SwiftAid.Contracts.Ellai;

/// <summary>Advisory page context only; excludes record IDs, queries and authority.</summary>
public static class EllaiPageContext
{
    private static readonly IReadOnlyDictionary<string, string> Pages = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [""] = "Home", ["dashboard"] = "Dashboard", ["my-cases"] = "My cases",
        ["reports"] = "Reports", ["report"] = "Public report", ["managed-report"] = "Capture case",
        ["surveillance"] = "Surveillance map", ["investigations"] = "Investigations",
        ["workspace/imports"] = "Case imports", ["workspaces"] = "Workspaces",
        ["organizations"] = "Organizations", ["ellai/about"] = "Ellai About and settings",
        ["assignments"] = "Assignments", ["follow-ups"] = "Follow-ups",
        ["account"] = "Account", ["profile"] = "Profile", ["case-details"] = "Case details",
    };

    public static string? FromRelativeUri(string uri)
    {
        var path = uri.Split('?', '#')[0].Trim('/').ToLowerInvariant();
        if (path.StartsWith("case/", StringComparison.Ordinal)) return "case-details";
        return Pages.ContainsKey(path) ? path : null;
    }

    public static string? DisplayName(string? page) => page is not null && Pages.TryGetValue(page, out var name) ? name : null;

    /// <summary>An advisory route identifier, never proof of permission to read the case.</summary>
    public static Guid? CaseIdFromRelativeUri(string uri)
    {
        var parts = uri.Split('?', '#')[0].Trim('/').Split('/');
        return parts.Length == 2 && parts[0].Equals("case", StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParseExact(parts[1], "D", out var id) && id != Guid.Empty ? id : null;
    }
}
