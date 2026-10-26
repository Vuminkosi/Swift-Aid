using SwiftAid.Intelligence.Usage;

namespace SwiftAid.Application.Tests;

public sealed class MetaUsageBudgetTests
{
    [Fact]
    public async Task MissingPriceOrBalanceFailsClosed()
    {
        var budget = new MetaUsageBudget(new AtomicTestBudgetStore(), new MetaUsageBudgetPolicy());

        var decision = await budget.TryReserveAsync(ValidRequest(), CancellationToken.None);

        Assert.False(decision.Allowed);
        Assert.Equal(BudgetDenialReason.PolicyUnverified, decision.DenialReason);
    }

    [Fact]
    public async Task NonAtomicStoreCannotAuthorizeProviderCall()
    {
        var store = new AtomicTestBudgetStore(supportsAtomicReservations: false);
        var budget = new MetaUsageBudget(store, VerifiedPolicy(maximumUsd: 1m));

        var decision = await budget.TryReserveAsync(ValidRequest(), CancellationToken.None);

        Assert.False(decision.Allowed);
        Assert.Equal(BudgetDenialReason.AtomicStoreUnavailable, decision.DenialReason);
        Assert.Equal(0, store.ReservationAttempts);
    }

    [Fact]
    public async Task UnverifiedPriceEstimateCannotReserveBudget()
    {
        var store = new AtomicTestBudgetStore();
        var budget = new MetaUsageBudget(store, VerifiedPolicy(maximumUsd: 1m));
        var request = new UsageReservationRequest(
            Guid.NewGuid(),
            UsagePurpose.Reasoning,
            estimatedCostUsd: 0.10m,
            inputTokens: 100,
            audioSeconds: 0,
            costEvidence: UsageChargeEvidence.Unknown);

        var decision = await budget.TryReserveAsync(request, CancellationToken.None);

        Assert.False(decision.Allowed);
        Assert.Equal(BudgetDenialReason.InvalidRequest, decision.DenialReason);
        Assert.Equal(0, store.ReservationAttempts);
    }

    [Fact]
    public async Task PlanCreditEnvelopesCannotBeRaisedByConfiguration()
    {
        var budget = new MetaUsageBudget(
            new AtomicTestBudgetStore(),
            VerifiedPolicy(MetaUsageBudgetPolicy.SourceTotalCreditCeilingUsd + 1m));

        var decision = await budget.TryReserveAsync(ValidRequest(), CancellationToken.None);

        Assert.False(decision.Allowed);
        Assert.Equal(BudgetDenialReason.PolicyUnverified, decision.DenialReason);
    }

    [Fact]
    public async Task ConcurrentReservationsCannotExceedVerifiedCeiling()
    {
        var store = new AtomicTestBudgetStore();
        var budget = new MetaUsageBudget(store, VerifiedPolicy(maximumUsd: 1m));
        var requests = Enumerable.Range(0, 20)
            .Select(async _ => await budget.TryReserveAsync(
                ValidRequest(estimatedCostUsd: 0.10m),
                CancellationToken.None));

        var results = await Task.WhenAll(requests);

        Assert.Equal(10, results.Count(result => result.Allowed));
        Assert.Equal(1m, store.ReservedUsd);
    }

    [Fact]
    public async Task ExhaustedTotalAndPurposeCeilingsRejectFurtherReservations()
    {
        var store = new AtomicTestBudgetStore();
        var budget = new MetaUsageBudget(store, VerifiedPolicy(maximumUsd: 0.20m));

        var first = await budget.TryReserveAsync(ValidRequest(estimatedCostUsd: 0.10m), CancellationToken.None);
        var second = await budget.TryReserveAsync(ValidRequest(estimatedCostUsd: 0.10m), CancellationToken.None);
        var third = await budget.TryReserveAsync(ValidRequest(estimatedCostUsd: 0.01m), CancellationToken.None);

        Assert.True(first.Allowed);
        Assert.True(second.Allowed);
        Assert.False(third.Allowed);
        Assert.Equal(BudgetDenialReason.LimitExceeded, third.DenialReason);
        Assert.Equal(0.20m, store.ReservedUsd);
    }

    [Fact]
    public async Task ConcurrentReservationsRespectInFlightAndCallCeilings()
    {
        var concurrencyStore = new AtomicTestBudgetStore();
        var concurrencyBudget = new MetaUsageBudget(
            concurrencyStore,
            VerifiedPolicy(maximumUsd: 10m, maximumConcurrentCalls: 3));
        var concurrencyCalls = Enumerable.Range(0, 20)
            .Select(_ => concurrencyBudget.TryReserveAsync(
                ValidRequest(estimatedCostUsd: 0.10m),
                CancellationToken.None).AsTask());

        var concurrencyResults = await Task.WhenAll(concurrencyCalls);

        Assert.Equal(3, concurrencyResults.Count(result => result.Allowed));

        var callStore = new AtomicTestBudgetStore();
        var callBudget = new MetaUsageBudget(
            callStore,
            VerifiedPolicy(maximumUsd: 10m, maximumCalls: 2, maximumConcurrentCalls: 10));
        var boundedCalls = Enumerable.Range(0, 20)
            .Select(_ => callBudget.TryReserveAsync(
                ValidRequest(estimatedCostUsd: 0.10m),
                CancellationToken.None).AsTask());

        var callResults = await Task.WhenAll(boundedCalls);

        Assert.Equal(2, callResults.Count(result => result.Allowed));
    }

    [Fact]
    public async Task UnknownProviderChargeRemainsReservedUntilReconciled()
    {
        var store = new AtomicTestBudgetStore();
        var budget = new MetaUsageBudget(store, VerifiedPolicy(maximumUsd: 1m));
        var reservation = await budget.TryReserveAsync(ValidRequest(estimatedCostUsd: 0.25m), CancellationToken.None);
        Assert.True(reservation.Allowed);

        await budget.RecordActualAsync(reservation.ReservationId!.Value, actualChargeUsd: null, CancellationToken.None);

        Assert.True(store.IsUncertain(reservation.ReservationId.Value));
        Assert.Equal(0.25m, store.ReservedUsd);
    }

    [Fact]
    public async Task ReconciledProviderChargeRemainsCumulative()
    {
        var store = new AtomicTestBudgetStore();
        var budget = new MetaUsageBudget(store, VerifiedPolicy(maximumUsd: 1m));
        var reservation = await budget.TryReserveAsync(ValidRequest(estimatedCostUsd: 0.25m), CancellationToken.None);
        Assert.True(reservation.Allowed);

        Assert.True(await budget.RecordActualAsync(
            reservation.ReservationId!.Value,
            actualChargeUsd: 0.25m,
            CancellationToken.None));
        Assert.Equal(0.25m, store.ReservedUsd);

        var second = await budget.TryReserveAsync(ValidRequest(estimatedCostUsd: 0.80m), CancellationToken.None);

        Assert.False(second.Allowed);
        Assert.Equal(BudgetDenialReason.LimitExceeded, second.DenialReason);
    }

    [Fact]
    public async Task UnknownChargeCanBeReconciledToKnownChargeIdempotently()
    {
        var store = new AtomicTestBudgetStore();
        var budget = new MetaUsageBudget(store, VerifiedPolicy(maximumUsd: 1m));
        var reservation = await budget.TryReserveAsync(ValidRequest(estimatedCostUsd: 0.25m), CancellationToken.None);
        Assert.True(reservation.Allowed);

        Assert.True(await budget.RecordActualAsync(reservation.ReservationId!.Value, null, CancellationToken.None));
        Assert.True(store.IsUncertain(reservation.ReservationId.Value));
        Assert.True(await budget.RecordActualAsync(reservation.ReservationId.Value, 0.20m, CancellationToken.None));
        Assert.False(store.IsUncertain(reservation.ReservationId.Value));
        Assert.True(await budget.RecordActualAsync(reservation.ReservationId.Value, 0.20m, CancellationToken.None));

        Assert.Equal(0.20m, store.ReservedUsd);
    }

    [Fact]
    public async Task ReserveTransferRequiresApproverReasonAndAudit()
    {
        var store = new AtomicTestBudgetStore();
        var budget = new MetaUsageBudget(store, VerifiedPolicy(maximumUsd: 1m), new TestTransferAuthorizer());

        var denied = await budget.TransferReserveAsync(new ReserveTransferRequest(
            TargetPurpose: UsagePurpose.Reasoning,
            AmountUsd: 0.10m,
            Reason: " "), CancellationToken.None);
        var accepted = await budget.TransferReserveAsync(new ReserveTransferRequest(
            Reason: "approved test transfer",
            TargetPurpose: UsagePurpose.Reasoning,
            AmountUsd: 0.10m), CancellationToken.None);

        Assert.False(denied);
        Assert.True(accepted);
        Assert.Single(store.AuditedTransfers);
        Assert.Equal("trusted-synthetic-session", store.AuditedTransfers[0].ApproverId);
    }

    [Fact]
    public async Task ReserveTransferFailsWhenTrustedAuthorizerIsMissing()
    {
        var store = new AtomicTestBudgetStore();
        var budget = new MetaUsageBudget(store, VerifiedPolicy(maximumUsd: 1m));

        var accepted = await budget.TransferReserveAsync(new ReserveTransferRequest(
            Reason: "approved test transfer",
            TargetPurpose: UsagePurpose.Reasoning,
            AmountUsd: 0.10m), CancellationToken.None);

        Assert.False(accepted);
        Assert.Empty(store.AuditedTransfers);
    }

    [Fact]
    public async Task NullStoreFailsClosedForReservationsChargesAndTransfers()
    {
        var budget = new MetaUsageBudget(null, VerifiedPolicy(maximumUsd: 1m), new TestTransferAuthorizer());
        var reservation = await budget.TryReserveAsync(ValidRequest(), CancellationToken.None);

        Assert.False(reservation.Allowed);
        Assert.Equal(BudgetDenialReason.AtomicStoreUnavailable, reservation.DenialReason);
        Assert.False(await budget.RecordActualAsync(Guid.NewGuid(), 0.10m, CancellationToken.None));
        Assert.False(await budget.TransferReserveAsync(new ReserveTransferRequest(
            Reason: "authorized test",
            TargetPurpose: UsagePurpose.Reasoning,
            AmountUsd: 0.10m), CancellationToken.None));
    }

    [Fact]
    public async Task TransferRejectsAuthorizerReasonPurposeOrAmountMismatch()
    {
        foreach (var mismatch in Enum.GetValues<TransferApprovalMismatch>())
        {
            var store = new AtomicTestBudgetStore();
            var budget = new MetaUsageBudget(
                store,
                VerifiedPolicy(maximumUsd: 1m),
                new MismatchedTransferAuthorizer(mismatch));

            var accepted = await budget.TransferReserveAsync(new ReserveTransferRequest(
                Reason: "approved test transfer",
                TargetPurpose: UsagePurpose.Reasoning,
                AmountUsd: 0.10m), CancellationToken.None);

            Assert.False(accepted);
            Assert.Empty(store.AuditedTransfers);
        }
    }

    [Fact]
    public async Task ReserveTransferRaisesOnlyTargetPurposeCeiling()
    {
        var store = new AtomicTestBudgetStore();
        var policy = VerifiedPolicy(
            maximumUsd: 1m,
            reasoningCeilingUsd: 0.10m,
            transcriptionCeilingUsd: 0.10m,
            reserveCeilingUsd: 0.20m);
        var budget = new MetaUsageBudget(store, policy, new TestTransferAuthorizer());

        Assert.True(await budget.TransferReserveAsync(new ReserveTransferRequest(
            Reason: "approved reasoning reserve transfer",
            TargetPurpose: UsagePurpose.Reasoning,
            AmountUsd: 0.10m), CancellationToken.None));
        Assert.False(await budget.TransferReserveAsync(new ReserveTransferRequest(
            Reason: "transfer beyond remaining reserve",
            TargetPurpose: UsagePurpose.Transcription,
            AmountUsd: 0.11m), CancellationToken.None));
        Assert.Single(store.AuditedTransfers);

        var reasoning = await budget.TryReserveAsync(ValidRequest(estimatedCostUsd: 0.20m), CancellationToken.None);
        var transcription = await budget.TryReserveAsync(new UsageReservationRequest(
            Guid.NewGuid(), UsagePurpose.Transcription, 0.11m, 100, 0,
            UsageChargeEvidence.VerifiedEstimate), CancellationToken.None);

        Assert.True(reasoning.Allowed);
        Assert.False(transcription.Allowed);
        Assert.Equal(BudgetDenialReason.LimitExceeded, transcription.DenialReason);
    }

    private static UsageReservationRequest ValidRequest(decimal estimatedCostUsd = 0.10m) =>
        new(
            Guid.NewGuid(),
            UsagePurpose.Reasoning,
            estimatedCostUsd,
            inputTokens: 100,
            audioSeconds: 0,
            costEvidence: UsageChargeEvidence.VerifiedEstimate);

    private static MetaUsageBudgetPolicy VerifiedPolicy(
        decimal maximumUsd,
        int maximumCalls = 100,
        int maximumConcurrentCalls = 10,
        decimal? reasoningCeilingUsd = null,
        decimal? transcriptionCeilingUsd = null,
        decimal? evaluationCeilingUsd = null,
        decimal? reserveCeilingUsd = null) =>
        new(
            accountBalanceVerified: true,
            pricingVerified: true,
            totalCeilingUsd: maximumUsd,
            purposeCeilingsUsd: new Dictionary<UsagePurpose, decimal>
            {
                [UsagePurpose.Reasoning] = reasoningCeilingUsd ?? maximumUsd,
                [UsagePurpose.Transcription] = transcriptionCeilingUsd ?? maximumUsd,
                [UsagePurpose.Evaluation] = evaluationCeilingUsd ?? maximumUsd,
                [UsagePurpose.Reserve] = reserveCeilingUsd ?? maximumUsd
            },
            maximumCalls,
            maximumConcurrentCalls,
            maximumInputTokensPerCall: 1_000,
            maximumAudioSecondsPerCall: 300,
            reserveCeilingUsd: reserveCeilingUsd ?? maximumUsd);

    private sealed class AtomicTestBudgetStore : IUsageBudgetStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<Guid, UsageReservationRequest> _reservations = [];
        private readonly Dictionary<Guid, (UsagePurpose Purpose, decimal Charge)> _charges = [];
        private readonly HashSet<Guid> _uncertain = [];
        private readonly List<AuthorizedReserveTransfer> _transfers = [];
        private readonly Dictionary<UsagePurpose, decimal> _transferredByPurpose = [];

        public AtomicTestBudgetStore(bool supportsAtomicReservations = true)
        {
            SupportsAtomicReservations = supportsAtomicReservations;
        }

        public bool SupportsAtomicReservations { get; }

        public int ReservationAttempts { get; private set; }

        public decimal ReservedUsd
        {
            get
            {
                lock (_gate)
                {
                    return _reservations.Values.Sum(reservation => reservation.EstimatedCostUsd.GetValueOrDefault()) +
                        _charges.Values.Sum(charge => charge.Charge);
                }
            }
        }

        public IReadOnlyList<AuthorizedReserveTransfer> AuditedTransfers
        {
            get
            {
                lock (_gate)
                {
                    return _transfers.ToArray();
                }
            }
        }

        public bool IsUncertain(Guid reservationId)
        {
            lock (_gate)
            {
                return _uncertain.Contains(reservationId);
            }
        }

        public async ValueTask<bool> TryReserveAsync(
            UsageReservationRequest request,
            MetaUsageBudgetPolicy policy,
            CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                ReservationAttempts++;
                var totalUsed = _reservations.Values.Sum(reservation => reservation.EstimatedCostUsd.GetValueOrDefault()) +
                    _charges.Values.Sum(charge => charge.Charge);
                var purposeUsed = _reservations.Values
                    .Where(reservation => reservation.Purpose == request.Purpose)
                    .Sum(reservation => reservation.EstimatedCostUsd.GetValueOrDefault()) +
                    _charges.Values
                    .Where(charge => charge.Purpose == request.Purpose)
                    .Sum(charge => charge.Charge);
                var purposeLimit = policy.PurposeCeilingsUsd.GetValueOrDefault(request.Purpose) +
                    _transferredByPurpose.GetValueOrDefault(request.Purpose);

                if (_reservations.ContainsKey(request.ReservationId) ||
                    _reservations.Count >= policy.MaximumConcurrentCalls.GetValueOrDefault() ||
                    ReservationAttempts > policy.MaximumCalls.GetValueOrDefault() ||
                    totalUsed + request.EstimatedCostUsd.GetValueOrDefault() > policy.TotalCeilingUsd.GetValueOrDefault() ||
                    purposeUsed + request.EstimatedCostUsd.GetValueOrDefault() > purposeLimit)
                {
                    ReservationAttempts--;
                    return false;
                }

                _reservations.Add(request.ReservationId, request);
                return true;
            }
        }

        public ValueTask<bool> RecordActualAsync(
            Guid reservationId,
            decimal? actualChargeUsd,
            UsageChargeEvidence evidence,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (actualChargeUsd is not null && _charges.TryGetValue(reservationId, out var existingCharge))
                {
                    return ValueTask.FromResult(existingCharge.Charge == actualChargeUsd.Value);
                }

                if (!_reservations.TryGetValue(reservationId, out var reservation))
                {
                    return ValueTask.FromResult(false);
                }

                if (actualChargeUsd is null)
                {
                    _uncertain.Add(reservationId);
                }
                else
                {
                    _reservations.Remove(reservationId);
                    _uncertain.Remove(reservationId);
                    _charges.Add(reservationId, (reservation.Purpose, actualChargeUsd.Value));
                }

                return ValueTask.FromResult(true);
            }
        }

        public ValueTask<bool> TransferReserveAsync(
            AuthorizedReserveTransfer approval,
            MetaUsageBudgetPolicy policy,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var transferred = _transferredByPurpose.Values.Sum();
                if (string.IsNullOrWhiteSpace(approval.ApproverId) ||
                    approval.AmountUsd <= 0 ||
                    transferred + approval.AmountUsd > policy.ReserveCeilingUsd)
                {
                    return ValueTask.FromResult(false);
                }

                _transfers.Add(approval);
                _transferredByPurpose[approval.TargetPurpose] =
                    _transferredByPurpose.GetValueOrDefault(approval.TargetPurpose) + approval.AmountUsd;
                return ValueTask.FromResult(true);
            }
        }
    }

    private sealed class TestTransferAuthorizer : IUsageBudgetTransferAuthorizer
    {
        public ValueTask<AuthorizedReserveTransfer?> AuthorizeAsync(
            ReserveTransferRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(request.Reason))
            {
                return ValueTask.FromResult<AuthorizedReserveTransfer?>(null);
            }

            return ValueTask.FromResult<AuthorizedReserveTransfer?>(new AuthorizedReserveTransfer(
                ApproverId: "trusted-synthetic-session",
                TargetPurpose: request.TargetPurpose,
                AmountUsd: request.AmountUsd,
                Reason: request.Reason));
        }
    }

    private enum TransferApprovalMismatch
    {
        Reason,
        Purpose,
        Amount
    }

    private sealed class MismatchedTransferAuthorizer(TransferApprovalMismatch mismatch) : IUsageBudgetTransferAuthorizer
    {
        public ValueTask<AuthorizedReserveTransfer?> AuthorizeAsync(
            ReserveTransferRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<AuthorizedReserveTransfer?>(new AuthorizedReserveTransfer(
                ApproverId: "trusted-synthetic-session",
                TargetPurpose: mismatch == TransferApprovalMismatch.Purpose
                    ? UsagePurpose.Evaluation
                    : request.TargetPurpose,
                AmountUsd: request.AmountUsd + (mismatch == TransferApprovalMismatch.Amount ? 0.01m : 0m),
                Reason: mismatch == TransferApprovalMismatch.Reason
                    ? $"{request.Reason} altered"
                    : request.Reason));
        }
    }
}
