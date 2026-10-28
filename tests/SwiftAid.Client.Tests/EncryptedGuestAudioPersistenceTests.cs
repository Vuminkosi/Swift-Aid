using System.Security.Cryptography;
using System.Text.Json;
using SwiftAid.Client.Intake;
using SwiftAid.Contracts.Geography;
using SwiftAid.Contracts.Intake;
using SwiftAid.Mobile.Services.Intake;
using Xunit.Sdk;

namespace SwiftAid.Client.Tests;

public sealed class EncryptedGuestAudioPersistenceTests
{
    [Fact]
    public async Task CleanupJournalPersistsPendingAndTerminalStateAcrossInstances()
    {
        using var root = new TemporaryRoot();
        var keys = new TestInstallKeyStore();
        var intent = NewIntent();

        var first = new EncryptedGuestAudioCleanupJournal(root.Path, keys);
        Assert.Equal(GuestAudioCleanupJournalCode.Recorded,
            (await first.EnsurePendingAsync(intent)).Code);

        var restarted = new EncryptedGuestAudioCleanupJournal(root.Path, keys);
        Assert.Equal(new[] { intent }, await restarted.GetPendingAsync());
        Assert.Equal(GuestAudioCleanupJournalCode.Completed,
            (await restarted.CompleteAsync(intent.IntentKey)).Code);

        var afterRestart = new EncryptedGuestAudioCleanupJournal(root.Path, keys);
        Assert.Empty(await afterRestart.GetPendingAsync());
        Assert.Equal(GuestAudioCleanupJournalCode.AlreadyCompleted,
            (await afterRestart.EnsurePendingAsync(intent)).Code);
    }

    [Fact]
    public async Task CleanupJournalCorruptionFailsClosed()
    {
        using var root = new TemporaryRoot();
        var keys = new TestInstallKeyStore();
        var journal = new EncryptedGuestAudioCleanupJournal(root.Path, keys);
        var intent = NewIntent();
        Assert.Equal(GuestAudioCleanupJournalCode.Recorded,
            (await journal.EnsurePendingAsync(intent)).Code);

        var journalPath = Path.Combine(root.Path, "guest-intake", "audio-cleanup", "intents.v1.bin");
        var bytes = await File.ReadAllBytesAsync(journalPath);
        bytes[^1] ^= 0x5A;
        await File.WriteAllBytesAsync(journalPath, bytes);

        var restarted = new EncryptedGuestAudioCleanupJournal(root.Path, keys);
        await Assert.ThrowsAsync<GuestSubmissionStoreUnavailableException>(
            () => restarted.GetPendingAsync());
    }

    [Fact]
    public async Task AudioCiphertextTamperingFailsClosed()
    {
        using var root = new TemporaryRoot();
        var keys = new TestInstallKeyStore();
        var submissions = new EncryptedGuestSubmissionStore(root.Path, keys);
        var fixture = new GuestAudioFixture(root.Path, keys, submissions);
        var draft = await fixture.CreateDraftWithAudioAsync();
        var attachmentId = fixture.AttachmentId;
        var audioPath = Path.Combine(root.Path, "guest-intake", "audio", $"{attachmentId:N}.v1.bin");
        var bytes = await File.ReadAllBytesAsync(audioPath);
        bytes[^1] ^= 0x80;
        await File.WriteAllBytesAsync(audioPath, bytes);

        var restarted = new EncryptedGuestAudioStore(root.Path, keys, submissions);
        await Assert.ThrowsAsync<GuestSubmissionStoreUnavailableException>(
            () => restarted.ReadOriginalAsync(draft.ClientSubmissionId, attachmentId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdempotentSaveRepairsMissingOrTamperedAudioFile(bool tamperBeforeRetry)
    {
        using var root = new TemporaryRoot();
        var keys = new TestInstallKeyStore();
        var submissions = new EncryptedGuestSubmissionStore(root.Path, keys);
        var fixture = new GuestAudioFixture(root.Path, keys, submissions);
        var draft = await fixture.CreateDraftWithAudioAsync();
        var path = Path.Combine(root.Path, "guest-intake", "audio", $"{fixture.AttachmentId:N}.v1.bin");

        if (tamperBeforeRetry)
        {
            var encryptedBytes = await File.ReadAllBytesAsync(path);
            encryptedBytes[^1] ^= 0xA5;
            await File.WriteAllBytesAsync(path, encryptedBytes);
        }
        else
        {
            File.Delete(path);
        }

        var savedAsset = await fixture.Audio.SaveOriginalAsync(
            draft.ClientSubmissionId, fixture.AttachmentId, "audio/wav", 800,
            new MemoryStream(fixture.AudioBytes));

        Assert.Equal(fixture.AudioBytes,
            await fixture.Audio.ReadOriginalAsync(draft.ClientSubmissionId, fixture.AttachmentId));
        Assert.True(File.Exists(path));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(fixture.AudioBytes)), savedAsset.Sha256);
        var persisted = Assert.IsType<GuestSubmissionRecord>(
            await submissions.GetAsync(draft.ClientSubmissionId, CancellationToken.None));
        Assert.Equal(2, persisted.Version);
        Assert.Single(persisted.AudioAssets!);
    }

    [Fact]
    public async Task CleanupRequiresExactSupersededSourceAndReplacementVersions()
    {
        using var root = new TemporaryRoot();
        var keys = new TestInstallKeyStore();
        var submissions = new EncryptedGuestSubmissionStore(root.Path, keys);
        var fixture = new GuestAudioFixture(root.Path, keys, submissions);
        var original = await fixture.CreateDraftWithAudioAsync();
        var draftReplacement = await fixture.Drafts.CreateDraftAsync(fixture.NewSubmission(includeAudio: false));
        var draftSource = Assert.IsType<GuestSubmissionRecord>(
            await submissions.GetAsync(original.ClientSubmissionId, CancellationToken.None));
        var draftTarget = Assert.IsType<GuestSubmissionRecord>(
            await submissions.GetAsync(draftReplacement.ClientSubmissionId, CancellationToken.None));
        var beforeSupersede = fixture.Intent(draftSource, draftTarget, draftSource.Version);
        var cleanup = new EncryptedGuestAudioStore(root.Path, keys, submissions);
        Assert.Equal(GuestAudioCleanupCode.NotSafe,
            (await cleanup.CleanupSupersededOriginalAsync(beforeSupersede)).Code);
        Assert.Equal(fixture.AudioBytes,
            await cleanup.ReadOriginalAsync(original.ClientSubmissionId, fixture.AttachmentId));

        var revised = await fixture.Drafts.CreateRevisionAsync(original.ClientSubmissionId, fixture.NewSubmission(includeAudio: false));
        var superseded = Assert.IsType<GuestSubmissionRecord>(
            await submissions.GetAsync(original.ClientSubmissionId, CancellationToken.None));
        var replacement = Assert.IsType<GuestSubmissionRecord>(
            await submissions.GetAsync(revised.ClientSubmissionId, CancellationToken.None));
        var staleIntent = fixture.Intent(superseded, replacement, expectedSupersededVersion: superseded.Version - 1);

        Assert.Equal(GuestAudioCleanupCode.NotSafe,
            (await cleanup.CleanupSupersededOriginalAsync(staleIntent)).Code);
        Assert.Equal(fixture.AudioBytes,
            await cleanup.ReadOriginalAsync(original.ClientSubmissionId, fixture.AttachmentId));

        var exactIntent = fixture.Intent(superseded, replacement, superseded.Version);
        Assert.Equal(GuestAudioCleanupCode.Removed,
            (await cleanup.CleanupSupersededOriginalAsync(exactIntent)).Code);
        await Assert.ThrowsAsync<GuestSubmissionStoreUnavailableException>(
            () => cleanup.ReadOriginalAsync(original.ClientSubmissionId, fixture.AttachmentId));
    }

    [Fact]
    public async Task SharedAudioReferencedByQueuedRecordIsRetainedForHistory()
    {
        using var root = new TemporaryRoot();
        var keys = new TestInstallKeyStore();
        var submissions = new EncryptedGuestSubmissionStore(root.Path, keys);
        var fixture = new GuestAudioFixture(root.Path, keys, submissions);
        var original = await fixture.CreateDraftWithAudioAsync();

        var secondSubmission = fixture.NewSubmission() with
        {
            ClientSubmissionId = Guid.NewGuid(),
            OriginalAudioAttachmentIds = [fixture.AttachmentId]
        };
        var second = await fixture.Drafts.CreateDraftAsync(secondSubmission);
        var firstRecord = Assert.IsType<GuestSubmissionRecord>(
            await submissions.GetAsync(original.ClientSubmissionId, CancellationToken.None));
        var sharedAsset = Assert.Single(firstRecord.AudioAssets!);
        var secondRecord = Assert.IsType<GuestSubmissionRecord>(
            await submissions.GetAsync(second.ClientSubmissionId, CancellationToken.None));
        await submissions.SaveAsync(secondRecord with
        {
            State = GuestSubmissionLocalState.Queued,
            Version = secondRecord.Version + 1,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            AudioAssets = [sharedAsset]
        }, CancellationToken.None);

        var revised = await fixture.Drafts.CreateRevisionAsync(original.ClientSubmissionId, fixture.NewSubmission(includeAudio: false));
        var superseded = Assert.IsType<GuestSubmissionRecord>(
            await submissions.GetAsync(original.ClientSubmissionId, CancellationToken.None));
        var replacement = Assert.IsType<GuestSubmissionRecord>(
            await submissions.GetAsync(revised.ClientSubmissionId, CancellationToken.None));
        var intent = fixture.Intent(superseded, replacement, superseded.Version);

        Assert.Equal(GuestAudioCleanupCode.RetainedForHistory,
            (await fixture.Audio.CleanupSupersededOriginalAsync(intent)).Code);
        var newAudioInstance = new EncryptedGuestAudioStore(root.Path, keys, submissions);
        Assert.Equal(fixture.AudioBytes,
            await newAudioInstance.ReadOriginalAsync(second.ClientSubmissionId, fixture.AttachmentId));
    }

    [Fact]
    public async Task RetakeLeaseWinsBeforeWaitingSaveAndPreventsAnOrphanedAudioFile()
    {
        using var root = new TemporaryRoot();
        var keys = new TestInstallKeyStore();
        var submissions = new EncryptedGuestSubmissionStore(root.Path, keys);
        var fixture = new GuestAudioFixture(root.Path, keys, submissions);
        var original = await fixture.CreateDraftWithAudioAsync();
        var before = Assert.IsType<GuestSubmissionRecord>(
            await submissions.GetAsync(original.ClientSubmissionId, CancellationToken.None));
        var gate = new PausingMutationGate();
        var journal = new EncryptedGuestAudioCleanupJournal(root.Path, keys);
        var audio = new EncryptedGuestAudioStore(root.Path, keys, submissions, mutationGate: gate);
        var drafts = new GuestDraftStore(submissions, cleanupJournal: journal, audioStore: audio, mutationGate: gate);
        var audioPath = Path.Combine(root.Path, "guest-intake", "audio", $"{fixture.AttachmentId:N}.v1.bin");

        var retakeTask = drafts.RetakeAudioAsync(original.ClientSubmissionId, fixture.AttachmentId, before.Version);
        Task<GuestAudioAsset>? waitingSave = null;
        try
        {
            await gate.FirstLeaseHeld.WaitAsync(TimeSpan.FromSeconds(10));
            waitingSave = audio.SaveOriginalAsync(original.ClientSubmissionId, fixture.AttachmentId,
                "audio/wav", 800, new MemoryStream([0x52, 0x49, 0x46, 0x46, 0x09, 0x08]));
            await gate.SecondAcquireAttempted.WaitAsync(TimeSpan.FromSeconds(10));
            gate.ReleaseFirstLease();

            var retake = await retakeTask;
            Assert.Equal(GuestAudioRetakeStatus.Replaced, retake.Status);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await waitingSave);

            var superseded = Assert.IsType<GuestSubmissionRecord>(
                await submissions.GetAsync(original.ClientSubmissionId, CancellationToken.None));
            var replacement = Assert.IsType<GuestSubmissionRecord>(
                await submissions.GetAsync(retake.ReplacementSubmissionId!.Value, CancellationToken.None));
            Assert.Equal(GuestSubmissionLocalState.Superseded, superseded.State);
            Assert.Contains(superseded.AudioAssets!, item => item.AttachmentId == fixture.AttachmentId);
            Assert.DoesNotContain(fixture.AttachmentId, replacement.Submission.OriginalAudioAttachmentIds);
            Assert.Empty(replacement.AudioAssets!);
            Assert.False(File.Exists(audioPath));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(audioPath)!, "*.v1.bin"));
        }
        finally
        {
            gate.ReleaseFirstLease();
        }
    }

    [Fact]
    public async Task SaveOriginalLeaseWinsBeforeRetakeAndRetakeConflictsWithoutDroppingEitherAsset()
    {
        using var root = new TemporaryRoot();
        var keys = new TestInstallKeyStore();
        var submissions = new EncryptedGuestSubmissionStore(root.Path, keys);
        var fixture = new GuestAudioFixture(root.Path, keys, submissions);
        var secondAttachmentId = Guid.NewGuid();
        var original = await fixture.Drafts.CreateDraftAsync(
            fixture.NewSubmissionWithAudioIds(fixture.AttachmentId, secondAttachmentId));
        await fixture.Audio.SaveOriginalAsync(original.ClientSubmissionId, fixture.AttachmentId,
            "audio/wav", 800, new MemoryStream(fixture.AudioBytes));
        var before = Assert.IsType<GuestSubmissionRecord>(
            await submissions.GetAsync(original.ClientSubmissionId, CancellationToken.None));
        var gate = new PausingMutationGate();
        var journal = new EncryptedGuestAudioCleanupJournal(root.Path, keys);
        var audio = new EncryptedGuestAudioStore(root.Path, keys, submissions, mutationGate: gate);
        var drafts = new GuestDraftStore(submissions, cleanupJournal: journal, audioStore: audio, mutationGate: gate);
        var secondAudioBytes = new byte[] { 0x52, 0x49, 0x46, 0x46, 0x0A, 0x0B };

        var saveTask = audio.SaveOriginalAsync(original.ClientSubmissionId, secondAttachmentId,
            "audio/wav", 950, new MemoryStream(secondAudioBytes));
        Task<GuestAudioRetakeResult>? waitingRetake = null;
        try
        {
            await gate.FirstLeaseHeld.WaitAsync(TimeSpan.FromSeconds(10));
            waitingRetake = drafts.RetakeAudioAsync(original.ClientSubmissionId, fixture.AttachmentId, before.Version);
            await gate.SecondAcquireAttempted.WaitAsync(TimeSpan.FromSeconds(10));
            gate.ReleaseFirstLease();

            var addedAsset = await saveTask;
            Assert.Equal(secondAttachmentId, addedAsset.AttachmentId);
            var retake = await waitingRetake;
            Assert.Equal(GuestAudioRetakeStatus.Conflict, retake.Status);
            Assert.Null(retake.ReplacementSubmissionId);

            var current = Assert.IsType<GuestSubmissionRecord>(
                await submissions.GetAsync(original.ClientSubmissionId, CancellationToken.None));
            Assert.Equal(GuestSubmissionLocalState.Draft, current.State);
            Assert.Equal(before.Version + 1, current.Version);
            Assert.Contains(current.AudioAssets!, asset => asset.AttachmentId == fixture.AttachmentId);
            Assert.Contains(current.AudioAssets!, asset => asset.AttachmentId == secondAttachmentId);
            Assert.Equal(fixture.AudioBytes,
                await audio.ReadOriginalAsync(original.ClientSubmissionId, fixture.AttachmentId));
            Assert.Equal(secondAudioBytes,
                await audio.ReadOriginalAsync(original.ClientSubmissionId, secondAttachmentId));
            var audioFiles = Directory.GetFiles(Path.Combine(root.Path, "guest-intake", "audio"), "*.v1.bin");
            Assert.Equal(2, audioFiles.Length);
            Assert.Contains(audioFiles, path => Path.GetFileName(path) == $"{fixture.AttachmentId:N}.v1.bin");
            Assert.Contains(audioFiles, path => Path.GetFileName(path) == $"{secondAttachmentId:N}.v1.bin");
        }
        finally
        {
            gate.ReleaseFirstLease();
        }
    }

    [Fact]
    public async Task DuplicateAttachmentIdAcrossSubmissionsIsRejectedWithoutOverwritingFirstAudio()
    {
        using var root = new TemporaryRoot();
        var keys = new TestInstallKeyStore();
        var submissions = new EncryptedGuestSubmissionStore(root.Path, keys);
        var fixture = new GuestAudioFixture(root.Path, keys, submissions);
        var first = await fixture.CreateDraftWithAudioAsync();
        var second = await fixture.Drafts.CreateDraftAsync(
            fixture.NewSubmissionWithAudioIds(fixture.AttachmentId));
        var secondBytes = new byte[] { 0x52, 0x49, 0x46, 0x46, 0x07, 0x06 };

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Audio.SaveOriginalAsync(
            second.ClientSubmissionId, fixture.AttachmentId, "audio/wav", 850,
            new MemoryStream(secondBytes)));

        Assert.Equal(fixture.AudioBytes,
            await fixture.Audio.ReadOriginalAsync(first.ClientSubmissionId, fixture.AttachmentId));
        var firstRecord = Assert.IsType<GuestSubmissionRecord>(
            await submissions.GetAsync(first.ClientSubmissionId, CancellationToken.None));
        var secondRecord = Assert.IsType<GuestSubmissionRecord>(
            await submissions.GetAsync(second.ClientSubmissionId, CancellationToken.None));
        Assert.Contains(firstRecord.AudioAssets!, asset => asset.AttachmentId == fixture.AttachmentId);
        Assert.Empty(secondRecord.AudioAssets ?? Array.Empty<GuestAudioAsset>());
        Assert.Single(Directory.GetFiles(Path.Combine(root.Path, "guest-intake", "audio"), "*.v1.bin"));
    }

    [Fact]
    public async Task ForeignAttachmentConflictRejectsIdempotentSaveBeforeAnyFileWrite()
    {
        using var root = new TemporaryRoot();
        var keys = new TestInstallKeyStore();
        var submissions = new EncryptedGuestSubmissionStore(root.Path, keys);
        var fixture = new GuestAudioFixture(root.Path, keys, submissions);
        var first = await fixture.CreateDraftWithAudioAsync();
        var second = await fixture.Drafts.CreateDraftAsync(
            fixture.NewSubmissionWithAudioIds(fixture.AttachmentId));
        var path = Path.Combine(root.Path, "guest-intake", "audio", $"{fixture.AttachmentId:N}.v1.bin");
        var beforeWriteAttempt = await File.ReadAllBytesAsync(path);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Audio.SaveOriginalAsync(
            first.ClientSubmissionId, fixture.AttachmentId, "audio/wav", 800,
            new MemoryStream(fixture.AudioBytes)));

        Assert.Equal(beforeWriteAttempt, await File.ReadAllBytesAsync(path));
        Assert.Equal(fixture.AudioBytes,
            await fixture.Audio.ReadOriginalAsync(first.ClientSubmissionId, fixture.AttachmentId));
        var secondRecord = Assert.IsType<GuestSubmissionRecord>(
            await submissions.GetAsync(second.ClientSubmissionId, CancellationToken.None));
        Assert.Empty(secondRecord.AudioAssets ?? Array.Empty<GuestAudioAsset>());
    }

    [Fact]
    public async Task UncertainDeleteCanBeRetriedAfterNewAudioStoreInstance()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("The deterministic open-file deletion failure is supported by Windows file sharing semantics.");
        }

        using var root = new TemporaryRoot();
        var keys = new TestInstallKeyStore();
        var submissions = new EncryptedGuestSubmissionStore(root.Path, keys);
        var fixture = new GuestAudioFixture(root.Path, keys, submissions);
        var original = await fixture.CreateDraftWithAudioAsync();
        var revised = await fixture.Drafts.CreateRevisionAsync(original.ClientSubmissionId, fixture.NewSubmission(includeAudio: false));
        var superseded = Assert.IsType<GuestSubmissionRecord>(
            await submissions.GetAsync(original.ClientSubmissionId, CancellationToken.None));
        var replacement = Assert.IsType<GuestSubmissionRecord>(
            await submissions.GetAsync(revised.ClientSubmissionId, CancellationToken.None));
        var intent = fixture.Intent(superseded, replacement, superseded.Version);
        var path = Path.Combine(root.Path, "guest-intake", "audio", $"{fixture.AttachmentId:N}.v1.bin");
        Assert.Equal(GuestAudioCleanupJournalCode.Recorded,
            (await fixture.Journal.EnsurePendingAsync(intent)).Code);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var firstWorker = new GuestDraftStore(submissions,
                cleanupJournal: fixture.Journal,
                audioStore: fixture.Audio);
            var uncertain = await firstWorker.RetryPendingAudioCleanupAsync();
            Assert.Equal(1, uncertain.Failed);
            Assert.Single(await fixture.Journal.GetPendingAsync());
        }

        var restartedSubmissions = new EncryptedGuestSubmissionStore(root.Path, keys);
        var restartedJournal = new EncryptedGuestAudioCleanupJournal(root.Path, keys);
        var retryWorker = new GuestDraftStore(restartedSubmissions,
            cleanupJournal: restartedJournal,
            audioStore: new EncryptedGuestAudioStore(root.Path, keys, restartedSubmissions));
        var retried = await retryWorker.RetryPendingAudioCleanupAsync();
        Assert.Equal(1, retried.Completed);
        Assert.Empty(await new EncryptedGuestAudioCleanupJournal(root.Path, keys).GetPendingAsync());
    }

    private static GuestAudioCleanupIntent NewIntent() => new(
        $"guest-audio-cleanup:{Guid.NewGuid():N}", Guid.NewGuid(), 2, Guid.NewGuid(), 1,
        Guid.NewGuid(), DateTimeOffset.UtcNow);

    private sealed class TestInstallKeyStore : IGuestInstallKeyStore
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

        public Task<byte[]> GetOrCreateKeyAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_key.ToArray());
    }

    private sealed class TemporaryRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"swiftaid-audio-tests-{Guid.NewGuid():N}");

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class GuestAudioFixture
    {
        public GuestAudioFixture(string root, IGuestInstallKeyStore keys, IGuestSubmissionPersistence submissions)
        {
            Root = root;
            Keys = keys;
            Submissions = submissions;
            Audio = new EncryptedGuestAudioStore(root, keys, submissions);
            Journal = new EncryptedGuestAudioCleanupJournal(root, keys);
            Drafts = new GuestDraftStore(submissions, cleanupJournal: Journal, audioStore: Audio);
        }

        public string Root { get; }

        public IGuestInstallKeyStore Keys { get; }

        public IGuestSubmissionPersistence Submissions { get; }

        public EncryptedGuestAudioStore Audio { get; }

        public EncryptedGuestAudioCleanupJournal Journal { get; }

        public GuestDraftStore Drafts { get; }

        public Guid AttachmentId { get; } = Guid.NewGuid();

        public byte[] AudioBytes { get; } = [0x52, 0x49, 0x46, 0x46, 0x01, 0x02, 0x03, 0x04];

        public IntakeSubmission NewSubmission(bool includeAudio = true) => new(
            1, Guid.NewGuid(), IntakeSubjectScope.Human, "A sample report without clinical advice.",
            ["Observed activity"], includeAudio ? new[] { AttachmentId } : Array.Empty<Guid>(),
            new ReportedTime(DateTimeOffset.UtcNow, null, null, null, "+02:00", null, ReportedTimePrecision.Approximate),
            new ReportedTime(null, null, null, null, null, null, ReportedTimePrecision.Unknown),
            new LocationContext("ZA", UnknownPlace(), UnknownPlace(), UnknownPlace(), null, null, null, LocationSource.Reporter,
                LocationPrecision.Approximate, "catalogue-v1"),
            new IntakeConfirmations(true, "ZA", "catalogue-v1", true, true,
                "https://swiftaid.example/privacy", "policy-v1", null, null, null));

        public IntakeSubmission NewSubmissionWithAudioIds(params Guid[] attachmentIds) =>
            NewSubmission(includeAudio: false) with { OriginalAudioAttachmentIds = attachmentIds };

        private static OptionalPlace UnknownPlace() => new(null, null, PresenceState.Unknown);

        public async Task<GuestSubmissionSummary> CreateDraftWithAudioAsync()
        {
            var draft = await Drafts.CreateDraftAsync(NewSubmission());
            await Audio.SaveOriginalAsync(draft.ClientSubmissionId, AttachmentId, "audio/wav", 800,
                new MemoryStream(AudioBytes));
            return draft;
        }

        public GuestAudioCleanupIntent Intent(
            GuestSubmissionRecord superseded,
            GuestSubmissionRecord replacement,
            long expectedSupersededVersion) => new(
                $"guest-audio-retake:{superseded.ClientSubmissionId:N}:{expectedSupersededVersion}:{replacement.ClientSubmissionId:N}:{AttachmentId:N}",
                superseded.ClientSubmissionId,
                expectedSupersededVersion,
                replacement.ClientSubmissionId,
                replacement.Version,
                AttachmentId,
                DateTimeOffset.UtcNow);
    }

    private sealed class PausingMutationGate : IGuestDraftMutationGate
    {
        private readonly GuestDraftMutationGate _inner = new();
        private readonly TaskCompletionSource _firstLeaseHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondAttempted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _acquisitionCount;

        public Task FirstLeaseHeld => _firstLeaseHeld.Task;

        public Task SecondAcquireAttempted => _secondAttempted.Task;

        public void ReleaseFirstLease() => _releaseFirst.TrySetResult();

        public ValueTask<IGuestDraftMutationLease> AcquireAsync(CancellationToken cancellationToken = default)
        {
            var acquisition = Interlocked.Increment(ref _acquisitionCount);
            if (acquisition == 2) _secondAttempted.TrySetResult();
            return AcquireCoreAsync(acquisition, cancellationToken);
        }

        private async ValueTask<IGuestDraftMutationLease> AcquireCoreAsync(
            int acquisition,
            CancellationToken cancellationToken)
        {
            var lease = await _inner.AcquireAsync(cancellationToken).ConfigureAwait(false);
            if (acquisition == 1)
            {
                _firstLeaseHeld.TrySetResult();
                await _releaseFirst.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return new DelegatingLease(lease);
        }

        private sealed class DelegatingLease(IGuestDraftMutationLease innerLease) : IGuestDraftMutationLease
        {
            public ValueTask DisposeAsync() => innerLease.DisposeAsync();
        }
    }
}
