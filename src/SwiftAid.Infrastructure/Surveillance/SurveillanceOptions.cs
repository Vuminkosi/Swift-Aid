namespace SwiftAid.Infrastructure.Surveillance;

public sealed class SurveillanceOptions
{
    public bool Enabled { get; set; }
    public string Mode { get; set; } = "Disabled";
    public string[] Workspaces { get; set; } = [];
    public string[] Organizations { get; set; } = [];

    public void Validate()
    {
        if (!Enabled) return;
        if (Mode != "SyntheticEvaluation" || Workspaces.Length is < 1 or > 100 || Organizations.Length is < 1 or > 100 ||
            Workspaces.Any(string.IsNullOrWhiteSpace) || Organizations.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("Surveillance requires explicitly scoped synthetic evaluation configuration. Production thresholds are not validated.");
    }
}
