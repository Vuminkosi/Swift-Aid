namespace SwiftAid.Infrastructure.Persistence.Repositories.Intake;

public sealed class IntakeSessionOptions
{
    public const string SectionName = "Intake:Session";

    public int CapabilityLifetimeMinutes { get; set; } = 15;

    public void Validate()
    {
        if (CapabilityLifetimeMinutes is < 1 or > 60)
        {
            throw new InvalidOperationException("Intake session limits are outside supported bounds.");
        }
    }
}
