using SwiftAid.Application.Ellai;
using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Intake;
using SwiftAid.Intelligence.Meta;

namespace SwiftAid.Gateway.Ellai;

/// <summary>Session-owned, explicitly consented report extraction, separate from Ellai conversation turns.</summary>
public sealed class ReportVoiceGatewayService(
    IEllaiSessionStore sessions,
    EllaiConsentService consent,
    IEllaiConsentStore consentStore,
    ReportVoiceExtractionAdmission admission,
    EllaiOptions options)
{
    public async Task<bool> GrantAsync(string sessionId, string actorId, CancellationToken cancellationToken)
    {
        var session = await sessions.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (!options.Enabled || session is null || session.Status != "Active" || session.OwnerActorId != actorId)
        {
            return false;
        }

        var grant = consent.Grant(sessionId, EllaiConsentDimension.ReasoningText,
            ReportVoiceExtractionAdmission.ConsentPurpose, ReportVoiceExtractionAdmission.ConsentDataClass,
            TimeSpan.FromHours(1));
        try
        {
            await consentStore.RecordAsync(grant, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            consent.Revoke(grant.GrantId);
            throw;
        }
    }

    public async Task<ReportVoiceExtractionResponse> ExtractAsync(
        string sessionId, string actorId, string transcript, CancellationToken cancellationToken)
    {
        if (!options.Enabled) return new("Unavailable", []);
        var result = await admission.ExtractAsync(sessionId, actorId, transcript, cancellationToken)
            .ConfigureAwait(false);
        return new(result.Status, result.Fields?.Select(field =>
            new ReportVoiceSuggestion(field.Field, field.Value, field.Evidence)).ToArray() ?? []);
    }
}
