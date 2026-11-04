namespace SwiftAid.Client.Presentation;

/// <summary>Fail-closed investigation runs until an authorized live provider is available.</summary>
public sealed class UnavailableInvestigationClient : IAiInvestigationClient
{
    public Task<UiCapabilityResult<InvestigationSnapshot>> GetAsync(
        string investigationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(investigationId))
        {
            return Task.FromResult(new UiCapabilityResult<InvestigationSnapshot>(
                UiCapabilityResultCode.InvalidRequest,
                null,
                UiCapabilityResults.UnavailableMetadata("investigations", "Choose a valid investigation."),
                "Choose a valid investigation."));
        }

        return Task.FromResult(UiCapabilityResults.Unavailable<InvestigationSnapshot>("investigations"));
    }

    public Task<UiCapabilityResult<InvestigationSnapshot>> StartAsync(
        InvestigationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(UiCapabilityResults.Unavailable<InvestigationSnapshot>("investigations"));
    }
}
