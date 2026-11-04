using SwiftAid.Client.Intake;
using SwiftAid.Contracts.Geography;
using SwiftAid.Contracts.Intake;

namespace SwiftAid.Client.Tests;

public sealed class GuestSubmissionJourneyTests
{
    [Fact]
    public async Task CreateDraft_Queue_SubmitPending_ReceivesReceiptAndClearsSecrets()
    {
        var persistence = new MemorySubmissionPersistence();
        var transport = new ScriptedTransport();
        var store = new GuestDraftStore(persistence, transport: transport);
        var coordinator = new GuestSubmissionCoordinator(persistence, transport);
        var draft = await store.CreateDraftAsync(ValidSubmission());

        var queued = await store.QueueAsync(draft.ClientSubmissionId);

        Assert.Equal(GuestSubmissionLocalState.Queued, queued.State);
        var summary = await coordinator.SubmitPendingAsync();
        Assert.Equal(1, summary.Received);
        Assert.Equal(0, summary.RetryPending);
        var stored = await persistence.GetAsync(draft.ClientSubmissionId, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(GuestSubmissionLocalState.Received, stored.State);
        Assert.NotNull(stored.Receipt);
        Assert.Equal(draft.ClientSubmissionId, stored.Receipt.ClientSubmissionId);
        Assert.Null(stored.IntakeCapability);
        Assert.Null(stored.CapabilityExpiresAtUtc);
    }

    [Fact]
    public async Task Queue_WhenCountryLeavesCatalogue_ThrowsAndKeepsDraftUntouched()
    {
        var persistence = new MemorySubmissionPersistence();
        var transport = new ScriptedTransport(countries: []);
        var store = new GuestDraftStore(persistence, transport: transport);
        var draft = await store.CreateDraftAsync(ValidSubmission());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.QueueAsync(draft.ClientSubmissionId));

        Assert.Contains("country", exception.Message, StringComparison.OrdinalIgnoreCase);
        var stored = await persistence.GetAsync(draft.ClientSubmissionId, CancellationToken.None);
        Assert.Equal(GuestSubmissionLocalState.Draft, stored?.State);
    }

    [Fact]
    public async Task Queue_Twice_ReturnsCurrentWithoutThrowing()
    {
        var persistence = new MemorySubmissionPersistence();
        var transport = new ScriptedTransport();
        var store = new GuestDraftStore(persistence, transport: transport);
        var draft = await store.CreateDraftAsync(ValidSubmission());
        await store.QueueAsync(draft.ClientSubmissionId);

        var again = await store.QueueAsync(draft.ClientSubmissionId);

        Assert.Equal(GuestSubmissionLocalState.Queued, again.State);
    }

    [Fact]
    public async Task CreateRevision_AfterQueue_Throws()
    {
        var persistence = new MemorySubmissionPersistence();
        var transport = new ScriptedTransport();
        var store = new GuestDraftStore(persistence, transport: transport);
        var draft = await store.CreateDraftAsync(ValidSubmission());
        await store.QueueAsync(draft.ClientSubmissionId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CreateRevisionAsync(draft.ClientSubmissionId, ValidSubmission()));
    }

    [Fact]
    public async Task SubmitPending_RetryableTransportFailure_StaysQueuedWithRetryPending()
    {
        var persistence = new MemorySubmissionPersistence();
        var transport = new ScriptedTransport(submit: _ => throw new GuestIntakeTransportException(isRetryable: true));
        var store = new GuestDraftStore(persistence, transport: transport);
        var coordinator = new GuestSubmissionCoordinator(persistence, transport);
        var draft = await store.CreateDraftAsync(ValidSubmission());
        await store.QueueAsync(draft.ClientSubmissionId);

        var summary = await coordinator.SubmitPendingAsync();

        Assert.Equal(0, summary.Received);
        Assert.Equal(1, summary.RetryPending);
        var stored = await persistence.GetAsync(draft.ClientSubmissionId, CancellationToken.None);
        Assert.Equal(GuestSubmissionLocalState.Queued, stored?.State);
    }

    [Fact]
    public async Task SubmitPending_NonRetryableTransportFailure_MovesToNeedsAttention()
    {
        var persistence = new MemorySubmissionPersistence();
        var transport = new ScriptedTransport(submit: _ => throw new GuestIntakeTransportException(isRetryable: false));
        var store = new GuestDraftStore(persistence, transport: transport);
        var coordinator = new GuestSubmissionCoordinator(persistence, transport);
        var draft = await store.CreateDraftAsync(ValidSubmission());
        await store.QueueAsync(draft.ClientSubmissionId);

        var summary = await coordinator.SubmitPendingAsync();

        Assert.Equal(1, summary.NeedsAttention);
        var stored = await persistence.GetAsync(draft.ClientSubmissionId, CancellationToken.None);
        Assert.Equal(GuestSubmissionLocalState.NeedsAttention, stored?.State);
        Assert.Null(stored?.IntakeCapability);
    }

    [Fact]
    public async Task SubmitPending_TamperedPayload_MovesToNeedsAttention()
    {
        var persistence = new MemorySubmissionPersistence();
        var transport = new ScriptedTransport();
        var store = new GuestDraftStore(persistence, transport: transport);
        var coordinator = new GuestSubmissionCoordinator(persistence, transport);
        var draft = await store.CreateDraftAsync(ValidSubmission());
        await store.QueueAsync(draft.ClientSubmissionId);
        var stored = await persistence.GetAsync(draft.ClientSubmissionId, CancellationToken.None);
        await persistence.SaveAsync(
            stored! with { Submission = stored.Submission with { Narrative = "Edited after queueing." } },
            CancellationToken.None);

        var summary = await coordinator.SubmitPendingAsync();

        Assert.Equal(1, summary.NeedsAttention);
        Assert.Equal(0, transport.SubmitCalls);
    }

    [Fact]
    public async Task SubmitPending_ValidCapability_SubmitsWithoutCreatingSession()
    {
        var persistence = new MemorySubmissionPersistence();
        var transport = new ScriptedTransport();
        var store = new GuestDraftStore(persistence, transport: transport);
        var coordinator = new GuestSubmissionCoordinator(persistence, transport);
        var draft = await store.CreateDraftAsync(ValidSubmission());
        await store.QueueAsync(draft.ClientSubmissionId);
        var stored = await persistence.GetAsync(draft.ClientSubmissionId, CancellationToken.None);
        await persistence.SaveAsync(
            stored! with
            {
                IntakeCapability = "existing-capability",
                CapabilityExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(20)
            },
            CancellationToken.None);

        var summary = await coordinator.SubmitPendingAsync();

        Assert.Equal(1, summary.Received);
        Assert.Equal(0, transport.CreateSessionCalls);
        Assert.Equal(1, transport.SubmitCalls);
    }

    [Fact]
    public async Task SubmitPending_ExpiredCapability_RenewsSessionBeforeSubmit()
    {
        var persistence = new MemorySubmissionPersistence();
        var transport = new ScriptedTransport();
        var store = new GuestDraftStore(persistence, transport: transport);
        var coordinator = new GuestSubmissionCoordinator(persistence, transport);
        var draft = await store.CreateDraftAsync(ValidSubmission());
        await store.QueueAsync(draft.ClientSubmissionId);
        var stored = await persistence.GetAsync(draft.ClientSubmissionId, CancellationToken.None);
        await persistence.SaveAsync(
            stored! with
            {
                IntakeCapability = "stale-capability",
                CapabilityExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
            },
            CancellationToken.None);

        var summary = await coordinator.SubmitPendingAsync();

        Assert.Equal(1, summary.Received);
        Assert.Equal(1, transport.CreateSessionCalls);
    }

    private static IntakeSubmission ValidSubmission() => new(
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

    private sealed class MemorySubmissionPersistence : IGuestSubmissionPersistence
    {
        private readonly Dictionary<Guid, GuestSubmissionRecord> _records = new();

        public Task<GuestSubmissionRecord?> GetAsync(Guid clientSubmissionId, CancellationToken cancellationToken) =>
            Task.FromResult(_records.TryGetValue(clientSubmissionId, out var record) ? record : null);

        public Task<IReadOnlyList<GuestSubmissionRecord>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GuestSubmissionRecord>>([.. _records.Values]);

        public Task SaveAsync(GuestSubmissionRecord submission, CancellationToken cancellationToken)
        {
            _records[submission.ClientSubmissionId] = submission;
            return Task.CompletedTask;
        }

        public Task SaveBatchAsync(IReadOnlyList<GuestSubmissionRecord> submissions, CancellationToken cancellationToken)
        {
            foreach (var submission in submissions)
            {
                _records[submission.ClientSubmissionId] = submission;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class ScriptedTransport(
        Func<IntakeSubmission, IntakeReceipt>? submit = null,
        IReadOnlyList<PublicIntakeCountry>? countries = null) : IGuestIntakeTransport
    {
        private readonly IReadOnlyList<PublicIntakeCountry> _countries =
            countries ?? [new PublicIntakeCountry("ZA", "South Africa")];

        public int CreateSessionCalls { get; private set; }

        public int SubmitCalls { get; private set; }

        public Task<IntakeSessionResponse> CreateSessionAsync(
            Guid sessionId, string receiptProof, CancellationToken cancellationToken)
        {
            CreateSessionCalls++;
            return Task.FromResult(new IntakeSessionResponse(
                "scripted-capability", DateTimeOffset.UtcNow.AddMinutes(30)));
        }

        public Task<IntakeReceipt> SubmitAsync(
            IntakeSubmission submission,
            Guid sessionId,
            string intakeCapability,
            string receiptProof,
            CancellationToken cancellationToken)
        {
            SubmitCalls++;
            if (submit is not null)
            {
                return Task.FromResult(submit(submission));
            }

            return Task.FromResult(new IntakeReceipt(
                Guid.NewGuid(),
                submission.ClientSubmissionId,
                IntakeStatus.ReceivedUnverified,
                IntakeRoutingStatus.NeedsRouting,
                DateTimeOffset.UtcNow));
        }

        public Task<IntakeReceipt> GetReceiptAsync(
            Guid receiptId, string receiptProof, CancellationToken cancellationToken) =>
            Task.FromResult(new IntakeReceipt(
                receiptId, Guid.NewGuid(), IntakeStatus.ReceivedUnverified,
                IntakeRoutingStatus.NeedsRouting, DateTimeOffset.UtcNow));

        public Task<PublicIntakeMetadata> GetMetadataAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new PublicIntakeMetadata(
                "2026.10.27-1", "2026.10.27-1", "/public/v1/intake/metadata/countries",
                "2026.10.27-1", "/privacy"));

        public Task<PublicIntakeCountryCatalogue> GetCountriesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new PublicIntakeCountryCatalogue(
                "2026.10.27-1", "/public/v1/intake/metadata/countries", _countries));
    }
}
