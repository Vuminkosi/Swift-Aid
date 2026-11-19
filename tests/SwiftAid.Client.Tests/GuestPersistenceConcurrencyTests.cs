using System.Security.Cryptography;
using System.Text.Json;
using SwiftAid.Client.Intake;
using SwiftAid.Contracts.Geography;
using SwiftAid.Contracts.Intake;
using SwiftAid.Mobile.Services.Intake;

namespace SwiftAid.Client.Tests;

public sealed class GuestPersistenceConcurrencyTests
{
    [Theory]
    [InlineData(null, 2)]
    [InlineData(0L, 1)]
    [InlineData(-1L, 1)]
    [InlineData(1L, 3)]
    [InlineData(long.MaxValue, 1)]
    public async Task InvalidVersionWriteConflictsWithoutChangingStoredRecord(long? expected, long next)
    {
        using var root = new TemporaryRoot();
        IGuestSubmissionPersistence store = new EncryptedGuestSubmissionStore(root.Path, new TestInstallKeyStore());
        var draft = await new GuestDraftStore(store).CreateDraftAsync(CreateSubmission());
        var original = (await store.GetAsync(draft.ClientSubmissionId, default))!;
        var exception = await Assert.ThrowsAsync<GuestSubmissionPersistenceConflictException>(() =>
            store.SaveVersionedAsync(original with { Version = next }, expected, default));
        Assert.Equal(original.ClientSubmissionId, exception.ClientSubmissionId);
        Assert.Equal(1, (await store.GetAsync(original.ClientSubmissionId, default))!.Version);
    }

    [Fact]
    public async Task TwoReadersCannotOverwriteWinnerAndRejectedBatchHasNoPrefix()
    {
        using var root = new TemporaryRoot();
        var keys = new TestInstallKeyStore();
        IGuestSubmissionPersistence store = new EncryptedGuestSubmissionStore(root.Path, keys);
        var drafts = new GuestDraftStore(store);
        var attachmentId = Guid.NewGuid();
        var a = await drafts.CreateDraftAsync(CreateSubmission() with { OriginalAudioAttachmentIds = [attachmentId] });
        var b = await drafts.CreateDraftAsync(CreateSubmission());
        var first = (await store.GetAsync(a.ClientSubmissionId, default))!;
        var second = (await store.GetAsync(b.ClientSubmissionId, default))!;
        var winner = first with
        {
            Version = 2,
            FormSnapshot = new(1, "{\"marker\":\"winner\"}"),
            AudioAssets = [new(attachmentId, new string('A', 64), "audio/wav", 6, 900, GuestAudioLocalState.LocalOnly)]
        };
        await store.SaveVersionedAsync(winner, 1, default);
        await Assert.ThrowsAsync<GuestSubmissionPersistenceConflictException>(() =>
            store.SaveBatchVersionedAsync([
                new(second with { Version = 2 }, 1),
                new(first with { Version = 2 }, 1)], default));
        IGuestSubmissionPersistence restarted = new EncryptedGuestSubmissionStore(root.Path, keys);
        Assert.Equal(1, (await restarted.GetAsync(second.ClientSubmissionId, default))!.Version);
        var restoredWinner = (await restarted.GetAsync(first.ClientSubmissionId, default))!;
        Assert.Equal(winner.FormSnapshot, restoredWinner.FormSnapshot);
        Assert.Equal(winner.AudioAssets, restoredWinner.AudioAssets);
        Assert.Contains(attachmentId, restoredWinner.Submission.OriginalAudioAttachmentIds);
        await Assert.ThrowsAsync<GuestSubmissionPersistenceConflictException>(() =>
            restarted.SaveVersionedAsync(first, null, default));
        // Use a valid independent record so only the missing expected row is under test.
        var newDraft = await drafts.CreateDraftAsync(CreateSubmission());
        var newRecord = (await store.GetAsync(newDraft.ClientSubmissionId, default))!;
        using var emptyRoot = new TemporaryRoot();
        IGuestSubmissionPersistence empty = new EncryptedGuestSubmissionStore(emptyRoot.Path, keys);
        await Assert.ThrowsAsync<GuestSubmissionPersistenceConflictException>(() =>
            empty.SaveVersionedAsync(newRecord with { Version = 2 }, 1, default));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public async Task CoordinatorDefersStaleReceiptOrFailureWithoutOverwritingWinner(
        bool permanentFailure, bool cancelReload, bool failReload)
    {
        using var root = new TemporaryRoot();
        IGuestSubmissionPersistence store = new EncryptedGuestSubmissionStore(root.Path, new TestInstallKeyStore());
        var draft = await new GuestDraftStore(store).CreateDraftAsync(CreateSubmission());
        var original = (await store.GetAsync(draft.ClientSubmissionId, default))!;
        var queued = original with
        {
            State = GuestSubmissionLocalState.Queued,
            Version = 2,
            IntakeCapability = "synthetic-capability",
            CapabilityExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1)
        };
        await store.SaveVersionedAsync(queued, 1, default);
        using var cancellation = new CancellationTokenSource();
        var observed = new ObservedPersistence(store, () =>
        {
            if (cancelReload) { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
            if (failReload) throw new GuestSubmissionStoreUnavailableException();
        });
        var transport = new ConcurrentTransport(async () =>
        {
            await store.SaveVersionedAsync(queued with
            {
                Version = 3,
                IntakeCapability = "concurrent-capability"
            }, 2, default);
            if (permanentFailure) throw new GuestIntakeTransportException(false);
            return new IntakeReceipt(Guid.NewGuid(), queued.ClientSubmissionId,
                IntakeStatus.ReceivedUnverified, IntakeRoutingStatus.NeedsRouting, DateTimeOffset.UtcNow);
        });

        var coordinator = new GuestSubmissionCoordinator(observed, transport);
        if (cancelReload)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.SubmitPendingAsync(cancellation.Token));
        else
            Assert.Equal(new GuestSyncSummary(1, 0, 1, 0), await coordinator.SubmitPendingAsync(cancellation.Token));
        Assert.Equal(1, observed.Reloads);
        var winner = (await store.GetAsync(queued.ClientSubmissionId, default))!;
        Assert.Equal(3, winner.Version);
        Assert.Equal(GuestSubmissionLocalState.Queued, winner.State);
        Assert.Equal("concurrent-capability", winner.IntakeCapability);
        Assert.Null(winner.Receipt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiptRefreshDefersStaleResultAndPreservesConcurrentWinner(bool cancelReload)
    {
        using var root = new TemporaryRoot();
        var keys = new TestInstallKeyStore();
        IGuestSubmissionPersistence store = new EncryptedGuestSubmissionStore(root.Path, keys);
        var draft = await new GuestDraftStore(store).CreateDraftAsync(CreateSubmission());
        var original = (await store.GetAsync(draft.ClientSubmissionId, default))!;
        var queued = original with { State = GuestSubmissionLocalState.Queued, Version = 2 };
        await store.SaveVersionedAsync(queued, 1, default);
        var receipt = new IntakeReceipt(Guid.NewGuid(), original.ClientSubmissionId,
            IntakeStatus.ReceivedUnverified, IntakeRoutingStatus.NeedsRouting, DateTimeOffset.UtcNow);
        var received = queued with { State = GuestSubmissionLocalState.Received, Version = 3, Receipt = receipt };
        await store.SaveVersionedAsync(received, 2, default);
        var winningReceipt = receipt with { Status = IntakeStatus.AwaitingReview };
        using var cancellation = new CancellationTokenSource();
        var observed = new ObservedPersistence(store, () =>
        {
            if (cancelReload)
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
        });
        var refreshCalls = 0;
        var transport = new ConcurrentTransport(
            () => throw new InvalidOperationException("Unexpected submission request"),
            async () =>
            {
                refreshCalls++;
                await store.SaveVersionedAsync(received with { Version = 4, Receipt = winningReceipt }, 3, default);
                return receipt with { Status = IntakeStatus.Declined };
            });

        var coordinator = new GuestSubmissionCoordinator(observed, transport);
        if (cancelReload)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.RefreshReceiptsAsync(cancellation.Token));
        else
            Assert.Equal(new GuestReceiptRefreshSummary(0, 1), await coordinator.RefreshReceiptsAsync(cancellation.Token));
        Assert.Equal(1, refreshCalls);
        Assert.Equal(1, observed.Reloads);
        var persisted = (await new EncryptedGuestSubmissionStore(root.Path, keys)
            .GetAsync(received.ClientSubmissionId, default))!;
        Assert.Equal(4, persisted.Version);
        Assert.Equal(GuestSubmissionLocalState.Received, persisted.State);
        Assert.Equal(winningReceipt, persisted.Receipt);
    }

    private sealed class ObservedPersistence(IGuestSubmissionPersistence inner, Action onReload) : IGuestSubmissionPersistence
    {
        public int Reloads { get; private set; }
        public Task<GuestSubmissionRecord?> GetAsync(Guid id, CancellationToken ct)
        {
            Reloads++;
            onReload();
            return inner.GetAsync(id, ct);
        }
        public Task<IReadOnlyList<GuestSubmissionRecord>> GetAllAsync(CancellationToken ct) => inner.GetAllAsync(ct);
        public Task SaveAsync(GuestSubmissionRecord record, CancellationToken ct) => inner.SaveAsync(record, ct);
        public Task SaveBatchAsync(IReadOnlyList<GuestSubmissionRecord> records, CancellationToken ct) => inner.SaveBatchAsync(records, ct);
        public Task SaveVersionedAsync(GuestSubmissionRecord record, long? expected, CancellationToken ct) => inner.SaveVersionedAsync(record, expected, ct);
    }

    private sealed class ConcurrentTransport(Func<Task<IntakeReceipt>> submit, Func<Task<IntakeReceipt>>? refresh = null) : IGuestIntakeTransport
    {
        public Task<IntakeReceipt> SubmitAsync(IntakeSubmission submission, Guid sessionId, string capability,
            string proof, CancellationToken ct) => submit();
        public Task<IntakeSessionResponse> CreateSessionAsync(Guid id, string proof, CancellationToken ct) => throw new InvalidOperationException("Unexpected session request");
        public Task<IntakeReceipt> GetReceiptAsync(Guid id, string proof, CancellationToken ct) =>
            refresh?.Invoke() ?? throw new NotSupportedException();
        public Task<PublicIntakeMetadata> GetMetadataAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<PublicIntakeCountryCatalogue> GetCountriesAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task SharedMobileAndBrowserVersionContract()
    {
        using var scenarios = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "guest-version-contract.json")));
        foreach (var scenario in scenarios.RootElement.EnumerateArray())
        {
            using var root = new TemporaryRoot();
            using var templatesRoot = new TemporaryRoot();
            var keys = new TestInstallKeyStore();
            IGuestSubmissionPersistence store = new EncryptedGuestSubmissionStore(root.Path, keys);
            IGuestSubmissionPersistence templates = new EncryptedGuestSubmissionStore(templatesRoot.Path, keys);
            var records = new Dictionary<string, GuestSubmissionRecord>();
            var expected = new Dictionary<string, GuestSubmissionRecord>();
            foreach (var name in new[] { "a", "b" })
            {
                var draft = await new GuestDraftStore(templates).CreateDraftAsync(CreateSubmission());
                records[name] = (await templates.GetAsync(draft.ClientSubmissionId, default))!;
            }
            foreach (var step in scenario.GetProperty("steps").EnumerateArray())
            {
                var writes = step.GetProperty("writes").EnumerateArray().Select(write =>
                    new GuestSubmissionWrite(records[write.GetProperty("id").GetString()!] with
                    {
                        Version = write.GetProperty("version").GetInt64(),
                        FormSnapshot = new(1, JsonSerializer.Serialize(new { marker = write.GetProperty("marker").GetString() }))
                    }, write.GetProperty("expected").ValueKind == JsonValueKind.Null ? null : write.GetProperty("expected").GetInt64())).ToArray();
                if (step.GetProperty("result").GetString() == "conflict")
                    await Assert.ThrowsAsync<GuestSubmissionPersistenceConflictException>(() => store.SaveBatchVersionedAsync(writes, default));
                else
                {
                    await store.SaveBatchVersionedAsync(writes, default);
                    foreach (var write in writes) expected[write.Record.ClientSubmissionId.ToString()] = write.Record;
                }
                // Reopen the encrypted store after every step: failed batches cannot persist a prefix.
                var persisted = await new EncryptedGuestSubmissionStore(root.Path, keys).GetAllAsync(default);
                Assert.Equal(expected.Count, persisted.Count);
                foreach (var record in persisted)
                {
                    var wanted = expected[record.ClientSubmissionId.ToString()];
                    Assert.Equal(wanted.Version, record.Version);
                    Assert.Equal(wanted.FormSnapshot, record.FormSnapshot);
                }
            }
        }
    }

    [Fact]
    public async Task ConcurrentInstancesHaveExactlyOneWinner()
    {
        using var root = new TemporaryRoot();
        var keys = new TestInstallKeyStore();
        IGuestSubmissionPersistence first = new EncryptedGuestSubmissionStore(root.Path, keys);
        IGuestSubmissionPersistence second = new EncryptedGuestSubmissionStore(root.Path, keys);
        var draft = await new GuestDraftStore(first).CreateDraftAsync(CreateSubmission());
        var snapshot = (await first.GetAsync(draft.ClientSubmissionId, default))!;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> WriteAsync(IGuestSubmissionPersistence store, string marker)
        {
            await start.Task;
            try
            {
                await store.SaveVersionedAsync(snapshot with { Version = 2, FormSnapshot = new(1, marker) }, 1, default);
                return true;
            }
            catch (GuestSubmissionPersistenceConflictException) { return false; }
        }
        var a = WriteAsync(first, "writer-one");
        var b = WriteAsync(second, "writer-two");
        start.SetResult();
        var outcomes = await Task.WhenAll(a, b);
        Assert.Single(outcomes, won => won);
        var winner = (await first.GetAsync(snapshot.ClientSubmissionId, default))!;
        Assert.Equal(2, winner.Version);
        Assert.Equal(outcomes[0] ? "writer-one" : "writer-two", winner.FormSnapshot!.Json);
    }

    [Fact]
    public async Task BatchSnapshotsCallerListBeforeWaitingForFileGate()
    {
        using var root = new TemporaryRoot();
        var keys = new PausingKeyStore();
        IGuestSubmissionPersistence store = new EncryptedGuestSubmissionStore(root.Path, keys);
        var draft = await new GuestDraftStore(store).CreateDraftAsync(CreateSubmission());
        var current = (await store.GetAsync(draft.ClientSubmissionId, default))!;
        keys.Pause = true;
        var holder = store.SaveAsync(current, default);
        await keys.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var writes = new List<GuestSubmissionWrite> { new(current with { Version = 2 }, 1) };
        var pending = store.SaveBatchVersionedAsync(writes, default);
        writes[0] = new(current with { Version = 99 }, 0);
        keys.Release.SetResult();
        await holder;
        await pending;
        Assert.Equal(2, (await store.GetAsync(current.ClientSubmissionId, default))!.Version);
    }

    private sealed class PausingKeyStore : IGuestInstallKeyStore
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
        public bool Pause { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<byte[]> GetOrCreateKeyAsync(CancellationToken cancellationToken)
        {
            if (Pause)
            {
                Pause = false;
                Entered.SetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return _key.ToArray();
        }
    }

    private static IntakeSubmission CreateSubmission() => new(
        1,
        Guid.NewGuid(),
        IntakeSubjectScope.Human,
        "A sample report without clinical advice.",
        ["Observed activity"],
        [],
        new ReportedTime(DateTimeOffset.UtcNow, null, null, null, "+02:00", null, ReportedTimePrecision.Approximate),
        new ReportedTime(null, null, null, null, null, null, ReportedTimePrecision.Unknown),
        new LocationContext("ZA", UnknownPlace(), UnknownPlace(), UnknownPlace(), null, null, null, LocationSource.Reporter,
            LocationPrecision.Approximate, "catalogue-v1"),
        new IntakeConfirmations(true, "ZA", "catalogue-v1", true, true,
            "https://swiftaid.example/privacy", "policy-v1", null, null, null));

    private static OptionalPlace UnknownPlace() => new(null, null, PresenceState.Unknown);

    private sealed class TestInstallKeyStore : IGuestInstallKeyStore
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

        public Task<byte[]> GetOrCreateKeyAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_key.ToArray());
    }

    private sealed class TemporaryRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"swiftaid-concurrency-tests-{Guid.NewGuid():N}");

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
