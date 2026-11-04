namespace SwiftAid.Application.Ellai;

/// <summary>Representation ceilings for APIs that require an integer bound.
/// Cancellation and authorization remain enforced; usage thresholds may be advisory.</summary>
public static class EllaiExecutionLimits
{
    public const int MaxIterations = int.MaxValue;
    public const int MaxToolCalls = int.MaxValue;
    public const int MaxRepairAttempts = int.MaxValue;
}
