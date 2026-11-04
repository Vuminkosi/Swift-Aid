namespace SwiftAid.Contracts.Ellai;

/// <summary>Value-free, server-approved report validation prompts.</summary>
public static class EllaiValidationPrompts
{
    private sealed record Prompt(string ValidationMessage, string Narration);

    private static readonly IReadOnlyDictionary<string, Prompt> Prompts = new Dictionary<string, Prompt>(StringComparer.Ordinal)
    {
        ["narrative-required"] = new("Describe what happened or add an observation.", "I haven't captured a report description yet. Describe what happened or add an observation."),
        ["narrative-length"] = new("Keep the description under 6,000 characters.", "The report description is too long to capture. Shorten it to under 6,000 characters."),
        ["age-invalid"] = new("Enter age in whole years, or leave it blank if unknown.", "The reported age isn't captured as a valid whole number. Enter age in whole years, or leave it blank if unknown."),
        ["observations-length"] = new("Use no more than 20 observations, with each under 1,000 characters.", "The observations aren't captured because there are too many or one is too long. Use no more than 20 observations, each under 1,000 characters."),
        ["date-required"] = new("Add the reported date or choose that the time is unknown.", "I haven't captured the reported date. Add it, or choose that the time is unknown."),
        ["time-required"] = new("Enter the reported local time.", "I haven't captured a valid reported local time. Enter the time, or choose that it is unknown."),
        ["offset-invalid"] = new("Enter a valid time difference from UTC, such as +02:00.", "The reported time difference isn't valid, so I can't capture the time accurately. Enter a valid difference from UTC, such as +02:00."),
        ["region-required"] = new("Enter the region, or mark it unknown.", "I haven't captured the reported region. Enter it, or mark it unknown."),
        ["area-required"] = new("Enter the area or district, or mark it unknown.", "I haven't captured the reported area or district. Enter it, or mark it unknown."),
        ["locality-required"] = new("Enter the locality, or mark it unknown.", "I haven't captured the reported locality. Enter it, or mark it unknown."),
        ["region-length"] = new("Use no more than 200 characters for the region, or mark it unknown.", "The reported region is too long to capture. Use no more than 200 characters, or mark it unknown."),
        ["area-length"] = new("Use no more than 200 characters for the area or district, or mark it unknown.", "The reported area or district is too long to capture. Use no more than 200 characters, or mark it unknown."),
        ["locality-length"] = new("Use no more than 200 characters for the locality, or mark it unknown.", "The reported locality is too long to capture. Use no more than 200 characters, or mark it unknown."),
        ["location-required"] = new("Add a reported place or mark the location unknown.", "I haven't captured a reported place. Add the place, or mark the location unknown.")
    };

    public static string? Resolve(string? code) => code is not null && Prompts.TryGetValue(code, out var prompt) ? prompt.Narration : null;

    public static string? CodeFor(string? message) =>
        Prompts.FirstOrDefault(pair => string.Equals(pair.Value.ValidationMessage, message, StringComparison.Ordinal)).Key;
}
