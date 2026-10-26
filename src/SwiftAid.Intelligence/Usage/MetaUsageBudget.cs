namespace SwiftAid.Intelligence.Usage;

public sealed class MetaUsageBudget(
    IUsageBudgetStore? store,
    MetaUsageBudgetPolicy policy,
    IUsageBudgetTransferAuthorizer? transferAuthorizer = null)
{
    public async ValueTask<BudgetDecision> TryReserveAsync(
        UsageReservationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!policy.IsVerifiedAndBounded)
        {
            return Denied(BudgetDenialReason.PolicyUnverified);
        }

        if (store is null || !store.SupportsAtomicReservations)
        {
            return Denied(BudgetDenialReason.AtomicStoreUnavailable);
        }

        if (request.ReservationId == Guid.Empty ||
            request.EstimatedCostUsd is not > 0 ||
            request.CostEvidence != UsageChargeEvidence.VerifiedEstimate ||
            request.InputTokens < 0 ||
            request.AudioSeconds < 0 ||
            !Enum.IsDefined(request.Purpose) ||
            request.Purpose == UsagePurpose.Reserve ||
            !policy.PurposeCeilingsUsd.ContainsKey(request.Purpose) ||
            request.InputTokens > policy.MaximumInputTokensPerCall ||
            request.AudioSeconds > policy.MaximumAudioSecondsPerCall)
        {
            return Denied(BudgetDenialReason.InvalidRequest);
        }

        var reserved = await store.TryReserveAsync(request, policy, cancellationToken).ConfigureAwait(false);
        return reserved
            ? new BudgetDecision(true, BudgetDenialReason.None, request.ReservationId)
            : Denied(BudgetDenialReason.LimitExceeded);
    }

    public ValueTask<bool> RecordActualAsync(
        Guid reservationId,
        decimal? actualChargeUsd,
        CancellationToken cancellationToken)
    {
        if (store is null || !store.SupportsAtomicReservations ||
            reservationId == Guid.Empty || actualChargeUsd is < 0)
        {
            return ValueTask.FromResult(false);
        }

        var evidence = actualChargeUsd is null
            ? UsageChargeEvidence.Unknown
            : UsageChargeEvidence.ProviderReported;

        return store.RecordActualAsync(reservationId, actualChargeUsd, evidence, cancellationToken);
    }

    public ValueTask<bool> TransferReserveAsync(
        ReserveTransferRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (store is null || !store.SupportsAtomicReservations || transferAuthorizer is null ||
            !policy.IsVerifiedAndBounded ||
            string.IsNullOrWhiteSpace(request.Reason) ||
            !Enum.IsDefined(request.TargetPurpose) ||
            request.TargetPurpose is UsagePurpose.Reserve ||
            request.AmountUsd <= 0 ||
            policy.ReserveCeilingUsd is null ||
            request.AmountUsd > policy.ReserveCeilingUsd)
        {
            return ValueTask.FromResult(false);
        }

        return TransferAuthorizedAsync(request, cancellationToken);
    }

    private async ValueTask<bool> TransferAuthorizedAsync(
        ReserveTransferRequest request,
        CancellationToken cancellationToken)
    {
        var authorization = await transferAuthorizer!.AuthorizeAsync(request, cancellationToken).ConfigureAwait(false);
        if (authorization is null ||
            string.IsNullOrWhiteSpace(authorization.ApproverId) ||
            !string.Equals(authorization.Reason, request.Reason, StringComparison.Ordinal) ||
            authorization.TargetPurpose != request.TargetPurpose ||
            authorization.AmountUsd != request.AmountUsd)
        {
            return false;
        }

        return await store!.TransferReserveAsync(authorization, policy, cancellationToken).ConfigureAwait(false);
    }

    private static BudgetDecision Denied(BudgetDenialReason reason) =>
        new(false, reason, null);
}
