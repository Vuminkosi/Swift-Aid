using System.Reflection;
using SwiftAid.Contracts.Sessions;
using SwiftAid.UI.Components.Sessions;

namespace SwiftAid.Ui.Tests;

public sealed class SessionEditorTimeTests
{
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
}
