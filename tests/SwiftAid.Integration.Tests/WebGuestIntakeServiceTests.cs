using SwiftAid.Application.Intake;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Contracts.Geography;
using SwiftAid.Contracts.Intake;
using SwiftAid.Gateway.Intake;
using SwiftAid.Gateway.Presentation;
using SwiftAid.Client.Presentation;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SwiftAid.Domain.Intake;
using SwiftAid.Infrastructure.Persistence.Entities.Intake;
using SwiftAid.Infrastructure.Persistence.Repositories.Intake;
using System.Security.Cryptography;
using System.Text.Json;
using System.Reflection;
using SwiftAid.Gateway.Components.Pages;
using SwiftAid.UI.Components.Public;
using SwiftAid.Application.Authorization;
using SwiftAid.Contracts.Commands;

namespace SwiftAid.Integration.Tests;

[Collection(IntakePostgresCollection.Name)]
public sealed class WebGuestIntakeServiceTests
{
    [IntakePostgresFact]
    public async Task ServerReportFormReachesAuthoritativeReceiptAndRetriesOnce()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var limits = IntakeLimits.Default;
        var authority = new PostgresIntakeSessionAuthority(db, limits, Options.Create(new IntakeSessionOptions()));
        var sessions = new IntakeSessionEndpointService(authority, new PostgresIntakeReceiptReader(db), TimeProvider.System);
        var submissions = new PostgresIntakeSubmissionStore(db, limits, new EphemeralDataProtectionProvider());
        var countries = new FixtureCountries();
        var intake = new IntakeService(submissions, new PostgresIntakeDecisionStore(db), new UnusedPromotion(),
            new PostgresIntakeAttachmentAuthority(db), new CoverageRouter(new NoCoverage()),
            new IntakeSubmissionValidator(countries, new FixturePolicy()), limits, TimeProvider.System);
        var web = new WebGuestIntakeService(sessions, intake);
        var client = new WebPublicIntakeSubmissionClient(web);
        var page = new Report();
        typeof(Report).GetProperty("IntakeSubmissionClient", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(page, client);
        var draft = new PublicReportDraft
        {
            SubjectScope = IntakeSubjectScope.Human, Narrative = "Synthetic observation", CountryCode = "ZA",
            CountryConfirmed = true, ReportReviewConfirmed = true, ProcessingConfirmed = true
        };
        var command = draft.ToSubmission("fixture-catalogue", "https://fixture.invalid/policy", "v1",
            [new PublicCountryOption("ZA", "Synthetic country")], true);
        Assert.Equal(Guid.Empty, command.ClientSubmissionId);
        var invalidId = Guid.NewGuid();
        var invalid = await client.SubmitAsync(command with { ClientSubmissionId = invalidId,
            Confirmations = command.Confirmations with { ReportReviewConfirmed = false } });
        Assert.Equal(UiCapabilityResultCode.InvalidRequest, invalid.Code);
        Assert.Null(invalid.Value);
        Assert.Equal(0, await db.Set<IntakeReportEntity>().CountAsync(item => item.ClientSubmissionId == invalidId));
        var unavailableId = Guid.NewGuid();
        countries.Unavailable = true;
        var unavailable = await client.SubmitAsync(command with { ClientSubmissionId = unavailableId });
        Assert.Equal(UiCapabilityResultCode.Unavailable, unavailable.Code);
        Assert.Equal(0, await db.Set<IntakeReportEntity>().CountAsync(item => item.ClientSubmissionId == unavailableId));
        countries.Unavailable = false;
        var send = typeof(Report).GetMethod("SendReportAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        await (Task)send.Invoke(page, [command])!;
        var receipt = (IntakeReceipt?)typeof(Report).GetField("_receipt", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page);
        Assert.NotNull(receipt);
        Assert.NotEqual(Guid.Empty, receipt.ClientSubmissionId);
        var persisted = await web.GetReceiptAsync(receipt.ReceiptId);
        Assert.NotNull(persisted);
        Assert.Equal(receipt.ReceiptId, persisted.ReceiptId);
        Assert.Equal(receipt.ClientSubmissionId, persisted.ClientSubmissionId);
        Assert.Equal(receipt.Status, persisted.Status);
        Assert.Equal(receipt.RoutingStatus, persisted.RoutingStatus);
        Assert.InRange((receipt.ReceivedAtUtc - persisted.ReceivedAtUtc).Duration(), TimeSpan.Zero, TimeSpan.FromTicks(9));
        Assert.Null(await sessions.GetReceiptAsync(receipt.ReceiptId, RandomNumberGenerator.GetBytes(32), default));
        await (Task)send.Invoke(page, [command])!;
        var retried = (IntakeReceipt?)typeof(Report).GetField("_receipt", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page);
        Assert.Equal(receipt.ReceiptId, retried!.ReceiptId);
        Assert.Equal(1, await db.Set<IntakeReportEntity>().CountAsync(item => item.ClientSubmissionId == receipt.ClientSubmissionId));
        Assert.Equal(1, await db.Set<IntakeOutboxEntity>().CountAsync(item => item.ReceiptId == receipt.ReceiptId));
    }

    private sealed class FixtureCountries : IIntakeCountryCatalogue
    {
        public bool Unavailable { get; set; }
        public Task<bool> IsKnownCountryAsync(string countryCode, string catalogueVersion, CancellationToken ct) =>
            Unavailable ? Task.FromException<bool>(new IOException("Synthetic catalogue outage")) :
            Task.FromResult(countryCode == "ZA" && catalogueVersion == "fixture-catalogue");
    }

    private sealed class FixturePolicy : IIntakeConsentPolicy
    {
        public Task<bool> IsCurrentAsync(IntakeConfirmations confirmations, bool includesAudio, CancellationToken ct) =>
            Task.FromResult(confirmations.ProcessingPolicyVersion == "v1" && !includesAudio);
    }

    private sealed class NoCoverage : IIntakeCoverageCatalog
    {
        public Task<IntakeCoverageDecision> FindAsync(LocationContext location, CaseKind kind, CancellationToken ct) =>
            Task.FromResult(new IntakeCoverageDecision(IntakeCoverageMatch.None, null));
    }

    private sealed class UnusedPromotion : IIntakePromotionService
    {
        public Task<IntakeDecisionResult> PromoteAsync(IntakeDecision command, ActorContext actor, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    [IntakePostgresFact]
    public async Task LostAcknowledgementAndExpiredCapabilityReturnOriginalPostgresReceipt()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var limits = IntakeLimits.Default;
        var authority = new PostgresIntakeSessionAuthority(db, limits, Options.Create(new IntakeSessionOptions()));
        var sessions = new IntakeSessionEndpointService(authority, new PostgresIntakeReceiptReader(db), TimeProvider.System);
        var submissions = new PostgresIntakeSubmissionStore(db, limits, new EphemeralDataProtectionProvider());
        var intake = new CommitThenDropIntakeService(submissions);
        var service = new WebGuestIntakeService(sessions, intake);
        var submission = GuestSubmissionSamples.Human();

        await Assert.ThrowsAsync<IOException>(() => service.SubmitAsync(submission));
        var sessionId = intake.SessionId;
        var originalReceipt = intake.FirstReceipt!;
        db.ChangeTracker.Clear();
        var past = DateTimeOffset.UtcNow.AddMinutes(-2);
        await db.Set<IntakeCapabilityEntity>().Where(item => item.SessionId == sessionId)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.CreatedAtUtc, past)
                .SetProperty(item => item.ExpiresAtUtc, past.AddMinutes(1)));
        db.ChangeTracker.Clear();

        var retried = await service.SubmitAsync(submission);

        Assert.Equal(originalReceipt.ReceiptId, retried.ReceiptId);
        Assert.Equal(sessionId, intake.SessionId);
        Assert.Equal(1, await db.Set<IntakeReportEntity>().CountAsync(item => item.SessionId == sessionId &&
            item.ClientSubmissionId == submission.ClientSubmissionId));
        Assert.Equal(1, await db.Set<IntakeOutboxEntity>().CountAsync(item => item.ReceiptId == originalReceipt.ReceiptId));
        var conflict = await Assert.ThrowsAsync<IntakeSubmissionException>(() => service.SubmitAsync(
            submission with { Narrative = "A different synthetic report" }));
        Assert.Equal(IntakeErrorCode.SubmissionConflict, conflict.Code);
        Assert.Equal(1, await db.Set<IntakeReportEntity>().CountAsync(item => item.SessionId == sessionId &&
            item.ClientSubmissionId == submission.ClientSubmissionId));
    }

    private sealed class CommitThenDropIntakeService(PostgresIntakeSubmissionStore store) : IIntakeService
    {
        public Guid SessionId { get; private set; }
        public IntakeReceipt? FirstReceipt { get; private set; }

        public async Task<IntakeReceipt> SubmitAsync(IntakeSubmission submission, IntakeCapability capability, CancellationToken ct)
        {
            SessionId = capability.SessionId;
            var digest = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(submission));
            var receipt = await store.CommitAsync(submission, capability, digest,
                new CoverageResult(IntakeRoutingStatus.NeedsRouting, null), DateTimeOffset.UtcNow, [], ct);
            if (FirstReceipt is null)
            {
                FirstReceipt = receipt;
                throw new IOException("Synthetic lost acknowledgement after commit.");
            }
            return receipt;
        }

        public Task<IntakeDecisionResult> DecideAsync(IntakeDecision command,
            SwiftAid.Application.Authorization.ActorContext actor, CancellationToken ct) => throw new NotSupportedException();
    }
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
        Assert.Equal(sessions.CreatedSessions[0].SessionId, sessions.CreatedSessions[1].SessionId);
        Assert.Equal(sessions.CreatedSessions[0].Proof, sessions.CreatedSessions[1].Proof);
        Assert.Equal(submission.ClientSubmissionId, receipt.ClientSubmissionId);
        Assert.Equal(2, intake.SubmitCalls);
    }

    [Fact]
    public async Task AmbiguousSubmissionAndExpiredCapabilityRetryWithinOriginalSession()
    {
        var sessions = new FakeSessionEndpointService();
        var attempts = 0;
        var committed = new Dictionary<(Guid SessionId, Guid SubmissionId), IntakeReceipt>();
        var intake = new FakeIntakeService
        {
            Submit = (submission, capability) =>
            {
                var key = (capability.SessionId, submission.ClientSubmissionId);
                if (!committed.TryGetValue(key, out var receipt))
                    committed[key] = receipt = new IntakeReceipt(Guid.NewGuid(), submission.ClientSubmissionId,
                        IntakeStatus.ReceivedUnverified, IntakeRoutingStatus.NeedsRouting, DateTimeOffset.UtcNow);
                if (++attempts == 1) throw new IOException("Synthetic lost acknowledgement");
                return receipt;
            }
        };
        var service = new WebGuestIntakeService(sessions, intake);
        var submission = GuestSubmissionSamples.Human();
        await Assert.ThrowsAsync<IOException>(() => service.SubmitAsync(submission));
        sessions.FailNextResolveThenReturn(FakeSessionEndpointService.DefaultCapability());

        var receipt = await service.SubmitAsync(submission);

        Assert.Equal(submission.ClientSubmissionId, receipt.ClientSubmissionId);
        Assert.Equal(2, intake.SubmitCalls);
        Assert.Single(committed);
        Assert.Equal(Assert.Single(committed).Value.ReceiptId, receipt.ReceiptId);
        Assert.Equal(2, sessions.CreatedSessions.Count);
        Assert.Equal(sessions.CreatedSessions[0].SessionId, sessions.CreatedSessions[1].SessionId);
        Assert.Equal(sessions.CreatedSessions[0].Proof, sessions.CreatedSessions[1].Proof);
    }

    [Fact]
    public async Task CapabilityRejectedDuringSubmissionIsRenewedForSameSession()
    {
        var sessions = new FakeSessionEndpointService();
        var attempts = 0;
        var intake = new FakeIntakeService
        {
            Submit = (submission, _) => ++attempts == 1
                ? throw new IntakeSubmissionException(IntakeErrorCode.CapabilityInvalid)
                : new IntakeReceipt(Guid.NewGuid(), submission.ClientSubmissionId, IntakeStatus.ReceivedUnverified,
                    IntakeRoutingStatus.NeedsRouting, DateTimeOffset.UtcNow)
        };
        var service = new WebGuestIntakeService(sessions, intake);

        await service.SubmitAsync(GuestSubmissionSamples.Human());

        Assert.Equal(2, intake.SubmitCalls);
        Assert.Equal(2, sessions.CreatedSessions.Count);
        Assert.Equal(sessions.CreatedSessions[0].SessionId, sessions.CreatedSessions[1].SessionId);
        Assert.Equal(sessions.CreatedSessions[0].Proof, sessions.CreatedSessions[1].Proof);
    }

    [Fact]
    public async Task FailedCapabilityRenewalDoesNotDiscardOriginalSession()
    {
        var sessions = new FakeSessionEndpointService();
        var intake = new FakeIntakeService();
        var service = new WebGuestIntakeService(sessions, intake);
        await service.SubmitAsync(GuestSubmissionSamples.Human());
        sessions.FailNextResolveThenReturn(FakeSessionEndpointService.DefaultCapability());
        sessions.Create = () => null;
        await Assert.ThrowsAsync<IntakeSubmissionException>(() => service.SubmitAsync(GuestSubmissionSamples.Human()));
        sessions.Create = () => new IntakeSessionResponse("renewed", DateTimeOffset.UtcNow.AddMinutes(15));

        await service.SubmitAsync(GuestSubmissionSamples.Human());

        Assert.Equal(3, sessions.CreatedSessions.Count);
        Assert.All(sessions.CreatedSessions, item => Assert.Equal(sessions.CreatedSessions[0].SessionId, item.SessionId));
        Assert.All(sessions.CreatedSessions, item => Assert.Equal(sessions.CreatedSessions[0].Proof, item.Proof));
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
        public List<(Guid SessionId, byte[] Proof)> CreatedSessions { get; } = [];

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
            CreatedSessions.Add((sessionId, proof.ToArray()));
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

            var candidate = _postFailCapability ?? Resolve();
            ResolvedCapability = candidate is null ? null : candidate with { SessionId = sessionId };
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
