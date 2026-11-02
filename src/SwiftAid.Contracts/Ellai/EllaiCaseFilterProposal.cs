using System.Globalization;

namespace SwiftAid.Contracts.Ellai;

/// <summary>Presentation-only filter request. Current component options and application access still apply.</summary>
public static class EllaiCaseFilterProposal
{
    public static IReadOnlyList<string> Keys { get; } = Array.AsReadOnly(new[]
        { "search", "mobile-search", "sector", "status", "country", "region", "assignee", "category", "from", "to", "sort", "clear" });
    public static bool IsValid(UiIntentProposal? proposal)
    {
        if (proposal is not { Kind: UiIntentKind.SetPermittedFilter, FeatureId: "case-list", ResourceId: null, ControlId: { } id } ||
            !id.StartsWith("case-list.", StringComparison.Ordinal) || !Keys.Contains(id[10..]) || proposal.Arguments is null) return false;
        var key = id[10..];
        if (key == "clear") return proposal.Arguments.Count == 0;
        if (proposal.Arguments.Count != 1 || !proposal.Arguments.TryGetValue("value", out var value) ||
            value is null || value.Length > 200 || value.Any(char.IsControl)) return false;
        return key switch
        {
            "sort" => value is "newest" or "oldest",
            "from" or "to" => value.Length == 0 || DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            _ => true,
        };
    }
}
