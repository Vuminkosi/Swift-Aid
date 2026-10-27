using SwiftAid.Intelligence.Meta;
using SwiftAid.Intelligence.Usage;

namespace SwiftAid.Application.Tests;

public sealed class MetaConfigurationTests
{
    [Fact]
    public async Task InFlightReservationsPreventConcurrentBudgetOvershoot()
    {
        const int concurrentRequests = 10;
        var store = new CoordinatedBudgetStore(concurrentRequests);
        var budget = new MetaUsageBudget(store, new MetaUsageBudgetPolicy(
            accountBalanceVerified: true,
            pricingVerified: true,
            totalCeilingUsd: 0.10m,
            purposeCeilingsUsd: new Dictionary<UsagePurpose, decimal>
            {
                [UsagePurpose.Reasoning] = 0.10m,
                [UsagePurpose.Transcription] = 0.10m,
                [UsagePurpose.Evaluation] = 0.10m
            },
            maximumCalls: concurrentRequests,
            maximumConcurrentCalls: concurrentRequests,
            maximumInputTokensPerCall: 1_000,
            maximumAudioSecondsPerCall: 300,
            reserveCeilingUsd: 0m));

        var decisions = await Task.WhenAll(Enumerable.Range(0, concurrentRequests).Select(_ =>
            budget.TryReserveAsync(new UsageReservationRequest(
                Guid.NewGuid(), UsagePurpose.Reasoning, 0.02m, 10, 0,
                UsageChargeEvidence.VerifiedEstimate), CancellationToken.None).AsTask()));

        Assert.Equal(5, decisions.Count(decision => decision.Allowed));
        Assert.Equal(0.10m, store.ReservedUsd);
    }

    [Fact]
    public async Task MissingCredentialDisablesProviderCapabilities()
    {
        var probe = new MetaCapabilityProbe(new MetaOptions
        {
            Provider = "meta",
            Route = InferenceRouteKind.MetaHostedApi,
            RouteAlias = "hosted-default",
            ModelId = "source-listed-model"
        }, SafeRegistry());

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(CapabilityState.NotProvisioned, result.Text.State);
        Assert.False(result.Text.IsUsable);
        Assert.Equal(CapabilityState.NotProvisioned, result.Asr.State);
    }

    [Fact]
    public async Task ConfiguredCredentialDoesNotClaimProviderVerification()
    {
        const string credential = "test-secret-must-not-be-in-result";
        var probe = new MetaCapabilityProbe(new MetaOptions
        {
            Provider = "meta",
            Route = InferenceRouteKind.MetaHostedApi,
            RouteAlias = "hosted-default",
            CredentialConfigured = true,
            ModelId = "source-listed-model"
        }, SafeRegistry());

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(CapabilityState.ConfiguredUnverified, result.Text.State);
        Assert.False(result.Text.IsUsable);
        Assert.DoesNotContain(credential, result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void NonMetaProviderIsRejected()
    {
        var options = new MetaOptions
        {
            Provider = "unapproved-provider",
            Route = InferenceRouteKind.MetaHostedApi,
        };

        Assert.Throws<MetaConfigurationException>(() => MetaOptionsValidator.Validate(options));
    }

    [Fact]
    public async Task MissingAsrDoesNotChangeTextCapability()
    {
        var probe = new MetaCapabilityProbe(new MetaOptions
        {
            Provider = "meta",
            Route = InferenceRouteKind.MetaHostedApi,
            RouteAlias = "hosted-default",
            CredentialConfigured = true,
            ModelId = "source-listed-model"
        }, SafeRegistry());

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(CapabilityState.ConfiguredUnverified, result.Text.State);
        Assert.Equal(CapabilityState.NotProvisioned, result.Asr.State);
        Assert.Equal(CapabilityState.NotProvisioned, result.Pricing.State);
    }

    [Fact]
    public async Task EndpointFromUnverifiedAuthorityIsRejectedWithoutRequestingIt()
    {
        var probe = new MetaCapabilityProbe(new MetaOptions
        {
            Provider = "meta",
            Route = InferenceRouteKind.MetaHostedApi,
            RouteAlias = "hosted-default",
            ModelId = "source-listed-model",
            CredentialConfigured = true
        }, new TestEndpointRegistry(new MetaEndpointRegistration(
            Route: InferenceRouteKind.MetaHostedApi,
            RouteAlias: "hosted-default",
            ModelId: "source-listed-model",
            Endpoint: new Uri("https://192.0.2.10"),
            ExpectedAuthority: "192.0.2.10",
            EvidenceReference: "synthetic-test-source",
            PublicAuthorityVerified: true,
            AsrModelId: null,
            ToolsListed: false,
            AudioFormatsListed: [],
            LanguagesListed: [])));

        await Assert.ThrowsAsync<MetaConfigurationException>(async () =>
            await probe.ProbeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RegistrationForDifferentRouteKindIsRejected()
    {
        var probe = new MetaCapabilityProbe(new MetaOptions
        {
            Provider = "meta",
            Route = InferenceRouteKind.MetaHostedApi,
            RouteAlias = "hosted-default",
            ModelId = "source-listed-model",
            CredentialConfigured = true
        }, new TestEndpointRegistry(new MetaEndpointRegistration(
            Route: InferenceRouteKind.MetaSelfHostedOpenWeight,
            RouteAlias: "hosted-default",
            ModelId: "source-listed-model",
            Endpoint: new Uri("https://api.example.com/v1"),
            ExpectedAuthority: "api.example.com",
            EvidenceReference: "synthetic-test-source",
            PublicAuthorityVerified: true,
            AsrModelId: null,
            ToolsListed: false,
            AudioFormatsListed: [],
            LanguagesListed: [])));

        await Assert.ThrowsAsync<MetaConfigurationException>(async () =>
            await probe.ProbeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DefaultEmptyRegistryLeavesRouteNotProvisioned()
    {
        var probe = new MetaCapabilityProbe(new MetaOptions
        {
            Provider = "meta",
            Route = InferenceRouteKind.MetaHostedApi,
            RouteAlias = "unlisted-alias",
            ModelId = "unlisted-model",
            CredentialConfigured = true
        });

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(CapabilityState.NotProvisioned, result.Text.State);
        Assert.False(result.Text.IsUsable);
    }

    [Fact]
    public async Task EndpointAuthorityMustMatchItsRegistryRecord()
    {
        var registration = new MetaEndpointRegistration(
            Route: InferenceRouteKind.MetaHostedApi,
            RouteAlias: "hosted-default",
            ModelId: "source-listed-model",
            Endpoint: new Uri("https://api.example.com/v1"),
            ExpectedAuthority: "different.example.com",
            EvidenceReference: "synthetic-test-source",
            PublicAuthorityVerified: true,
            AsrModelId: null,
            ToolsListed: false,
            AudioFormatsListed: [],
            LanguagesListed: []);
        var probe = new MetaCapabilityProbe(new MetaOptions
        {
            Provider = "meta",
            Route = InferenceRouteKind.MetaHostedApi,
            RouteAlias = "hosted-default",
            ModelId = "source-listed-model",
            CredentialConfigured = true
        }, new TestEndpointRegistry(registration));

        await Assert.ThrowsAsync<MetaConfigurationException>(async () =>
            await probe.ProbeAsync(CancellationToken.None));
    }

    private static IMetaEndpointRegistry SafeRegistry() => new TestEndpointRegistry(new MetaEndpointRegistration(
        Route: InferenceRouteKind.MetaHostedApi,
        RouteAlias: "hosted-default",
        ModelId: "source-listed-model",
        Endpoint: new Uri("https://api.example.com/v1"),
        ExpectedAuthority: "api.example.com",
        EvidenceReference: "synthetic-test-source",
        PublicAuthorityVerified: true,
        AsrModelId: null,
        ToolsListed: false,
        AudioFormatsListed: [],
        LanguagesListed: []));

    private sealed class TestEndpointRegistry(MetaEndpointRegistration registration) : IMetaEndpointRegistry
    {
        public ValueTask<MetaEndpointRegistration?> FindAsync(
            string routeAlias,
            string modelId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<MetaEndpointRegistration?>(
                registration.RouteAlias == routeAlias && registration.ModelId == modelId
                    ? registration
                    : null);
        }
    }

    private sealed class CoordinatedBudgetStore(int participants) : IUsageBudgetStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<Guid, UsageReservationRequest> _reservations = [];
        private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        public bool SupportsAtomicReservations => true;
        public decimal ReservedUsd { get; private set; }

        public async ValueTask<bool> TryReserveAsync(
            UsageReservationRequest request,
            MetaUsageBudgetPolicy policy,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _arrivals) == participants)
                _allArrived.TrySetResult();
            await _allArrived.Task.WaitAsync(cancellationToken);

            lock (_gate)
            {
                var amount = request.EstimatedCostUsd.GetValueOrDefault();
                if (_reservations.ContainsKey(request.ReservationId) ||
                    ReservedUsd + amount > policy.TotalCeilingUsd)
                    return false;

                _reservations.Add(request.ReservationId, request);
                ReservedUsd += amount;
                return true;
            }
        }

        public ValueTask<bool> RecordActualAsync(
            Guid reservationId,
            decimal? actualChargeUsd,
            UsageChargeEvidence evidence,
            CancellationToken cancellationToken) => ValueTask.FromResult(false);

        public ValueTask<bool> TransferReserveAsync(
            AuthorizedReserveTransfer approval,
            MetaUsageBudgetPolicy policy,
            CancellationToken cancellationToken) => ValueTask.FromResult(false);
    }
}
