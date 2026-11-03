using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using SwiftAid.Contracts.Sessions;
using SwiftAid.UI.Components.Sessions;

namespace SwiftAid.Ui.Tests;

public sealed class SessionEditorTimeTests
{
    [Fact]
    public async Task NavigationIsStoppedWhileDraftHasUnsavedInput()
    {
        var editor = new SessionEditor();
        Invoke(editor, "OnParametersSet");
        Set(editor, "_title", "Draft in progress");
        var navigation = new TestNavigationManager();
        using var registration = navigation.RegisterLocationChangingHandler(context =>
        {
            typeof(SessionEditor).GetMethod("ConfirmLeaveAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(editor, [context]);
            return ValueTask.CompletedTask;
        });

        Assert.False(await navigation.CanNavigateAsync("https://example.test/other"));
        Assert.True((bool)typeof(SessionEditor).GetField("_confirmingLeave", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(editor)!);
        Invoke(editor, "StayOnEditor");
        Assert.False((bool)typeof(SessionEditor).GetField("_confirmingLeave", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(editor)!);
        Assert.False(await navigation.CanNavigateAsync("https://example.test/other"));
        typeof(SessionEditor).GetProperty("Navigation", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(editor, navigation);
        Invoke(editor, "LeaveEditor");
        Assert.Equal("https://example.test/other", navigation.RequestedUri);
        Assert.True(await navigation.CanNavigateAsync("https://example.test/other"));
        Assert.False(await navigation.CanNavigateAsync("https://example.test/another"));
    }

    [Fact]
    public void NewSessionTracksUnsavedDraftInput()
    {
        var editor = new SessionEditor();
        Invoke(editor, "OnParametersSet");
        var pending = typeof(SessionEditor).GetProperty("HasPendingInput", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.False((bool)pending.GetValue(editor)!);

        Set(editor, "_title", "Draft in progress");
        Assert.True((bool)pending.GetValue(editor)!);
        Set(editor, "_title", string.Empty);
        Set(editor, "_reason", "Planning notes");
        Assert.True((bool)pending.GetValue(editor)!);
    }

    [Fact]
    public async Task CancellationRequiresItsReasonsButNotEditableSessionFields()
    {
        var id = Guid.NewGuid();
        var editor = new SessionEditor();
        typeof(SessionEditor).GetProperty(nameof(SessionEditor.SessionId))!.SetValue(editor, id);
        typeof(SessionEditor).GetProperty(nameof(SessionEditor.ExpectedVersion))!.SetValue(editor, 3L);
        typeof(SessionEditor).GetProperty(nameof(SessionEditor.Status))!.SetValue(editor, "Scheduled");
        typeof(SessionEditor).GetProperty(nameof(SessionEditor.ManagementCapabilities))!.SetValue(editor,
            new SessionManagementPresentation(true, true, id, 3, "Scheduled", false, false, false, false, true, false));
        SessionCancelSubmission? submission = null;
        typeof(SessionEditor).GetProperty(nameof(SessionEditor.OnCancel))!.SetValue(editor,
            EventCallback.Factory.Create<SessionCancelSubmission>(new object(), value => submission = value));
        Set(editor, "_timeZone", "Unavailable/Zone");

        await (Task)Invoke(editor, "SubmitCancelAsync")!;
        Assert.Null(submission);
        Set(editor, "_reason", "  schedule changed  ");
        Set(editor, "_cancellationReason", "  venue unavailable  ");
        await (Task)Invoke(editor, "SubmitCancelAsync")!;

        Assert.NotNull(submission);
        Assert.Equal("venue unavailable", submission.CancellationReason);
        Assert.Equal("schedule changed", submission.Reason);
    }

    [Fact]
    public async Task UnsavedEditsCannotBeReviewedOrPublished()
    {
        var id = Guid.NewGuid();
        var saved = new CommunitySessionView(id, 1, "Title", "Summary", "Community", ["topic"],
            "SZ", "region", "education", "Presenter", "en", DateTimeOffset.UtcNow.AddDays(2),
            "UTC", 60, null, [new StoredSessionMaterial("material", "1")], SessionVisibility.Public,
            SessionChannelType.None, null, [24], "Draft", "creator", null, false, 0, null);
        var editor = new SessionEditor();
        typeof(SessionEditor).GetProperty(nameof(SessionEditor.SessionId))!.SetValue(editor, id);
        typeof(SessionEditor).GetProperty(nameof(SessionEditor.ExpectedVersion))!.SetValue(editor, 1L);
        typeof(SessionEditor).GetProperty(nameof(SessionEditor.Status))!.SetValue(editor, "Draft");
        typeof(SessionEditor).GetProperty(nameof(SessionEditor.ManagementCapabilities))!.SetValue(editor,
            new SessionManagementPresentation(true, true, id, 1, "Draft", false, true, true, true, false, false));
        typeof(SessionEditor).GetMethod("Prefill", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(editor, [saved]);
        var invoked = false;
        typeof(SessionEditor).GetProperty(nameof(SessionEditor.OnReview))!.SetValue(editor,
            EventCallback.Factory.Create<SessionReviewSubmission>(editor, _ => invoked = true));
        typeof(SessionEditor).GetProperty(nameof(SessionEditor.OnSchedule))!.SetValue(editor,
            EventCallback.Factory.Create<string>(editor, _ => invoked = true));
        Set(editor, "_title", "Unsaved title");

        Assert.True((bool)typeof(SessionEditor).GetProperty("HasUnsavedChanges", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(editor)!);
        await (Task)Invoke(editor, "SubmitReviewAsync")!;
        await (Task)Invoke(editor, "SubmitScheduleAsync")!;
        Assert.False(invoked);
        Set(editor, "_title", "Title");
        Assert.False((bool)typeof(SessionEditor).GetProperty("HasUnsavedChanges", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(editor)!);
    }

    [Fact]
    public void UnknownTimeZoneDoesNotDisplayUtcAsLocalTime()
    {
        var method = typeof(SessionEditor).GetMethod("ToWallClock", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Null(method.Invoke(null, [DateTimeOffset.UtcNow, "Unavailable/Zone"]));
    }

    [Fact]
    public void ReschedulingPreservesTheExactPublishedContent()
    {
        var id = Guid.NewGuid();
        var published = new CommunitySessionView(id, 3, " Published title ", "Summary", "Community", ["topic"],
            "SZ", "region", "education", "Presenter", "en", new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero),
            "America/New_York", 60, null, [new StoredSessionMaterial("material", "1")], SessionVisibility.Public,
            SessionChannelType.None, null, [24], "Scheduled", "creator", "reviewer", false, 0, null);
        var editor = new SessionEditor();
        typeof(SessionEditor).GetProperty(nameof(SessionEditor.SessionId))!.SetValue(editor, id);
        typeof(SessionEditor).GetProperty(nameof(SessionEditor.ExpectedVersion))!.SetValue(editor, 3L);
        typeof(SessionEditor).GetProperty(nameof(SessionEditor.Status))!.SetValue(editor, "Scheduled");
        typeof(SessionEditor).GetProperty(nameof(SessionEditor.InitialSession))!.SetValue(editor, published);
        typeof(SessionEditor).GetMethod("Prefill", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(editor, [published]);
        Set(editor, "_startLocal", new DateTime(2026, 11, 2, 1, 30, 0));
        Set(editor, "_title", "A changed form value");

        var update = Assert.IsType<UpdateSessionInput>(Invoke(editor, "BuildUpdateInput"));
        Assert.Equal(published.Title, update.Title);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 6, 30, 0, TimeSpan.Zero), update.StartUtc);
        Assert.Equal(published.Materials[0].MaterialId, Assert.Single(update.Materials).MaterialId);
    }

    [Theory]
    [InlineData(-240, 5)]
    [InlineData(-300, 6)]
    public void RepeatedLocalTimeRequiresAnOffsetAndPreservesTheSelectedInstant(int offsetMinutes, int expectedHour)
    {
        var editor = CreateEditor(new DateTime(2026, 11, 1, 1, 30, 0));
        var error = Assert.Throws<TargetInvocationException>(() => Invoke(editor, "ToStartUtc"));
        Assert.IsType<InvalidOperationException>(error.InnerException);

        Set(editor, "_ambiguousOffsetMinutes", offsetMinutes);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, expectedHour, 30, 0, TimeSpan.Zero), Invoke(editor, "ToStartUtc"));

        Invoke(editor, "ClearAmbiguousOffset");
        Assert.Throws<TargetInvocationException>(() => Invoke(editor, "ToStartUtc"));
    }

    [Theory]
    [InlineData(5, -240)]
    [InlineData(6, -300)]
    public void ExistingSessionOffsetIsRecoveredFromItsStoredInstant(int hour, int expectedOffset)
    {
        var instant = new DateTimeOffset(2026, 11, 1, hour, 30, 0, TimeSpan.Zero);
        var method = typeof(SessionEditor).GetMethod("GetStoredAmbiguousOffset", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(expectedOffset, method.Invoke(null, [instant, "America/New_York"]));
    }

    [Fact]
    public void NonexistentLocalTimeIsRejected()
    {
        var editor = CreateEditor(new DateTime(2027, 3, 14, 2, 30, 0));
        var error = Assert.Throws<TargetInvocationException>(() => Invoke(editor, "ToStartUtc"));
        Assert.IsType<ArgumentException>(error.InnerException);
    }

    private static SessionEditor CreateEditor(DateTime local)
    {
        var editor = new SessionEditor();
        Set(editor, "_timeZone", "America/New_York");
        Set(editor, "_startLocal", local);
        return editor;
    }

    private static void Set(SessionEditor editor, string name, object value) =>
        typeof(SessionEditor).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(editor, value);

    private static object? Invoke(SessionEditor editor, string name) =>
        typeof(SessionEditor).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(editor, null);

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("https://example.test/", "https://example.test/sessions");
        public string? RequestedUri { get; private set; }

        public ValueTask<bool> CanNavigateAsync(string target) => NotifyLocationChangingAsync(target, null, false);

        protected override void SetNavigationLockState(bool value) { }

        protected override void NavigateToCore(string uri, bool forceLoad) => RequestedUri = uri;
    }
}
