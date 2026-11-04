using SwiftAid.Application.Intake;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Contracts.Geography;
using SwiftAid.Contracts.Intake;
using SwiftAid.Gateway.Intake;
using SwiftAid.Gateway.Presentation;
using SwiftAid.Client.Presentation;

namespace SwiftAid.Integration.Tests;

public sealed class WebGuestIntakeServiceTests
{
    [Fact]
    public async Task ConflictingSubmissionIsNotReportedAsTransientFailure()
    {
        var intake = new FakeIntakeService { Submit = (_, _) => throw new IntakeSubmissionException(IntakeErrorCode.SubmissionConflict) };
        var client = new WebPublicIntakeSubmissionClient(new WebGuestIntakeService(new FakeSessionEndpointService(), intake));

        var result = await client.SubmitAsync(GuestSubmissionSamples.Human());

        Assert.Equal(UiCapabilityResultCode.Conflict, result.Code);
        Assert.Null(result.Value);
        Assert.Equal(1, intake.SubmitCalls);
    }
    private sealed class ScopedLifetime : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task ProductionScopeFactorySeparatesAndDisposesEachOperation()
    {
        var lifetimes = new List<ScopedLifetime>();
        var sessions = new List<FakeSessionEndpointService>();
        var services = new ServiceCollection();
        services.AddScoped<ScopedLifetime>(_ =>
        {
            var value = new ScopedLifetime(); lifetimes.Add(value); return value;
        });
        services.AddScoped<IIntakeSessionEndpointService>(provider =>
        {
            _ = provider.GetRequiredService<ScopedLifetime>();
            var value = new FakeSessionEndpointService(); sessions.Add(value); return value;
        });
        services.AddScoped<IIntakeService>(_ => new FakeIntakeService());
        await using var provider = services.BuildServiceProvider();
        var service = new WebGuestIntakeService(provider.GetRequiredService<IServiceScopeFactory>());
        await service.SubmitAsync(GuestSubmissionSamples.Human());
        await service.SubmitAsync(GuestSubmissionSamples.Human());
        Assert.Equal(2, sessions.Count);
        Assert.Equal(1, sessions[0].CreateCalls);
        Assert.Equal(0, sessions[1].CreateCalls);
        Assert.Equal(sessions[0].LastProof, sessions[1].LastProof);
        Assert.Equal(2, lifetimes.Count);
        Assert.All(lifetimes, lifetime => Assert.True(lifetime.Disposed));
    }

    [Fact]
    public async Task OverlappingOperationCanCancelWithoutSharingActiveDependencies()
    {
        var release = new TaskCompletionSource<IntakeReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        var intake = new FakeIntakeService { Pending = release.Task };
        var service = new WebGuestIntakeService(new FakeSessionEndpointService(), intake);
        var first = service.SubmitAsync(GuestSubmissionSamples.Human());
        using var cancellation = new CancellationTokenSource();
        var second = service.SubmitAsync(GuestSubmissionSamples.Human(), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Equal(1, intake.SubmitCalls);
        release.SetResult(new IntakeReceipt(Guid.NewGuid(), Guid.NewGuid(), IntakeStatus.ReceivedUnverified,
            IntakeRoutingStatus.NeedsRouting, DateTimeOffset.UtcNow));
        await first;
    }

    [Fact]
    public async Task Submit_FirstTime_CreatesSessionResolvesAndSubmits()
    {
        var sessions = new FakeSessionEndpointService();
        var intake = new FakeIntakeService();
        var service = new WebGuestIntakeService(sessions, intake);
        var submission = GuestSubmissionSamples.Human();

        var receipt = await service.SubmitAsync(submission);

        Assert.Equal(1, sessions.CreateCalls);
        Assert.Equal(1, sessions.ResolveCalls);
        Assert.Equal(1, intake.SubmitCalls);
        Assert.Equal(submission.ClientSubmissionId, receipt.ClientSubmissionId);
        Assert.Same(intake.LastCapability, sessions.ResolvedCapability);
    }

    [Fact]
    public async Task Submit_SecondTime_ReusesCapabilityWithoutNewSession()
    {
        var sessions = new FakeSessionEndpointService();
        var intake = new FakeIntakeService();
        var service = new WebGuestIntakeService(sessions, intake);
        await service.SubmitAsync(GuestSubmissionSamples.Human());

        await service.SubmitAsync(GuestSubmissionSamples.Human());

        Assert.Equal(1, sessions.CreateCalls);
        Assert.Equal(2, sessions.ResolveCalls);
        Assert.Equal(2, intake.SubmitCalls);
    }

    [Fact]
    public async Task Submit_WhenCapabilityExpires_RenewsSessionOnceAndRetries()
    {
        var sessions = new FakeSessionEndpointService();
        var intake = new FakeIntakeService();
        var service = new WebGuestIntakeService(sessions, intake);
        await service.SubmitAsync(GuestSubmissionSamples.Human());
        var submission = GuestSubmissionSamples.Human();
        sessions.FailNextResolveThenReturn(FakeSessionEndpointService.DefaultCapability());

        var receipt = await service.SubmitAsync(submission);

        Assert.Equal(2, sessions.CreateCalls);
        Assert.Equal(submission.ClientSubmissionId, receipt.ClientSubmissionId);
        Assert.Equal(2, intake.SubmitCalls);
    }

    [Fact]
    public async Task Submit_WhenRenewalFails_ThrowsCapabilityInvalid()
    {
        var sessions = new FakeSessionEndpointService { Resolve = () => null };
        var intake = new FakeIntakeService();
        var service = new WebGuestIntakeService(sessions, intake);

        var exception = await Assert.ThrowsAsync<IntakeSubmissionException>(
            () => service.SubmitAsync(GuestSubmissionSamples.Human()));

        Assert.Equal(IntakeErrorCode.CapabilityInvalid, exception.Code);
        Assert.Equal(0, intake.SubmitCalls);
    }

    [Fact]
    public async Task Submit_WhenReportInvalid_PropagatesWithoutRenewal()
    {
        var sessions = new FakeSessionEndpointService();
        var intake = new FakeIntakeService
        {
            Submit = (_, _) => throw new IntakeSubmissionException(IntakeErrorCode.InvalidReport)
        };
        var service = new WebGuestIntakeService(sessions, intake);

        var exception = await Assert.ThrowsAsync<IntakeSubmissionException>(
            () => service.SubmitAsync(GuestSubmissionSamples.Human()));

        Assert.Equal(IntakeErrorCode.InvalidReport, exception.Code);
        Assert.Equal(1, sessions.CreateCalls);
    }

    [Fact]
    public async Task Submit_WhenSessionCreationFails_ThrowsCapabilityInvalid()
    {
        var sessions = new FakeSessionEndpointService { Create = () => null };
        var intake = new FakeIntakeService();
        var service = new WebGuestIntakeService(sessions, intake);

        var exception = await Assert.ThrowsAsync<IntakeSubmissionException>(
            () => service.SubmitAsync(GuestSubmissionSamples.Human()));

        Assert.Equal(IntakeErrorCode.CapabilityInvalid, exception.Code);
        Assert.Equal(0, intake.SubmitCalls);
    }

    [Fact]
    public async Task GetReceipt_WithoutSession_ReturnsNullWithoutCallingService()
    {
        var sessions = new FakeSessionEndpointService();
        var service = new WebGuestIntakeService(sessions, new FakeIntakeService());

        var receipt = await service.GetReceiptAsync(Guid.NewGuid());

        Assert.Null(receipt);
        Assert.Equal(0, sessions.ReceiptCalls);
    }

    [Fact]
    public async Task GetReceipt_WithSession_DelegatesWithHeldProof()
    {
        var sessions = new FakeSessionEndpointService();
        var service = new WebGuestIntakeService(sessions, new FakeIntakeService());
        await service.SubmitAsync(GuestSubmissionSamples.Human());
        var receiptId = Guid.NewGuid();
        sessions.Receipt = new IntakeReceipt(
            receiptId, Guid.NewGuid(), IntakeStatus.ReceivedUnverified,
            IntakeRoutingStatus.NeedsRouting, DateTimeOffset.UtcNow);

        var receipt = await service.GetReceiptAsync(receiptId);

        Assert.Equal(receiptId, receipt?.ReceiptId);
        Assert.Equal(1, sessions.ReceiptCalls);
        Assert.Equal(32, sessions.LastProof.Length);
    }

    [Fact]
    public async Task Submit_NullSubmission_ThrowsArgumentNull()
    {
        var service = new WebGuestIntakeService(
            new FakeSessionEndpointService(), new FakeIntakeService());

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => service.SubmitAsync(null!, CancellationToken.None));
    }

    private static class GuestSubmissionSamples
    {
        public static IntakeSubmission Human() => new(
            1,
            Guid.NewGuid(),
            IntakeSubjectScope.Human,
            "Saw a sick goat near the river.",
            ["Coughing"],
            [],
            new ReportedTime(DateTimeOffset.UtcNow, null, null, null, "+02:00", null, ReportedTimePrecision.Approximate),
            new ReportedTime(null, null, null, null, null, null, ReportedTimePrecision.Unknown),
            new LocationContext(
                "ZA",
                new OptionalPlace("Western Cape", null, PresenceState.Known),
                new OptionalPlace("Cape Winelands", null, PresenceState.Known),
                new OptionalPlace("Stellenbosch", null, PresenceState.Known),
                null,
                null,
                null,
                LocationSource.Reporter,
                LocationPrecision.Approximate,
                "2026.10.27-1"),
            new IntakeConfirmations(
                true, "ZA", "2026.10.27-1", true, true,
                "https://swiftaid.online/privacy", "2026.10.27-1", null, null, null));
    }

    private sealed class FakeSessionEndpointService : IIntakeSessionEndpointService
    {
        public int CreateCalls { get; private set; }

        public int ResolveCalls { get; private set; }

        public int ReceiptCalls { get; private set; }

        public IntakeCapability? ResolvedCapability { get; private set; }

        public byte[] LastProof { get; private set; } = [];

        public Func<IntakeSessionResponse?> Create { get; set; } = () => new IntakeSessionResponse(
            "opaque-capability", DateTimeOffset.UtcNow.AddMinutes(15));

        public Func<IntakeCapability?> Resolve { get; set; } = DefaultCapability;

        public IntakeReceipt? Receipt { get; set; }

        private bool _failNextResolve;

        private IntakeCapability? _postFailCapability;

        public void FailNextResolveThenReturn(IntakeCapability? capability)
        {
            _failNextResolve = true;
            _postFailCapability = capability;
        }

        public Task<IntakeSessionResponse?> CreateAsync(Guid sessionId, ReadOnlyMemory<byte> proof, CancellationToken ct)
        {
            CreateCalls++;
            return Task.FromResult(Create());
        }

        public Task<IntakeCapability?> ResolveCapabilityAsync(
            string opaqueCapability, Guid sessionId, ReadOnlyMemory<byte> proof, CancellationToken ct)
        {
            ResolveCalls++;
            LastProof = proof.ToArray();
            if (_failNextResolve)
            {
                _failNextResolve = false;
                ResolvedCapability = null;
                return Task.FromResult(ResolvedCapability);
            }

            ResolvedCapability = _postFailCapability ?? Resolve();
            return Task.FromResult(ResolvedCapability);
        }

        public Task<IntakeReceipt?> GetReceiptAsync(Guid receiptId, ReadOnlyMemory<byte> proof, CancellationToken ct)
        {
            ReceiptCalls++;
            LastProof = proof.ToArray();
            return Task.FromResult(Receipt);
        }

        public static IntakeCapability DefaultCapability() => new(
            Guid.NewGuid(), Guid.NewGuid(), new byte[32], DateTimeOffset.UtcNow.AddMinutes(15), 10);
    }

    private sealed class FakeIntakeService : IIntakeService
    {
        public Task<IntakeReceipt>? Pending { get; set; }
        public int SubmitCalls { get; private set; }

        public IntakeCapability? LastCapability { get; private set; }

        public Func<IntakeSubmission, IntakeCapability, IntakeReceipt> Submit { get; set; } =
            (submission, _) => new IntakeReceipt(
                Guid.NewGuid(),
                submission.ClientSubmissionId,
                IntakeStatus.ReceivedUnverified,
                IntakeRoutingStatus.NeedsRouting,
                DateTimeOffset.UtcNow);

        public Task<IntakeReceipt> SubmitAsync(
            IntakeSubmission command, IntakeCapability capability, CancellationToken ct)
        {
            SubmitCalls++;
            LastCapability = capability;
            return Pending ?? Task.FromResult(Submit(command, capability));
        }

        public Task<IntakeDecisionResult> DecideAsync(
            IntakeDecision command, SwiftAid.Application.Authorization.ActorContext actor, CancellationToken ct) =>
            Task.FromResult(new IntakeDecisionResult(IntakeDecisionStatus.NotFoundOrNotAuthorized, null));
    }
}
