using System.Reflection;
using Microsoft.AspNetCore.Components;
using SwiftAid.Client.Intake;
using SwiftAid.Contracts.Intake;
using SwiftAid.UI.Components.Public;
using SwiftAid.UI.Components.Surfaces.Public;

namespace SwiftAid.Browser.Razor.Tests;

public sealed class BrowserDraftAudioTests
{
    private static readonly Type ComponentType = typeof(BrowserDraftAudioTests).Assembly.GetTypes().Single(type => type.Name == "BrowserGuestRoot");

    [Fact]
    public async Task TextSaveKeepsAudioAndHistoryExplainsWhySendingIsUnavailable()
    {
        var persistence = new MemoryStore();
        var store = new GuestDraftStore(persistence);
        var attachmentId = Guid.NewGuid();
        var submission = Carrier() with { OriginalAudioAttachmentIds = [attachmentId] };
        var draft = await store.CreateDraftAsync(submission);
        var existing = (await persistence.GetAsync(draft.ClientSubmissionId, default))!;
        await persistence.SaveAsync(existing with
        {
            AudioAssets = [new(attachmentId, new string('A', 64), "audio/wav", 6, 900, GuestAudioLocalState.LocalOnly)]
        }, default);
        var host = NewHost(store);
        await InvokeAsync(host, "OpenDraftAsync", draft.ClientSubmissionId);
        var request = new PublicReportSaveRequest(draft.ClientSubmissionId, Carrier());

        await InvokeAsync(host, "SaveDraftAsync", request);

        var saved = (await persistence.GetAsync(Assert.IsType<Guid>(request.PersistedDraftId), default))!;
        Assert.Contains(attachmentId, saved.Submission.OriginalAudioAttachmentIds);
        Assert.Contains(saved.AudioAssets!, item => item.AttachmentId == attachmentId);
        Assert.True(Get<bool>(host, "_activeHasLocalAudio"));
        var history = Get<IReadOnlyList<PublicReportHistorySurface.HistoryItem>>(host, "_history");
        var item = Assert.Single(history);
        Assert.False(item.CanSend);
        Assert.Contains("Voice upload is not available", item.SendUnavailableMessage);
    }

    [Fact]
    public async Task StaleBrowserViewRechecksAudioBeforeCreatingSubmissionRevision()
    {
        var persistence = new MemoryStore();
        var store = new GuestDraftStore(persistence);
        var host = NewHost(store);
        var draft = await store.CreateDraftAsync(Carrier());
        await InvokeAsync(host, "OpenDraftAsync", draft.ClientSubmissionId);
        var attachmentId = Guid.NewGuid();
        var existing = (await persistence.GetAsync(draft.ClientSubmissionId, default))!;
        await persistence.SaveAsync(existing with
        {
            Submission = existing.Submission with { OriginalAudioAttachmentIds = [attachmentId] },
            AudioAssets = [new(attachmentId, new string('A', 64), "audio/wav", 6, 900, GuestAudioLocalState.LocalOnly)]
        }, default);

        await InvokeAsync(host, "SendReportAsync", Carrier());

        Assert.Equal(draft.ClientSubmissionId, Get<Guid?>(host, "_activeDraftId"));
        Assert.True(Get<bool>(host, "_activeHasLocalAudio"));
        Assert.Contains("Voice upload is not available", Get<string>(host, "_feedback"));
        Assert.Single(await persistence.GetAllAsync(default));
    }

    private static IntakeSubmission Carrier() => new PublicReportDraft { SubjectScope = IntakeSubjectScope.Human, Narrative = "Synthetic" }
        .ToSubmission(null, null, null, [], false);

    private static object NewHost(IGuestSubmissionStore store)
    {
        var host = Activator.CreateInstance(ComponentType)!;
        SetProperty(host, "SubmissionStore", store);
        SetProperty(host, "Navigation", new TestNavigation());
        Set(host, "_storageAvailable", true);
        Set(host, "_countryCatalogueIsCurrent", true);
        Set(host, "_privacyNoticeIsCurrent", true);
        Set(host, "_processingPolicyReference", "/policy");
        Set(host, "_processingPolicyVersion", "v1");
        Set(host, "_processingPolicyIsLive", true);
        return host;
    }

    private static void SetProperty(object host, string name, object value) => ComponentType.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(host, value);
    private static void Set(object host, string name, object? value) => ComponentType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, value);
    private static T Get<T>(object host, string name) => (T)ComponentType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
    private static async Task InvokeAsync(object host, string name, params object?[] args) => await (Task)ComponentType.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host, args)!;

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("https://example.test/", "https://example.test/report");
        protected override void NavigateToCore(string uri, bool forceLoad) => Uri = ToAbsoluteUri(uri).AbsoluteUri;
        protected override void NavigateToCore(string uri, NavigationOptions options) => NavigateToCore(uri, options.ForceLoad);
    }

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
}
