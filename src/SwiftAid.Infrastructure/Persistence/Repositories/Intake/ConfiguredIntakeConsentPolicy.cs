using Microsoft.Extensions.Options;
using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Intake;

namespace SwiftAid.Infrastructure.Persistence.Repositories.Intake;

/// <summary>Accepts only explicit confirmations tied to the currently configured policy versions.</summary>
public sealed class ConfiguredIntakeConsentPolicy(IOptions<IntakeConsentOptions> options) : IIntakeConsentPolicy
{
    private readonly IntakeConsentOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public Task<bool> IsCurrentAsync(IntakeConfirmations confirmations, bool includesAudio, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.IsValid || confirmations is null || !confirmations.ProcessingConfirmed ||
            !string.Equals(confirmations.ProcessingPolicyReference, _options.ProcessingPolicyReference, StringComparison.Ordinal) ||
            !string.Equals(confirmations.ProcessingPolicyVersion, _options.ProcessingPolicyVersion, StringComparison.Ordinal))
        {
            return Task.FromResult(false);
        }

        var recordingMatches = includesAudio
            ? confirmations.RecordingConfirmed is true &&
              string.Equals(confirmations.RecordingPolicyReference, _options.RecordingPolicyReference, StringComparison.Ordinal) &&
              string.Equals(confirmations.RecordingPolicyVersion, _options.RecordingPolicyVersion, StringComparison.Ordinal)
            : confirmations.RecordingConfirmed is null && confirmations.RecordingPolicyReference is null &&
              confirmations.RecordingPolicyVersion is null;
        return Task.FromResult(recordingMatches);
    }
}
