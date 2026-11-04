namespace SwiftAid.Infrastructure.Intake;

/// <summary>PC-09 platform coordination queue configuration.</summary>
/// <remarks>
/// The coordination organization is a configured platform scope, not an inferred
/// catchment: coordinator authority derives only from an explicit
/// <c>CoordinateIntake</c> grant inside this organization. Geographic proximity never
/// grants access.
/// </remarks>
public sealed record IntakeCoordinationOptions
{
    public const string SectionName = "Intake:Coordination";

    public bool Enabled { get; init; }

    public string CoordinationOrganizationId { get; init; } = string.Empty;

    public int DefaultPageLimit { get; init; } = 50;

    public int MaximumPageLimit { get; init; } = 200;

    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(CoordinationOrganizationId) ||
            CoordinationOrganizationId.Length > 128)
        {
            throw new ArgumentException(
                "Intake coordination requires a configured coordination organization.",
                nameof(IntakeCoordinationOptions));
        }
    }
}
