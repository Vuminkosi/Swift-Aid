using SwiftAid.Application.Authorization;
using SwiftAid.Application.Intake;

namespace SwiftAid.Infrastructure.Persistence.Repositories.Intake;

/// <summary>
/// Fail-closed placeholder until the Postgres routing-decision store lands.
/// Denies every routing decision without state changes. Replace with an
/// <c>AddScoped</c> Postgres implementation; this <c>TryAdd</c> yields to it.
/// </summary>
public sealed class NotConfiguredIntakeDecisionStore : IIntakeDecisionStore
{
    public Task<IntakeDecisionResult> DecideAsync(
        IntakeDecision decision,
        ActorContext actor,
        DateTimeOffset nowUtc,
        CancellationToken ct) =>
        Task.FromResult(new IntakeDecisionResult(IntakeDecisionStatus.Invalid, null));
}
