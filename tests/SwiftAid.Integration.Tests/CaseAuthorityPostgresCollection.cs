namespace SwiftAid.Integration.Tests;

/// <summary>Serializes fixtures that provision the shared PostgreSQL authority helper.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CaseAuthorityPostgresCollection
{
    public const string Name = "SwiftAid PostgreSQL case authority";
}
