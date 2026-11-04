using SwiftAid.Application.Ellai;
using SwiftAid.Application.Ellai.Specialists;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Intelligence.Meta;

/// <summary>Returns approved source metadata only; never generates medical advice or health education.</summary>
public sealed class EllaiHealthResearchSpecialist
{
    public async Task<EllaiSpecialistResult> RunAsync(string topic, string jurisdiction, string language, bool enabled,
        EllaiToolContext parent, EllaiFrameworkUsage usage, Func<CancellationToken, Task<bool>> isCurrent,
        Func<EllaiToolContext, EllaiToolRequest, CancellationToken, Task<EllaiToolResult>> invoke,
        Func<EllaiToolContext, EllaiToolRequest, CancellationToken, Task<EllaiToolResult>> revalidate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(isCurrent);
        ArgumentNullException.ThrowIfNull(invoke);
        ArgumentNullException.ThrowIfNull(revalidate);
        if (string.IsNullOrWhiteSpace(topic) || string.IsNullOrWhiteSpace(jurisdiction) || string.IsNullOrWhiteSpace(language))
            return new(EllaiSpecialistOutcome.InvalidTask, null);
        if (parent.Actor is null || !string.Equals(parent.SessionId, usage.SessionId, StringComparison.Ordinal) ||
            !EllaiSpecialistRegistry.TryCreateToolContext(EllaiSpecialistRegistry.HealthResearch, enabled, parent, out var context) ||
            context!.AllowedTools is not { Count: > 0 }) return new(EllaiSpecialistOutcome.Denied, null);
        if (!await isCurrent(ct).ConfigureAwait(false)) return new(EllaiSpecialistOutcome.Stale, null);
        var request = new EllaiToolRequest(EllaiToolNames.ApprovedHealthSources, new Dictionary<string, string>
        { ["topic"] = topic, ["jurisdiction"] = jurisdiction, ["language"] = language });
        var result = await invoke(context, request, ct).ConfigureAwait(false);
        if (!result.Allowed || result.Truncated || result.HealthSourceReplayScope is not { } scope || !scope.IsValid() ||
            result.Evidence.Count != 1 || result.Evidence[0] is not { SourceKind: "HealthSourceRegistry", ResourceId: "approved-sources", Version: "metadata/1" })
            return new(EllaiSpecialistOutcome.Unverified, null);
        var fresh = await revalidate(context, request, ct).ConfigureAwait(false);
        if (!fresh.Allowed || fresh.Truncated || !fresh.Evidence.SequenceEqual(result.Evidence) ||
            EllaiObservedToolResult.ResultFingerprint(fresh) != scope.ResultDigest || !await isCurrent(ct).ConfigureAwait(false))
            return new(EllaiSpecialistOutcome.Stale, null);
        var decision = new EllaiDecision(EllaiDecision.CurrentSchemaVersion, DecisionKind.Answer,
            fresh.ResultSummary, fresh.ResultSummary,
            [new GroundedClaim("approved-health-sources", fresh.ResultSummary, ClaimKind.Observed, fresh.Evidence)],
            [], [], null, [], null, null);
        return EllaiDecisionValidator.Validate(decision).Count == 0
            ? new(EllaiSpecialistOutcome.Completed, decision) : new(EllaiSpecialistOutcome.Unverified, null);
    }
}
