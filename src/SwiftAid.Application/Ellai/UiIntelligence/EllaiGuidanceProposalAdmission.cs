using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>Server-resolved grants. Never bind this record from an HTTP request or model output.</summary>
public sealed record EllaiUiEntitlements(string OwnerActorId, string ScopeEpoch,
    IReadOnlySet<string> Features, IReadOnlySet<string> Capabilities);

public sealed record EllaiGuidanceAdmission(string Status, string? Objective = null,
    UiRegistrySnapshot? Catalogue = null, StoredUiObservationReport? Observation = null,
    long InteractionEpoch = 0);

/// <summary>Resolves a durable proposal and fences it against session ownership, scope,
/// newer turns and current UI evidence before the existing model planner is invoked.</summary>
public sealed class EllaiGuidanceProposalAdmission(
    IEllaiSessionStore sessions, IEllaiUtteranceOutboxStore outbox,
    EllaiUiRegistry registry, IEllaiUiObservationStore observations, TimeProvider? clock = null)
{
    public async Task<EllaiGuidanceAdmission> AdmitAsync(string sessionId, string proposalId,
        EllaiUiEntitlements authority, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (string.IsNullOrWhiteSpace(authority.OwnerActorId) || string.IsNullOrWhiteSpace(authority.ScopeEpoch))
            return new("Denied");
        var session = await sessions.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null || session.OwnerActorId != authority.OwnerActorId ||
            session.ScopeEpoch != authority.ScopeEpoch || session.Status != "Active") return new("Denied");
        var turns = await outbox.ReadConversationAsync(sessionId, authority.OwnerActorId, ct).ConfigureAwait(false);
        var latest = turns.MaxBy(turn => turn.Sequence);
        if (latest is null || latest.Outcome != "Completed") return new("Stale");
        var proposal = EllaiConversationProjection.ToClient(latest).UiProposals
            .SingleOrDefault(item => item.ProposalId == proposalId)?.Proposal;
        if (proposal is null) return new("Stale");
        if (proposal.Kind != UiIntentKind.StartGuidance || proposal.FeatureId != "ellai.guidance" ||
            proposal.ResourceId is not null || proposal.ControlId is not null ||
            !proposal.Arguments.TryGetValue("feature", out var feature) ||
            !proposal.Arguments.TryGetValue("objective", out var objective) || string.IsNullOrWhiteSpace(objective) ||
            proposal.Arguments.Keys.Any(key => key is not ("feature" or "objective"))) return new("InvalidProposal");
        if (!authority.Features.Contains(feature)) return new("Denied");
        var catalogue = registry.GetSnapshot(new(feature, [], 20), authority.Features, authority.Capabilities);
        if (catalogue.Descriptors.Count == 0) return new("Unavailable");
        var observation = await observations.LatestAsync(sessionId, ct).ConfigureAwait(false);
        var current = await sessions.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (current is null || current.Status != "Active" || current.OwnerActorId != session.OwnerActorId ||
            current.ScopeEpoch != session.ScopeEpoch || current.InteractionEpoch != session.InteractionEpoch ||
            current.SessionVersion != session.SessionVersion) return new("Stale");
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        if (observation is null || observation.ReceivedAtUtc > now || now - observation.ReceivedAtUtc > TimeSpan.FromSeconds(45))
            return new("NeedsObservation", objective, catalogue, InteractionEpoch: session.InteractionEpoch);
        var hasTarget = catalogue.Descriptors.Any(descriptor => observation.MountedControlIds.Contains(descriptor.ControlId));
        var actionable = catalogue.Descriptors.Any(descriptor => observation.MountedControlIds.Contains(descriptor.ControlId) &&
            observation.ControlStates?.Count(state => state.ControlId == descriptor.ControlId && state.State == UiObservationState.Ready) == 1);
        return new(!hasTarget ? "NeedsNavigation" : actionable ? "Ready" : "WaitingForControl",
            objective, catalogue, observation, session.InteractionEpoch);
    }
}
