using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>Finite preauthorized demo manifest: purpose, scenario, read
/// capabilities, resource bounds, cycle ordinals, limits, expiry, personas.
/// Each cycle redeems one grant; never approves an administrative plan.</summary>
public sealed record DemoManifest(
    string ManifestId,
    string Purpose,
    string ScenarioId,
    IReadOnlyList<string> AllowedCapabilities,
    IReadOnlyList<string> ResourceBounds,
    int TotalCycles,
    int CyclesRedeemed,
    DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<string> PermittedPersonas,
    string ManifestHash);

/// <summary>Scenario director: finite manifest-bound read/presentation cycles
/// (ELL-T16 groundwork). Scope/purpose changes require a fresh grant.</summary>
public sealed class EllaiScenarioDirector
{
    public DemoManifest? RedeemCycle(DemoManifest manifest, string purpose, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!string.Equals(manifest.Purpose, purpose, StringComparison.Ordinal))
        {
            return null;
        }

        if (manifest.CyclesRedeemed >= manifest.TotalCycles || now >= manifest.ExpiresAtUtc)
        {
            return null;
        }

        return manifest with { CyclesRedeemed = manifest.CyclesRedeemed + 1 };
    }
}

/// <summary>Approved administrative action with stable idempotency key.</summary>
public sealed record ApprovedEllaiAction(
    string ActionId,
    string PlanHash,
    int PlanVersion,
    AdminActionKind Kind,
    string IdempotencyKey,
    string Status);

/// <summary>Exact-approval gate + deterministic effect submission
/// (ELL-T08 groundwork). Any mutation of recipient, due time, entity version,
/// action set, evidence, scope epoch or expiry invalidates the approval.
/// Commit-then-timeout reuses the stable idempotency key — never a new
/// command identity.</summary>
public sealed class EllaiApprovedActionService
{
    private readonly Dictionary<string, ApprovedEllaiAction> _actions = new(StringComparer.Ordinal);

    public static string ComputeIdempotencyKey(string planHash, int planVersion, string actionLabel) =>
        $"ellai-{planHash}-{planVersion}-{actionLabel}";

    public ApprovedEllaiAction? Submit(
        string planHash, int planVersion, AdminActionProposal proposal,
        string approvedPlanHash, int approvedPlanVersion,
        string? approvedRecipient, DateTimeOffset? approvedDueAtUtc)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (!string.Equals(planHash, approvedPlanHash, StringComparison.Ordinal)
            || planVersion != approvedPlanVersion
            || !string.Equals(proposal.RecipientCandidateId, approvedRecipient, StringComparison.Ordinal)
            || proposal.HumanSuppliedDueAtUtc != approvedDueAtUtc)
        {
            return null;
        }

        var key = ComputeIdempotencyKey(planHash, planVersion, proposal.ProposalLabel);
        lock (_actions)
        {
            if (_actions.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var action = new ApprovedEllaiAction(
                $"act-{Guid.NewGuid():N}", planHash, planVersion,
                proposal.Kind, key, "Submitted");
            _actions[key] = action;
            return action;
        }
    }
}
