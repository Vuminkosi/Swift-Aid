namespace SwiftAid.Infrastructure.Classification;

public sealed class TranslationOptions
{
    public bool Enabled { get; set; }
    public bool ResearchModeOnly { get; set; } = true;
    public bool RequireHumanReview { get; set; } = true;
    public string[] Organizations { get; set; } = [];
    public string[] Reviewers { get; set; } = [];
    public bool Allows(string organization, string environment) => Enabled && ResearchModeOnly && RequireHumanReview &&
        environment is "Development" or "Test" && Organizations.Contains(organization, StringComparer.Ordinal);
}
