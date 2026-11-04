namespace SwiftAid.Integration.Tests;

/// <summary>Serializes fixtures that share the disposable loopback intake database.</summary>
/// <remarks>
/// Intake seeds reuse stable identifiers (officer-1, org-zw-1) with check-then-insert
/// guards; parallel collections would race those guards into primary-key violations.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class IntakePostgresCollection
{
    public const string Name = "SwiftAid PostgreSQL intake fixtures";
}
