using System.Text;
using SwiftAid.Application.Ellai;

namespace SwiftAid.Application.Intake;

/// <summary>Checks ownership, report-processing consent and budget around transient report extraction.</summary>
public sealed class ReportVoiceExtractionAdmission(
    IEllaiSessionStore sessions,
    EllaiConsentService consent,
    IEllaiConsentStore consentStore,
    EllaiUsageLedger ledger,
    IEllaiUsageStore usage,
    ReportVoiceExtraction extraction,
    TimeProvider? timeProvider = null)
{
    public const string ConsentPurpose = "public-report-voice-extraction";
    public const string ConsentDataClass = "public-report-transcript";
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <param name="ownerActorId">Server-resolved owner, never a value supplied by the model or request body.</param>
    public async Task<ReportVoiceAdmissionResult> ExtractAsync(
        string sessionId, string ownerActorId, string transcript, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(ownerActorId) ||
            string.IsNullOrWhiteSpace(transcript) || transcript.Length > ReportVoiceExtraction.MaximumTranscriptLength)
        {
            return new("InvalidRequest");
        }

        var session = await sessions.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null || session.OwnerActorId != ownerActorId || session.Status != "Active")
        {
            return new("Unavailable");
        }

        if (!await HasConsentAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            return new("ConsentRequired");
        }

        await ledger.HydrateAsync(sessionId, usage, cancellationToken).ConfigureAwait(false);
        var estimatedPrompt = Encoding.UTF8.GetByteCount(transcript) + 4096;
        var estimate = estimatedPrompt + ReportVoiceExtraction.MaximumCompletionTokens;
        using var reservation = ledger.TryBeginModelCall(sessionId, estimate);
        if (reservation is null)
        {
            return new("BudgetUnavailable");
        }

        ReportVoiceExtractionResult? result = null;
        try
        {
            result = await extraction.ExtractAsync(transcript, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // An interrupted provider request may still be billable. Record conservative
            // estimates when no usage was returned, using an independent bounded cleanup.
            var sample = new EllaiUsageSample(sessionId, result?.PromptTokens ?? 0, result?.CompletionTokens ?? 0);
            var estimatedInput = sample.PromptTokens == 0 ? estimatedPrompt : 0;
            var estimatedOutput = sample.CompletionTokens == 0 ? ReportVoiceExtraction.MaximumCompletionTokens : 0;
            ledger.Record(sample, estimatedInput, estimatedOutput);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await usage.RecordAsync(new StoredUsageSample(sessionId, sample.PromptTokens, sample.CompletionTokens,
                ledger.DollarsFor(sample), _clock.GetUtcNow())
            {
                EstimatedPromptTokens = estimatedInput,
                EstimatedCompletionTokens = estimatedOutput,
                EstimatedDollars = ledger.Prices.CostFor(estimatedInput, estimatedOutput)
            }, cleanup.Token).ConfigureAwait(false);
        }

        var current = await sessions.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (current is null || current.Status != "Active" || current.OwnerActorId != ownerActorId ||
            current.ScopeEpoch != session.ScopeEpoch || !await HasConsentAsync(sessionId, cancellationToken).ConfigureAwait(false))
        {
            return new("Stale");
        }

        return result.Available ? new("Ready", result.Fields) : new("Unavailable");
    }

    private async Task<bool> HasConsentAsync(string sessionId, CancellationToken cancellationToken)
    {
        await consent.HydrateAsync(sessionId, consentStore, cancellationToken).ConfigureAwait(false);
        return consent.IsGranted(sessionId, EllaiConsentDimension.ReasoningText,
            ConsentPurpose, ConsentDataClass, _clock.GetUtcNow());
    }
}

public sealed record ReportVoiceAdmissionResult(string Status, IReadOnlyList<ReportVoiceFieldProposal>? Fields = null);
