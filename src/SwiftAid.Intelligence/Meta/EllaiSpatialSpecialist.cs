using SwiftAid.Application.Ellai;
using SwiftAid.Application.Ellai.Specialists;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Intelligence.Meta;

/// <summary>Deterministic specialist for bounded authorized regional counts; it does not infer hotspots.</summary>
public sealed class EllaiSpatialSpecialist
{
    public async Task<EllaiSpecialistResult> RunAsync(string task, bool enabled, EllaiToolContext parent,
        EllaiFrameworkUsage usage, Func<CancellationToken, Task<bool>> isCurrent,
        Func<EllaiToolContext, EllaiToolRequest, CancellationToken, Task<EllaiToolResult>> invoke,
        Func<EllaiToolContext, EllaiToolRequest, CancellationToken, Task<EllaiToolResult>> revalidate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(isCurrent);
        ArgumentNullException.ThrowIfNull(invoke);
        ArgumentNullException.ThrowIfNull(revalidate);
        if (string.IsNullOrWhiteSpace(task) || task.Length > 2048) return new(EllaiSpecialistOutcome.InvalidTask, null);
        if (parent.Actor is null || !string.Equals(parent.SessionId, usage.SessionId, StringComparison.Ordinal) ||
            !EllaiSpecialistRegistry.TryCreateToolContext(EllaiSpecialistRegistry.Spatial, enabled, parent, out var context) ||
            context!.AllowedTools is not { Count: > 0 }) return new(EllaiSpecialistOutcome.Denied, null);
        if (!await isCurrent(ct).ConfigureAwait(false)) return new(EllaiSpecialistOutcome.Stale, null);
        var request = new EllaiToolRequest(EllaiToolNames.OwnedRegionCounts, new Dictionary<string, string>());
        var result = await invoke(context, request, ct).ConfigureAwait(false);
        if (!result.Allowed || result.Truncated || result.Evidence.Count != 1 ||
            result.Evidence[0] is not { SourceKind: "SpatialAggregate", ResourceId: "owned-regions", Version: "region-count/1" })
            return new(EllaiSpecialistOutcome.Unverified, null);
        var fresh = await revalidate(context, request, ct).ConfigureAwait(false);
        if (!fresh.Allowed || fresh.Truncated || !fresh.Evidence.SequenceEqual(result.Evidence) || !await isCurrent(ct).ConfigureAwait(false))
            return new(EllaiSpecialistOutcome.Stale, null);
        var decision = new EllaiDecision(EllaiDecision.CurrentSchemaVersion, DecisionKind.Answer,
            fresh.ResultSummary, fresh.ResultSummary,
            [new GroundedClaim("owned-region-counts", fresh.ResultSummary, ClaimKind.Observed, fresh.Evidence)],
            [], [], null, [], null, null);
        return EllaiDecisionValidator.Validate(decision).Count == 0
            ? new(EllaiSpecialistOutcome.Completed, decision) : new(EllaiSpecialistOutcome.Unverified, null);
    }
}
