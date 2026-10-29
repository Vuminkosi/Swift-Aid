namespace SwiftAid.Infrastructure.Configuration;

/// <summary>Development-only demo dataset import settings. Production must leave this disabled.</summary>
public sealed class DemoDataOptions
{
    public const string SectionName = "DemoData";

    public bool Enabled { get; init; }

    public int MaxCases { get; init; } = 1000;

    public void Validate()
    {
        if (MaxCases is < 1 or > 5000)
        {
            throw new InvalidOperationException("DemoData:MaxCases must be between one and 5000.");
        }
    }
}
