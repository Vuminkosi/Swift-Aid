using System.Reflection;
using SwiftAid.Client.Intake;
using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Geography;
using SwiftAid.Contracts.Intake;
using SwiftAid.UI.Components.Public;

namespace SwiftAid.Mobile.Razor.Tests;

public sealed class MobileDraftSnapshotTests
{
    private static readonly Type ComponentType = typeof(MobileDraftSnapshotTests).Assembly.GetTypes().Single(type => type.Name == "GuestReport");

    [Fact]
    public async Task IncompleteDraftCanBeSavedReopenedAndDiscardedToItsSavedValues()
    {
        var store = new GuestDraftStore(new MemoryStore());
        var original = IncompleteDraft();
        var first = NewHost(store);
        Set(first, "_reportDraft", original);
        var request = new PublicReportSaveRequest(null, Carrier(), original.ToFormSnapshot());

        await InvokeAsync(first, "SaveDraftAsync", request);

        var id = Assert.IsType<Guid>(request.PersistedDraftId);
        Assert.NotNull(await store.GetEditableDraftAsync(id));
        var reopened = NewHost(store);
        await InvokeAsync(reopened, "OpenDraftAsync", id);
        var restored = Get<PublicReportDraft>(reopened, "_reportDraft");
        Assert.Equal("SYNTHETIC-MOBILE-DRAFT", restored.Narrative);
        Assert.Equal("+02:", restored.OccurrenceOffset);
        restored.Narrative = "A later unsaved edit";

        await InvokeAsync(reopened, "DiscardCurrentUnsavedStateAsync");

        Assert.Equal("SYNTHETIC-MOBILE-DRAFT", Get<PublicReportDraft>(reopened, "_reportDraft").Narrative);
        Assert.Equal("+02:", Get<PublicReportDraft>(reopened, "_reportDraft").OccurrenceOffset);
    }

    [Fact]
    public async Task SaveBeforeLeavingPersistsIncompleteFormSnapshot()
    {
        var store = new GuestDraftStore(new MemoryStore());
        var host = NewHost(store);
        Set(host, "_reportDraft", IncompleteDraft());
        Set(host, "_storageAvailable", true);
        Set(host, "_showUnsavedExitPrompt", true);

        await InvokeAsync(host, "SaveDraftAndContinueAsync");

        var id = Assert.IsType<Guid>(Get<Guid?>(host, "_activeDraftId"));
        var saved = await store.GetEditableDraftAsync(id);
        Assert.NotNull(saved);
        Assert.Equal("+02:", PublicReportDraft.FromFormSnapshot(saved.FormSnapshot).OccurrenceOffset);
        Assert.False(Get<bool>(host, "_showUnsavedExitPrompt"));
    }

    [Fact]
    public async Task AddingRecordingKeepsNewerUnsavedFormEditsWhenAudioSaveFails()
    {
        var store = new GuestDraftStore(new MemoryStore());
        var savedDraft = IncompleteDraft();
        var initial = await store.CreateEditableDraftAsync(Carrier(), savedDraft.ToFormSnapshot());
        var editable = (await store.GetEditableDraftAsync(initial.ClientSubmissionId))!;
        var component = NewHost(store);
        var current = IncompleteDraft();
        current.Narrative = "Newer unsaved entry";
        Set(component, "_reportDraft", current);
        Set(component, "_activeDraftId", (Guid?)initial.ClientSubmissionId);
        Set(component, "_activeSubmission", editable.Submission);
        Set(component, "_activeFormSnapshot", editable.FormSnapshot);
        SetProperty(component, "AudioCapture", new FailingAudioCapture());

        await InvokeAsync(component, "SaveAudioToDraftAsync");

        Assert.Equal("Newer unsaved entry", Get<PublicReportDraft>(component, "_reportDraft").Narrative);
        var revisionId = Assert.IsType<Guid>(Get<Guid?>(component, "_activeDraftId"));
        var revision = (await store.GetEditableDraftAsync(revisionId))!;
        Assert.Equal("SYNTHETIC-MOBILE-DRAFT", PublicReportDraft.FromFormSnapshot(revision.FormSnapshot).Narrative);
        Assert.False(Get<bool>(component, "_voiceSaved"));
    }

    [Fact]
    public async Task SavingIncompleteEditsKeepsExistingAudioBinding()
    {
        var persistence = new MemoryStore();
        var store = new GuestDraftStore(persistence);
        var attachmentId = Guid.NewGuid();
        var initial = Carrier() with { OriginalAudioAttachmentIds = [attachmentId] };
        var created = await store.CreateDraftAsync(initial);
        var existing = (await persistence.GetAsync(created.ClientSubmissionId, default))!;
        await persistence.SaveAsync(existing with
        {
            AudioAssets = [new(attachmentId, new string('A', 64), "audio/wav", 6, 900, GuestAudioLocalState.LocalOnly)]
        }, default);
        var component = NewHost(store);
        var draft = IncompleteDraft();
        Set(component, "_reportDraft", draft);
        Set(component, "_activeDraftId", (Guid?)created.ClientSubmissionId);
        Set(component, "_activeSubmission", existing.Submission);
        var request = new PublicReportSaveRequest(created.ClientSubmissionId, Carrier(), draft.ToFormSnapshot());

        await InvokeAsync(component, "SaveDraftAsync", request);

        var saved = (await persistence.GetAsync(Assert.IsType<Guid>(request.PersistedDraftId), default))!;
        Assert.Contains(attachmentId, saved.Submission.OriginalAudioAttachmentIds);
        Assert.Contains(saved.AudioAssets!, asset => asset.AttachmentId == attachmentId);
        Assert.NotNull(saved.FormSnapshot);
        Assert.Equal("SYNTHETIC-MOBILE-DRAFT", Get<PublicReportDraft>(component, "_reportDraft").Narrative);
    }

    private static PublicReportDraft IncompleteDraft() => new()
    {
        SubjectScope = IntakeSubjectScope.Human,
        Narrative = "SYNTHETIC-MOBILE-DRAFT",
        OccurrencePrecision = ReportedTimePrecision.Exact,
        OccurrenceOffset = "+02:"
    };

    private static IntakeSubmission Carrier() => new PublicReportDraft { SubjectScope = IntakeSubjectScope.Human }
        .ToSubmission(null, null, null, [], false);

    private static object NewHost(IGuestSubmissionStore store)
    {
        var host = Activator.CreateInstance(ComponentType)!;
        ComponentType.GetProperty("SubmissionStore", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(host, store);
        return host;
    }

    private static void Set(object host, string name, object? value) =>
        ComponentType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, value);

    private static void SetProperty(object host, string name, object? value) =>
        ComponentType.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(host, value);

    private static T Get<T>(object host, string name) =>
        (T)ComponentType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;

    private static async Task InvokeAsync(object host, string name, params object?[] args) =>
        await (Task)ComponentType.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host, args)!;

    private sealed class MemoryStore : IGuestSubmissionPersistence
    {
        private readonly Dictionary<Guid, GuestSubmissionRecord> _data = [];
        public Task<GuestSubmissionRecord?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(_data.GetValueOrDefault(id));
        public Task<IReadOnlyList<GuestSubmissionRecord>> GetAllAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GuestSubmissionRecord>>(_data.Values.ToArray());
        public Task SaveAsync(GuestSubmissionRecord item, CancellationToken cancellationToken) { _data[item.ClientSubmissionId] = item; return Task.CompletedTask; }
        public Task SaveBatchAsync(IReadOnlyList<GuestSubmissionRecord> items, CancellationToken cancellationToken)
        {
            foreach (var item in items) _data[item.ClientSubmissionId] = item;
            return Task.CompletedTask;
        }
    }

    private sealed class FailingAudioCapture : IAudioCaptureService
    {
        public event EventHandler<AudioCaptureState>? StateChanged { add { } remove { } }
        public AudioCaptureState CurrentState { get; } = new(AudioCaptureStatus.ReadyToSave, TimeSpan.Zero);
        public Task<AudioCaptureState> StartAsync(CancellationToken cancellationToken = default) => Task.FromResult(CurrentState);
        public Task<AudioCaptureState> StopAsync(CancellationToken cancellationToken = default) => Task.FromResult(CurrentState);
        public Task<AudioDraftSaveResult> SaveToDraftAsync(Guid clientSubmissionId, Guid attachmentId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AudioDraftSaveResult(false, null, "Synthetic failure"));
        public Task<AudioCaptureState> RetryAsync(CancellationToken cancellationToken = default) => Task.FromResult(CurrentState);
        public Task<AudioCaptureState> RetryTemporaryCleanupAsync(CancellationToken cancellationToken = default) => Task.FromResult(CurrentState);
    }
}
