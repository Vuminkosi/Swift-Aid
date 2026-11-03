namespace SwiftAid.Contracts.Ellai;

/// <summary>Value-free, server-approved report validation prompts.</summary>
public static class EllaiValidationPrompts
{
    private static readonly IReadOnlyDictionary<string, string> Prompts = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["narrative-required"] = "Describe what happened or add an observation.",
        ["narrative-length"] = "Keep the description under 6,000 characters.",
        ["observations-length"] = "Use no more than 20 observations, with each under 1,000 characters.",
        ["date-required"] = "Add the reported date or choose that the time is unknown.",
        ["time-required"] = "Enter the reported local time.",
        ["offset-invalid"] = "Enter a valid time difference from UTC, such as +02:00.",
        ["region-required"] = "Enter the region, or mark it unknown.",
        ["area-required"] = "Enter the area or district, or mark it unknown.",
        ["locality-required"] = "Enter the locality, or mark it unknown.",
        ["location-required"] = "Add a reported place or mark the location unknown."
    };

    public static string? Resolve(string? code) => code is not null && Prompts.TryGetValue(code, out var text) ? text : null;

    public static string? CodeFor(string? message) =>
        Prompts.FirstOrDefault(pair => string.Equals(pair.Value, message, StringComparison.Ordinal)).Key;
}
