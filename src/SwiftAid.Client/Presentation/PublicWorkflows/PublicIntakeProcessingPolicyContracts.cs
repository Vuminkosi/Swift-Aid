namespace SwiftAid.Client.Presentation.PublicWorkflows;

/// <summary>Public, non-secret descriptor for the current report-handling terms.</summary>
public sealed record PublicReportProcessingPolicy(
    string Reference,
    string Version,
    string? NoticeText = null,
    string? AcknowledgementLabel = null);

public interface IPublicIntakeProcessingPolicyClient
{
    Task<UiCapabilityResult<PublicReportProcessingPolicy>> GetPolicyAsync(
        CancellationToken cancellationToken = default);
}

public sealed class UnavailablePublicIntakeProcessingPolicyClient : IPublicIntakeProcessingPolicyClient
{
    public Task<UiCapabilityResult<PublicReportProcessingPolicy>> GetPolicyAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(UiCapabilityResults.Unavailable<PublicReportProcessingPolicy>("public-intake-processing-policy"));
    }
}
