namespace SwiftAid.Client.Presentation;

/// <summary>Fail-closed education catalogue until an authorized live provider is available.</summary>
public sealed class UnavailableEducationClient : IEducationClient
{
    public Task<UiCapabilityResult<Page<EducationMaterial>>> ListAsync(
        EducationQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(UiCapabilityResults.Unavailable<Page<EducationMaterial>>("education"));
    }
}
