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
    }

    private sealed class StaffClient(CommunitySessionView current, bool canEdit) : IStaffSessionEditorClient
    {
        private static UiCapabilityResult<T> Success<T>(T value) => new(UiCapabilityResultCode.Success, value,
            new UiCapabilityMetadata("editor", "1", UiCapabilityProvenance.LiveService, DateTimeOffset.UtcNow, true));

        public Task<UiCapabilityResult<string>> GetCurrentActorIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(Success("officer"));
        public Task<UiCapabilityResult<CommunitySessionView>> GetStaffAsync(Guid sessionId, CancellationToken cancellationToken = default) => Task.FromResult(Success(current));
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
