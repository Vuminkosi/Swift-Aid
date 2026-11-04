using Microsoft.Extensions.Options;
using SwiftAid.Client.Presentation;
using SwiftAid.Client.Presentation.PublicWorkflows;
using SwiftAid.Infrastructure.Persistence.Repositories.Intake;

namespace SwiftAid.Gateway.Presentation;

/// <summary>Exposes only the public processing reference/version used by the intake validator.</summary>
public sealed class GatewayPublicIntakeProcessingPolicyClient(
    IOptions<IntakeConsentOptions> options) : IPublicIntakeProcessingPolicyClient
{
    private readonly IntakeConsentOptions _options = options?.Value
        ?? throw new ArgumentNullException(nameof(options));

    public Task<UiCapabilityResult<PublicReportProcessingPolicy>> GetPolicyAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var policy = new PublicReportProcessingPolicy(
            _options.ProcessingPolicyReference,
            _options.ProcessingPolicyVersion);
        if (!_options.IsValid || !policy.IsValid)
        {
            return Task.FromResult(UiCapabilityResults.Unavailable<PublicReportProcessingPolicy>(
                "public-intake-processing-policy"));
        }

        var metadata = new UiCapabilityMetadata(
            "public-intake-processing-policy",
            UiCapabilityResults.ContractVersion,
            UiCapabilityProvenance.LiveService,
            DateTimeOffset.UtcNow,
            IsAvailable: true);
        return Task.FromResult(new UiCapabilityResult<PublicReportProcessingPolicy>(
            UiCapabilityResultCode.Success,
            policy,
            metadata));
    }

}
