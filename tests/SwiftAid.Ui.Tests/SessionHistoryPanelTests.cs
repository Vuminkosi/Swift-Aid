using System.Reflection;
using SwiftAid.Contracts.Sessions;
using SwiftAid.UI.Components.Sessions;

namespace SwiftAid.Ui.Tests;

public sealed class SessionHistoryPanelTests
{
    [Fact]
    public async Task FailedNextPageClearsPreviouslyDisplayedHistory()
    {
        var id = Guid.NewGuid();
        var panel = new SessionHistoryPanel();
        SetParameter(panel, nameof(panel.SessionId), id);
        SetParameter(panel, nameof(panel.SessionVersion), 3L);
        SetParameter(panel, nameof(panel.LoadHistoryAsync), (Func<Guid, long?, Task<SessionHistoryPage?>>)((sessionId, before) =>
            Task.FromResult<SessionHistoryPage?>(before is null
                ? new SessionHistoryPage(sessionId, [Entry(3)], 3)
                : null)));
        Invoke(panel, "OnParametersSet");
        await (Task)Invoke(panel, "OpenAsync")!;
        Assert.Single(Field<List<SessionHistoryEntry>>(panel, "_items"));

        await (Task)Invoke(panel, "LoadMoreAsync")!;

        Assert.Empty(Field<List<SessionHistoryEntry>>(panel, "_items"));
        Assert.Contains("could not be loaded", Field<string>(panel, "_error"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResponseForAnotherSessionOrAfterSelectionChangeIsNotDisplayed()
    {
        var id = Guid.NewGuid();
        var panel = new SessionHistoryPanel();
        var pending = new TaskCompletionSource<SessionHistoryPage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetParameter(panel, nameof(panel.SessionId), id);
        SetParameter(panel, nameof(panel.SessionVersion), 3L);
        SetParameter(panel, nameof(panel.LoadHistoryAsync), (Func<Guid, long?, Task<SessionHistoryPage?>>)((_, _) => pending.Task));
        Invoke(panel, "OnParametersSet");
        var request = (Task)Invoke(panel, "OpenAsync")!;
        SetParameter(panel, nameof(panel.SessionId), Guid.NewGuid());
        Invoke(panel, "OnParametersSet");
        pending.SetResult(new SessionHistoryPage(id, [Entry(3)], null));
        await request;
        Assert.Empty(Field<List<SessionHistoryEntry>>(panel, "_items"));

        SetParameter(panel, nameof(panel.LoadHistoryAsync), (Func<Guid, long?, Task<SessionHistoryPage?>>)((_, _) =>
            Task.FromResult<SessionHistoryPage?>(new SessionHistoryPage(id, [Entry(3)], null))));
        await (Task)Invoke(panel, "OpenAsync")!;
        Assert.Empty(Field<List<SessionHistoryEntry>>(panel, "_items"));
        Assert.NotNull(Field<string?>(panel, "_error"));
    }

    private static SessionHistoryEntry Entry(long version) =>
        new(version, "officer", "reason", DateTimeOffset.UtcNow, "Reviewed", "Reviewed", null, null);

    private static void SetParameter(SessionHistoryPanel panel, string name, object value) =>
        typeof(SessionHistoryPanel).GetProperty(name)!.SetValue(panel, value);

    private static object? Invoke(SessionHistoryPanel panel, string name) =>
        typeof(SessionHistoryPanel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, null);

    private static T Field<T>(SessionHistoryPanel panel, string name) =>
        (T)typeof(SessionHistoryPanel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
}
