using System.Reflection;
using SwiftAid.UI.Components.Sessions;

namespace SwiftAid.Ui.Tests;

public sealed class SessionEditorTimeTests
{
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
