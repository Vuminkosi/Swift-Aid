using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using MudBlazor.Services;
using System.Reflection;
using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Sessions;
using SwiftAid.UI.Components.Sessions;

namespace SwiftAid.Ui.Tests;

public sealed class SessionConflictReviewTests
{
    [Fact]
    public async Task RetainedProposalBlocksNavigationUntilLeaveIsConfirmed()
    {
        var id = Guid.NewGuid();
        var input = new UpdateSessionInput("Proposal", "Summary", "Community", [], "Presenter", "en", DateTimeOffset.UtcNow.AddDays(1), 60, null, [new SessionMaterialInput("card", "1")], SessionVisibility.Public, SessionChannelType.None, null, [24]);
        var page = new SwiftAid.Gateway.Components.Pages.Sessions();
        var type = page.GetType();
        type.GetField("_deferredProposal", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, new SessionUpdateSubmission(id, 3, input, "reason"));
        var navigation = new TestNavigationManager();
        type.GetProperty("Navigation", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, navigation);
        using var registration = navigation.RegisterLocationChangingHandler(context =>
        {
            type.GetMethod("ConfirmRetainedLeave", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, [context]);
            return ValueTask.CompletedTask;
        });

        Assert.False(await navigation.CanNavigateAsync("http://localhost/other"));
        Assert.True((bool)type.GetField("_confirmingRetainedLeave", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!);
        type.GetMethod("StayWithRetainedProposal", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, null);
        Assert.False(await navigation.CanNavigateAsync("http://localhost/other"));
        type.GetMethod("LeaveWithRetainedProposal", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, null);
        Assert.True(await navigation.CanNavigateAsync("http://localhost/other"));
        Assert.False(await navigation.CanNavigateAsync("http://localhost/another"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnconfirmedProposalIsRestoredOnlyForTheSameActorAfterReadFailure(bool sameActor)
    {
        var id = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow.AddDays(1);
        var current = new CommunitySessionView(id, 3, "Current", "Summary", "Community", [], "SZ", "region", "education", "Presenter", "en", start, "UTC", 60, null, [new StoredSessionMaterial("card", "1")], SessionVisibility.Public, SessionChannelType.None, null, [24], "Scheduled", "creator", "reviewer", false, 1, null);
        var input = new UpdateSessionInput("Proposal", "Summary", "Community", [], "Presenter", "en", start, 60, null, [new SessionMaterialInput("card", "1")], SessionVisibility.Public, SessionChannelType.None, null, [24]);
        var proposal = new SessionUpdateSubmission(id, 3, input, "reason");
        var page = new SwiftAid.Gateway.Components.Pages.Sessions();
        var type = page.GetType();
        var client = new StaffClient(current, true) { ReadUnavailable = true };
        type.GetProperty("StaffSessionEditor", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, client);
        type.GetField("_actorId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, "officer");

        await (Task)type.GetMethod("RecoverConflictAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, [proposal, "officer", true])!;
        Assert.Same(proposal, type.GetField("_deferredProposal", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page));
        Assert.Null(type.GetField("_conflictProposal", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page));

        client.ReadUnavailable = false;
        client.ActorId = sameActor ? "officer" : "another-officer";
        await (Task)type.GetMethod("RetryDeferredProposalAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, null)!;
        var restored = type.GetField("_conflictProposal", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page);
        if (sameActor) Assert.Same(proposal, restored);
        else Assert.Null(restored);
        Assert.Null(type.GetField("_deferredProposal", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page));
    }

    [Fact]
    public async Task AnotherUpdateCannotReplaceAnUnconfirmedProposal()
    {
        var id = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow.AddDays(1);
        var input = new UpdateSessionInput("Proposal", "Summary", "Community", [], "Presenter", "en", start, 60, null, [new SessionMaterialInput("card", "1")], SessionVisibility.Public, SessionChannelType.None, null, [24]);
        var proposal = new SessionUpdateSubmission(id, 3, input, "reason");
        var page = new SwiftAid.Gateway.Components.Pages.Sessions();
        var type = page.GetType();
        type.GetField("_deferredProposal", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, proposal);

        await (Task)type.GetMethod("RunCommandAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(page, [input.ToCommand(Guid.NewGuid(), 3, "reason", DateTimeOffset.UtcNow), proposal])!;

        Assert.Same(proposal, type.GetField("_deferredProposal", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page));
        Assert.Contains("unconfirmed update", (string)type.GetField("_serverError", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnconfirmedUpdateRechecksAccessAndKeepsSubmittedProposal()
    {
        var id = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow.AddDays(1);
        var current = new CommunitySessionView(id, 3, "Current", "Summary", "Community", [], "SZ", "region", "education", "Presenter", "en", start, "UTC", 60, null, [new StoredSessionMaterial("card", "1")], SessionVisibility.Public, SessionChannelType.None, null, [24], "Scheduled", "creator", "reviewer", false, 1, null);
        var input = new UpdateSessionInput("Proposal", "Summary", "Community", [], "Presenter", "en", start, 60, null, [new SessionMaterialInput("card", "1")], SessionVisibility.Public, SessionChannelType.None, null, [24]);
        var proposal = new SessionUpdateSubmission(id, 3, input, "reason");
        var page = new SwiftAid.Gateway.Components.Pages.Sessions();
        var type = page.GetType();
        type.GetProperty("StaffSessionEditor", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, new StaffClient(current, true));
        type.GetField("_actorId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, "officer");
        type.GetField("_editingSessionId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, id);
        type.GetField("_editingVersion", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, 3L);
        type.GetField("_editingStatus", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, "Scheduled");

        await (Task)type.GetMethod("RunCommandAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(page, [input.ToCommand(id, 3, "reason", DateTimeOffset.UtcNow), proposal])!;

        Assert.Same(proposal, type.GetField("_conflictProposal", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page));
        Assert.True((bool)type.GetField("_proposalOutcomeUnknown", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!);
    }

    [Fact]
    public async Task ReopeningSameSessionReplacesTheEditorEvenWhenVersionIsUnchanged()
    {
        var id = Guid.NewGuid();
        var current = new CommunitySessionView(id, 4, "Current", "Summary", "Community", [], "SZ", "region", "education", "Presenter", "en", DateTimeOffset.UtcNow.AddDays(1), "UTC", 60, null, [new StoredSessionMaterial("card", "1")], SessionVisibility.Public, SessionChannelType.None, null, [24], "Scheduled", "creator", "reviewer", false, 1, null);
        var page = new SwiftAid.Gateway.Components.Pages.Sessions();
        var type = page.GetType();
        type.GetProperty("StaffSessionEditor", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, new StaffClient(current, true));
        type.GetField("_actorId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, "officer");
        type.GetField("_editingSessionId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, id);
        type.GetField("_editingVersion", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, 4L);
        var generation = type.GetField("_editorGeneration", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var before = (long)generation.GetValue(page)!;

        await (Task)type.GetMethod("ManageSessionAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, [id])!;

        Assert.True((long)generation.GetValue(page)! > before);
        Assert.Equal(4L, type.GetField("_editingVersion", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConflictProposalIsRestoredOnlyAfterCurrentSessionAccessIsConfirmed(bool canEdit)
    {
        var id = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero);
        var current = new CommunitySessionView(id, 4, "Current", "Summary", "Community", [], "SZ", "region", "education", "Presenter", "en", start, "UTC", 60, null, [new StoredSessionMaterial("card", "1")], SessionVisibility.Public, SessionChannelType.None, null, [24], "Scheduled", "creator", "reviewer", false, 1, null);
        var input = new UpdateSessionInput("Proposal", "Summary", "Community", [], "Presenter", "en", start, 60, null, [new SessionMaterialInput("card", "1")], SessionVisibility.Public, SessionChannelType.None, null, [24]);
        var proposal = new SessionUpdateSubmission(id, 3, input, "reason");
        var page = new SwiftAid.Gateway.Components.Pages.Sessions();
        var type = page.GetType();
        type.GetProperty("StaffSessionEditor", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, new StaffClient(current, canEdit));
        type.GetField("_actorId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, "officer");
        var task = (Task)type.GetMethod("RecoverConflictAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, [proposal, "officer", false])!;
        await task;
        var retained = type.GetField("_conflictProposal", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page);
        Assert.Equal(canEdit, retained is not null);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnknownOutcomeRetainsProposalAtSameVersionOnlyWithCurrentEditAccess(bool canEdit)
    {
        var id = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow.AddDays(1);
        var current = new CommunitySessionView(id, 3, "Current", "Summary", "Community", [], "SZ", "region", "education", "Presenter", "en", start, "UTC", 60, null, [new StoredSessionMaterial("card", "1")], SessionVisibility.Public, SessionChannelType.None, null, [24], "Scheduled", "creator", "reviewer", false, 1, null);
        var input = new UpdateSessionInput("Proposal", "Summary", "Community", [], "Presenter", "en", start, 60, null, [new SessionMaterialInput("card", "1")], SessionVisibility.Public, SessionChannelType.None, null, [24]);
        var proposal = new SessionUpdateSubmission(id, 3, input, "reason");
        var page = new SwiftAid.Gateway.Components.Pages.Sessions();
        var type = page.GetType();
        type.GetProperty("StaffSessionEditor", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, new StaffClient(current, canEdit));
        type.GetField("_actorId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, "officer");

        await (Task)type.GetMethod("RecoverConflictAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, [proposal, "officer", true])!;

        Assert.Equal(canEdit, type.GetField("_conflictProposal", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page) is not null);
        if (canEdit) Assert.True((bool)type.GetField("_proposalOutcomeUnknown", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisplaysBothVersionsAndEscapesUserContent(bool outcomeUnknown)
    {
        var id = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero);
        var current = new CommunitySessionView(id, 4, "Current", "Current summary", "Community", [], "SZ", "region", "education", "Presenter", "en", start, "UTC", 60, null, [new StoredSessionMaterial("card", "1")], SessionVisibility.Public, SessionChannelType.None, null, [24], "Scheduled", "creator", "reviewer", false, 1, null);
        var input = new UpdateSessionInput("<Proposal>", "Proposed summary", "Community", [], "Presenter", "en", start.AddDays(1), 60, null, [new SessionMaterialInput("card", "1")], SessionVisibility.Public, SessionChannelType.None, null, [24]);
        var proposal = new SessionUpdateSubmission(id, 3, input, "Schedule changed");
        var registrations = new ServiceCollection();
        registrations.AddLogging();
        registrations.AddMudServices();
        registrations.AddSingleton<IJSRuntime>(new NoOpJsRuntime());
        registrations.AddSingleton<NavigationManager>(new TestNavigationManager());
        await using var services = registrations.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var markup = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<SessionConflictReview>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(SessionConflictReview.Proposal)] = proposal, [nameof(SessionConflictReview.Current)] = current, [nameof(SessionConflictReview.OutcomeUnknown)] = outcomeUnknown }));
            return component.ToHtmlString();
        });

        Assert.Contains(outcomeUnknown ? "outcome could not be confirmed" : "Your update was not saved", markup, StringComparison.Ordinal);
        Assert.Contains("&lt;Proposal&gt;", markup, StringComparison.Ordinal);
        Assert.Contains("Current summary", markup, StringComparison.Ordinal);
        Assert.Contains("Schedule changed", markup, StringComparison.Ordinal);
        Assert.Contains("Current version: 4", markup, StringComparison.Ordinal);
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("http://localhost/", "http://localhost/");
        public ValueTask<bool> CanNavigateAsync(string target) => NotifyLocationChangingAsync(target, null, false);
        protected override void SetNavigationLockState(bool value) { }
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }

    private sealed class StaffClient(CommunitySessionView current, bool canEdit) : IStaffSessionEditorClient
    {
        public bool ReadUnavailable { get; set; }
        public string ActorId { get; set; } = "officer";
        private static UiCapabilityResult<T> Success<T>(T value) => new(UiCapabilityResultCode.Success, value,
            new UiCapabilityMetadata("editor", "1", UiCapabilityProvenance.LiveService, DateTimeOffset.UtcNow, true));

        public Task<UiCapabilityResult<string>> GetCurrentActorIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(Success(ActorId));
        public Task<UiCapabilityResult<CommunitySessionView>> GetStaffAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(ReadUnavailable ? UiCapabilityResults.Unavailable<CommunitySessionView>("editor") : Success(current));
        public Task<UiCapabilityResult<StaffSessionManagementCapabilities>> GetManagementCapabilitiesAsync(Guid? sessionId, long? expectedVersion, CancellationToken cancellationToken = default) =>
            Task.FromResult(Success(new StaffSessionManagementCapabilities(false, canEdit, false, false, false, false, current.SessionId, current.Version, current.Status, true)));
        public Task<UiCapabilityResult<SessionCommandResult>> ExecuteAsync(SessionCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class NoOpJsRuntime : JSRuntime
    {
        protected override void BeginInvokeJS(long taskId, string identifier, string? argsJson, JSCallResultType resultType, long targetInstanceId) { }
        protected override void EndInvokeDotNet(DotNetInvocationInfo callInfo, in DotNetInvocationResult result) { }
    }
}
