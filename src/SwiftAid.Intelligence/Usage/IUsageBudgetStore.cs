using System.Collections.ObjectModel;

namespace SwiftAid.Intelligence.Usage;

public enum UsagePurpose
{
    Reasoning,
    Transcription,
    Evaluation,
    Reserve
}

public enum BudgetDenialReason
{
    None,
    PolicyUnverified,
    AtomicStoreUnavailable,
    InvalidRequest,
    LimitExceeded
}

public enum UsageChargeEvidence
{
    Unknown,
    ProviderReported,
    VerifiedEstimate
}

public sealed record UsageReservationRequest
{
    public UsageReservationRequest(
        Guid reservationId,
        UsagePurpose purpose,
        decimal? estimatedCostUsd,
        int inputTokens,
        int audioSeconds,
        UsageChargeEvidence costEvidence)
    {
        ReservationId = reservationId;
        Purpose = purpose;
        EstimatedCostUsd = estimatedCostUsd;
        InputTokens = inputTokens;
        AudioSeconds = audioSeconds;
        CostEvidence = costEvidence;
    }

    public Guid ReservationId { get; }

    public UsagePurpose Purpose { get; }

    public decimal? EstimatedCostUsd { get; }

    public int InputTokens { get; }

    public int AudioSeconds { get; }

    public UsageChargeEvidence CostEvidence { get; }
}

public sealed record BudgetDecision(bool Allowed, BudgetDenialReason DenialReason, Guid? ReservationId);

public sealed class MetaUsageBudgetPolicy
{
    public const decimal SourceTotalCreditCeilingUsd = 150m;

    public const decimal SourceReasoningEnvelopeUsd = 85m;

    public const decimal SourceTranscriptionEnvelopeUsd = 25m;

    public const decimal SourceEvaluationEnvelopeUsd = 20m;

    public const decimal SourceReserveEnvelopeUsd = 20m;

    public MetaUsageBudgetPolicy(
        bool accountBalanceVerified = false,
        bool pricingVerified = false,
        decimal? totalCeilingUsd = null,
        IReadOnlyDictionary<UsagePurpose, decimal>? purposeCeilingsUsd = null,
        int? maximumCalls = null,
        int? maximumConcurrentCalls = null,
        int? maximumInputTokensPerCall = null,
        int? maximumAudioSecondsPerCall = null,
        decimal? reserveCeilingUsd = null)
    {
        AccountBalanceVerified = accountBalanceVerified;
        PricingVerified = pricingVerified;
        TotalCeilingUsd = totalCeilingUsd;
        PurposeCeilingsUsd = new ReadOnlyDictionary<UsagePurpose, decimal>(
            new Dictionary<UsagePurpose, decimal>(purposeCeilingsUsd ?? new Dictionary<UsagePurpose, decimal>()));
        MaximumCalls = maximumCalls;
        MaximumConcurrentCalls = maximumConcurrentCalls;
        MaximumInputTokensPerCall = maximumInputTokensPerCall;
        MaximumAudioSecondsPerCall = maximumAudioSecondsPerCall;
        ReserveCeilingUsd = reserveCeilingUsd;
    }

    public bool AccountBalanceVerified { get; }

    public bool PricingVerified { get; }

    public decimal? TotalCeilingUsd { get; }

    public IReadOnlyDictionary<UsagePurpose, decimal> PurposeCeilingsUsd { get; }

    public int? MaximumCalls { get; }

    public int? MaximumConcurrentCalls { get; }

    public int? MaximumInputTokensPerCall { get; }

    public int? MaximumAudioSecondsPerCall { get; }

    public decimal? ReserveCeilingUsd { get; }

    public bool IsVerifiedAndBounded
    {
        get
        {
            if (!AccountBalanceVerified || !PricingVerified ||
                TotalCeilingUsd is not > 0 or > SourceTotalCreditCeilingUsd ||
                ReserveCeilingUsd is not >= 0 or > SourceReserveEnvelopeUsd ||
                MaximumCalls is not > 0 ||
                MaximumConcurrentCalls is not > 0 ||
                MaximumInputTokensPerCall is not > 0 ||
                MaximumAudioSecondsPerCall is not > 0)
            {
                return false;
            }

            foreach (var (purpose, ceiling) in PurposeCeilingsUsd)
            {
                if (!Enum.IsDefined(purpose) || ceiling <= 0 || ceiling > SourceCeilingFor(purpose))
                {
                    return false;
                }
            }

            return PurposeCeilingsUsd.ContainsKey(UsagePurpose.Reasoning) &&
                PurposeCeilingsUsd.ContainsKey(UsagePurpose.Transcription) &&
                PurposeCeilingsUsd.ContainsKey(UsagePurpose.Evaluation);
        }
    }

    private static decimal SourceCeilingFor(UsagePurpose purpose) => purpose switch
    {
        UsagePurpose.Reasoning => SourceReasoningEnvelopeUsd,
        UsagePurpose.Transcription => SourceTranscriptionEnvelopeUsd,
        UsagePurpose.Evaluation => SourceEvaluationEnvelopeUsd,
        UsagePurpose.Reserve => SourceReserveEnvelopeUsd,
        _ => 0m
    };
}

/// <summary>
/// Atomic implementations must persist reservations, limits, uncertain charges, reconciled provider charges,
/// reserve availability and transfer audit records in a shared durable transaction before a provider call is allowed.
/// </summary>
public interface IUsageBudgetStore
{
    bool SupportsAtomicReservations { get; }

    ValueTask<bool> TryReserveAsync(
        UsageReservationRequest request,
        MetaUsageBudgetPolicy policy,
        CancellationToken cancellationToken);

    /// <summary>
    /// A null charge marks the reservation uncertain and retains the reserved amount. A known charge replaces
    /// the held reservation with cumulative reported usage; it does not free the amount for reuse. Repeating the
    /// same known charge for an already reconciled reservation is idempotent; a conflicting value is rejected.
    /// </summary>
    ValueTask<bool> RecordActualAsync(
        Guid reservationId,
        decimal? actualChargeUsd,
        UsageChargeEvidence evidence,
        CancellationToken cancellationToken);

    /// <summary>Transfers an already authorized request and stores the trusted approver identity as audit data.</summary>
    ValueTask<bool> TransferReserveAsync(
        AuthorizedReserveTransfer approval,
        MetaUsageBudgetPolicy policy,
        CancellationToken cancellationToken);
}
