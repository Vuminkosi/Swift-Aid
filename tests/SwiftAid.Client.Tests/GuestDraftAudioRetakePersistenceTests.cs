using System.Security.Cryptography;
using System.Text.Json;
using SwiftAid.Client.Intake;
using SwiftAid.Contracts.Geography;
using SwiftAid.Contracts.Intake;
using SwiftAid.Mobile.Services.Intake;

namespace SwiftAid.Client.Tests;

public sealed class GuestDraftAudioRetakePersistenceTests
{
    [Fact]
    public async Task EditableRevisionCanAddAudioWithoutDroppingExistingAttachment()
    {
        using var root = new TemporaryRoot();
        var keys = new TestInstallKeyStore();
        var persistence = new EncryptedGuestSubmissionStore(root.Path, keys);
        var audio = new EncryptedGuestAudioStore(root.Path, keys, persistence);
        var drafts = new GuestDraftStore(persistence, audioStore: audio);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var original = CreateSubmission(first);
        var created = await drafts.CreateDraftAsync(original);
        await audio.SaveOriginalAsync(created.ClientSubmissionId, first, "audio/wav", 900,
            new MemoryStream([0x52, 0x49, 0x46, 0x46, 0x01, 0x02]));
        var snapshot = new GuestDraftFormSnapshot(1, "{\"occurrenceTime\":\"unfinished\"}");

        var revised = await drafts.CreateEditableRevisionAsync(created.ClientSubmissionId,
            original with { OriginalAudioAttachmentIds = [second] }, snapshot);
        var record = (await persistence.GetAsync(revised.ClientSubmissionId, default))!;
        Assert.Equal(new[] { first, second }, record.Submission.OriginalAudioAttachmentIds);
        Assert.Contains(record.AudioAssets!, item => item.AttachmentId == first);
        Assert.Equal(snapshot, (await drafts.GetEditableDraftAsync(revised.ClientSubmissionId))!.FormSnapshot);
    }

    [Fact]
    public async Task EditableRevisionAndCompletionPreserveExistingOriginalAudio()
    {
        using var root = new TemporaryRoot();
        var keys = new TestInstallKeyStore();
        var persistence = new EncryptedGuestSubmissionStore(root.Path, keys);
        var audio = new EncryptedGuestAudioStore(root.Path, keys, persistence);
        var drafts = new GuestDraftStore(persistence, audioStore: audio);
        var attachmentId = Guid.NewGuid();
        var initial = CreateSubmission(attachmentId);
        var created = await drafts.CreateDraftAsync(initial);
        byte[] original = [0x52, 0x49, 0x46, 0x46, 0x01, 0x02];
        await audio.SaveOriginalAsync(created.ClientSubmissionId, attachmentId, "audio/wav", 900, new MemoryStream(original));
        var carrier = initial with { OriginalAudioAttachmentIds = [] };
        var snapshot = new GuestDraftFormSnapshot(1, "{\"occurrenceTime\":\"unfinished\"}");

        var editable = await drafts.CreateEditableRevisionAsync(created.ClientSubmissionId, carrier, snapshot);
        var editableRecord = (await persistence.GetAsync(editable.ClientSubmissionId, default))!;
        Assert.Contains(attachmentId, editableRecord.Submission.OriginalAudioAttachmentIds);
        Assert.Contains(editableRecord.AudioAssets!, item => item.AttachmentId == attachmentId);
        Assert.Equal(original, await audio.ReadOriginalAsync(editable.ClientSubmissionId, attachmentId));
        Assert.Equal(snapshot, (await drafts.GetEditableDraftAsync(editable.ClientSubmissionId))!.FormSnapshot);

        var completed = await drafts.CompleteEditableDraftAsync(editable.ClientSubmissionId, carrier);
        var completedRecord = (await persistence.GetAsync(completed.ClientSubmissionId, default))!;
        Assert.Contains(attachmentId, completedRecord.Submission.OriginalAudioAttachmentIds);
        Assert.Contains(completedRecord.AudioAssets!, item => item.AttachmentId == attachmentId);
        Assert.Equal(original, await audio.ReadOriginalAsync(completed.ClientSubmissionId, attachmentId));
    }

    [Fact]
    public async Task RetakePersistsSupersededAndReplacementRevisionsWithCorrectPayloadHashes()
    {
        using var root = new TemporaryRoot();
        var keys = new TestInstallKeyStore();
        var persistence = new EncryptedGuestSubmissionStore(root.Path, keys);
        var journal = new EncryptedGuestAudioCleanupJournal(root.Path, keys);
        var audio = new EncryptedGuestAudioStore(root.Path, keys, persistence);
        var drafts = new GuestDraftStore(persistence, cleanupJournal: journal, audioStore: audio);
        var attachmentId = Guid.NewGuid();
        var created = await drafts.CreateDraftAsync(CreateSubmission(attachmentId));
        await audio.SaveOriginalAsync(created.ClientSubmissionId, attachmentId, "audio/wav", 900,
            new MemoryStream([0x52, 0x49, 0x46, 0x46, 0x01, 0x02]));
        var before = Assert.IsType<GuestSubmissionRecord>(
            await persistence.GetAsync(created.ClientSubmissionId, CancellationToken.None));
        var snapshot = new GuestDraftFormSnapshot(1, "{\"occurrenceTime\":\"unfinished\"}");
        await persistence.SaveAsync(before with { FormSnapshot = snapshot, Version = before.Version + 1 }, default);
        before = Assert.IsType<GuestSubmissionRecord>(await persistence.GetAsync(created.ClientSubmissionId, default));

        var retake = await drafts.RetakeAudioAsync(
            created.ClientSubmissionId, attachmentId, before.Version);

        Assert.Equal(GuestAudioRetakeStatus.Replaced, retake.Status);
        Assert.NotNull(retake.ReplacementSubmissionId);
        Assert.NotEqual(created.ClientSubmissionId, retake.ReplacementSubmissionId);
        Assert.Equal(attachmentId, retake.AttachmentId);
        Assert.Equal(GuestSubmissionLocalState.Superseded,
            (await persistence.GetAsync(created.ClientSubmissionId, CancellationToken.None))?.State);

        var oldAfter = Assert.IsType<GuestSubmissionRecord>(
            await persistence.GetAsync(created.ClientSubmissionId, CancellationToken.None));
        var replacement = Assert.IsType<GuestSubmissionRecord>(
            await persistence.GetAsync(retake.ReplacementSubmissionId!.Value, CancellationToken.None));
        Assert.Equal(before.Version + 1, oldAfter.Version);
        Assert.Equal(GuestSubmissionLocalState.Superseded, oldAfter.State);
        Assert.Contains(attachmentId, oldAfter.Submission.OriginalAudioAttachmentIds);
        Assert.Contains(oldAfter.AudioAssets!, asset => asset.AttachmentId == attachmentId);
        Assert.Equal(1, replacement.Version);
        Assert.Equal(GuestSubmissionLocalState.Draft, replacement.State);
        Assert.Equal(snapshot, replacement.FormSnapshot);
        Assert.DoesNotContain(attachmentId, replacement.Submission.OriginalAudioAttachmentIds);
        Assert.Empty(replacement.AudioAssets!);
        Assert.Equal(HashSubmission(oldAfter.Submission), oldAfter.PayloadSha256);
        Assert.Equal(HashSubmission(replacement.Submission), replacement.PayloadSha256);
        Assert.NotEqual(oldAfter.PayloadSha256, replacement.PayloadSha256);

        var restartedPersistence = new EncryptedGuestSubmissionStore(root.Path, keys);
        var restoredOld = Assert.IsType<GuestSubmissionRecord>(
            await restartedPersistence.GetAsync(oldAfter.ClientSubmissionId, CancellationToken.None));
        var restoredReplacement = Assert.IsType<GuestSubmissionRecord>(
            await restartedPersistence.GetAsync(replacement.ClientSubmissionId, CancellationToken.None));
        Assert.Equal(oldAfter.Version, restoredOld.Version);
        Assert.Equal(replacement.Version, restoredReplacement.Version);
        Assert.Equal(snapshot, restoredReplacement.FormSnapshot);
        Assert.Equal(oldAfter.Submission.OriginalAudioAttachmentIds, restoredOld.Submission.OriginalAudioAttachmentIds);
        Assert.Empty(restoredReplacement.Submission.OriginalAudioAttachmentIds);
        Assert.Empty(await new EncryptedGuestAudioCleanupJournal(root.Path, keys).GetPendingAsync());
    }

    private static string HashSubmission(IntakeSubmission submission)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(submission, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        try
        {
            return Convert.ToHexString(SHA256.HashData(payload));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static IntakeSubmission CreateSubmission(Guid attachmentId) => new(
        1,
        Guid.NewGuid(),
        IntakeSubjectScope.Human,
        "A sample report without clinical advice.",
        ["Observed activity"],
        [attachmentId],
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
            System.IO.Path.GetTempPath(), $"swiftaid-retake-tests-{Guid.NewGuid():N}");

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
