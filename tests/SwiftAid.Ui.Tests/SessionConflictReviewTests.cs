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
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedCreatePreflightRestoresDraftOnlyForOriginalAuthorizedActor(bool sameActor)
    {
        var id = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow.AddDays(1);
        var current = new CommunitySessionView(id, 3, "Current", "Summary", "Community", [], "SZ", "region", "education", "Presenter", "en", start, "UTC", 60, null, [new StoredSessionMaterial("card", "1")], SessionVisibility.Public, SessionChannelType.None, null, [24], "Scheduled", "creator", "reviewer", false, 1, null);
        var input = new CreateSessionInput("Draft", "Summary", "Community", [], "SZ", "region", "education", "Presenter", "en", start, "UTC", 60, null, [new SessionMaterialInput("card", "1")], SessionVisibility.Public, SessionChannelType.None, null, [24]);
        var page = new SwiftAid.Gateway.Components.Pages.Sessions();
        var type = page.GetType();
        var client = new StaffClient(current, true) { IdentityUnavailable = true };
        type.GetProperty("StaffSessionEditor", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, client);
        type.GetField("_actorId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, "officer");

        await (Task)type.GetMethod("RunCommandAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(page, [input.ToCommand(Guid.NewGuid(), "reason", DateTimeOffset.UtcNow), null])!;
        Assert.NotNull(type.GetField("_deferredCreate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page));
        client.IdentityUnavailable = false;
        client.ActorId = sameActor ? "officer" : "another-officer";
        await (Task)type.GetMethod("ManageSessionAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, [null])!;

        var restored = (SessionCreateSubmission?)type.GetField("_recoveredCreate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page);
        if (sameActor) Assert.Equal("Draft", Assert.IsType<SessionCreateSubmission>(restored).Input.Title);
        else Assert.Null(restored);
        Assert.Null(type.GetField("_deferredCreate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SaveConfirmationRequiresMatchingResultEvenWhenRefreshFails(bool matchingResult)
    {
        var id = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow.AddDays(1);
        var current = new CommunitySessionView(id, 3, "Current", "Summary", "Community", [], "SZ", "region", "education", "Presenter", "en", start, "UTC", 60, null, [new StoredSessionMaterial("card", "1")], SessionVisibility.Public, SessionChannelType.None, null, [24], "Scheduled", "creator", "reviewer", false, 1, null);
        var input = new UpdateSessionInput("Proposal", "Summary", "Community", [], "Presenter", "en", start, 60, null, [new SessionMaterialInput("card", "1")], SessionVisibility.Public, SessionChannelType.None, null, [24]);
        var proposal = new SessionUpdateSubmission(id, 3, input, "reason");
        var page = new SwiftAid.Gateway.Components.Pages.Sessions();
        var type = page.GetType();
        var completion = new TaskCompletionSource<UiCapabilityResult<SessionCommandResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        completion.SetResult(new UiCapabilityResult<SessionCommandResult>(UiCapabilityResultCode.Success,
            new SessionCommandResult(matchingResult ? id : Guid.NewGuid(), 4, "Scheduled"),
            new UiCapabilityMetadata("editor", "1", UiCapabilityProvenance.LiveService, DateTimeOffset.UtcNow, true)));
        type.GetProperty("StaffSessionEditor", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page,
            new StaffClient(current, true) { Execution = completion, ReadUnavailable = true });
        type.GetField("_actorId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, "officer");
        type.GetField("_editingSessionId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, id);
        type.GetField("_editingVersion", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, 3L);
        type.GetField("_editingStatus", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, "Scheduled");

        await (Task)type.GetMethod("RunCommandAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(page, [input.ToCommand(id, 3, "reason", DateTimeOffset.UtcNow), proposal])!;

        var notice = (string?)type.GetField("_commandNotice", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page);
        if (matchingResult)
        {
            Assert.Contains("was saved", notice, StringComparison.Ordinal);
            Assert.Contains("could not be refreshed", notice, StringComparison.Ordinal);
            Assert.Null(type.GetField("_deferredProposal", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page));
        }
        else
        {
            Assert.Null(notice);
            Assert.Same(proposal, type.GetField("_deferredProposal", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page));
        }
    }

    [Fact]
    public async Task FailedPreflightRetainsProposalAsNotSubmittedUntilAccessReturns()
    {
        var id = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow.AddDays(1);
        var current = new CommunitySessionView(id, 3, "Current", "Summary", "Community", [], "SZ", "region", "education", "Presenter", "en", start, "UTC", 60, null, [new StoredSessionMaterial("card", "1")], SessionVisibility.Public, SessionChannelType.None, null, [24], "Scheduled", "creator", "reviewer", false, 1, null);
        var input = new UpdateSessionInput("Proposal", "Summary", "Community", [], "Presenter", "en", start, 60, null, [new SessionMaterialInput("card", "1")], SessionVisibility.Public, SessionChannelType.None, null, [24]);
        var proposal = new SessionUpdateSubmission(id, 3, input, "reason");
        var page = new SwiftAid.Gateway.Components.Pages.Sessions();
        var type = page.GetType();
        var client = new StaffClient(current, true) { IdentityUnavailable = true };
        type.GetProperty("StaffSessionEditor", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, client);
        type.GetField("_actorId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, "officer");

        await (Task)type.GetMethod("RunCommandAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(page, [input.ToCommand(id, 3, "reason", DateTimeOffset.UtcNow), proposal])!;
        Assert.Same(proposal, type.GetField("_deferredProposal", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page));
        Assert.True((bool)type.GetField("_deferredNotSubmitted", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!);
        Assert.Null(type.GetField("_conflictProposal", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page));

        client.IdentityUnavailable = false;
        await (Task)type.GetMethod("RetryDeferredProposalAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, null)!;
        Assert.Same(proposal, type.GetField("_conflictProposal", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page));
        Assert.True((bool)type.GetField("_proposalNotSubmitted", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!);
        Assert.False((bool)type.GetField("_proposalOutcomeUnknown", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IdentityOutageRecoveryRestoresProposalOnlyToOriginalActor(bool sameActor)
    {
        var id = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow.AddDays(1);
        var current = new CommunitySessionView(id, 3, "Current", "Summary", "Community", [], "SZ", "region", "education", "Presenter", "en", start, "UTC", 60, null, [new StoredSessionMaterial("card", "1")], SessionVisibility.Public, SessionChannelType.None, null, [24], "Scheduled", "creator", "reviewer", false, 1, null);
        var input = new UpdateSessionInput("Proposal", "Summary", "Community", [], "Presenter", "en", start, 60, null, [new SessionMaterialInput("card", "1")], SessionVisibility.Public, SessionChannelType.None, null, [24]);
        var proposal = new SessionUpdateSubmission(id, 3, input, "reason");
        var page = new SwiftAid.Gateway.Components.Pages.Sessions();
        var type = page.GetType();
        var client = new StaffClient(current, true) { IdentityUnavailable = true };
        type.GetProperty("StaffSessionEditor", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, client);
        type.GetField("_actorId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, "officer");

        await (Task)type.GetMethod("RecoverConflictAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, [proposal, "officer", true])!;
        Assert.Null(type.GetField("_actorId", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page));
        Assert.Same(proposal, type.GetField("_deferredProposal", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page));

        client.IdentityUnavailable = false;
        client.ActorId = sameActor ? "officer" : "another-officer";
        await (Task)type.GetMethod("RetryDeferredProposalAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, null)!;

        var restored = type.GetField("_conflictProposal", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page);
        if (sameActor) Assert.Same(proposal, restored);
        else Assert.Null(restored);
        Assert.Null(type.GetField("_deferredProposal", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page));
    }

    [Fact]
    public async Task OldAccountUpdateResponseCannotRestoreProposalAfterIdentityChanges()
    {
        var id = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow.AddDays(1);
        var current = new CommunitySessionView(id, 3, "Current", "Summary", "Community", [], "SZ", "region", "education", "Presenter", "en", start, "UTC", 60, null, [new StoredSessionMaterial("card", "1")], SessionVisibility.Public, SessionChannelType.None, null, [24], "Scheduled", "creator", "reviewer", false, 1, null);
        var input = new UpdateSessionInput("Proposal", "Summary", "Community", [], "Presenter", "en", start, 60, null, [new SessionMaterialInput("card", "1")], SessionVisibility.Public, SessionChannelType.None, null, [24]);
        var proposal = new SessionUpdateSubmission(id, 3, input, "reason");
        var page = new SwiftAid.Gateway.Components.Pages.Sessions();
        var type = page.GetType();
        var completion = new TaskCompletionSource<UiCapabilityResult<SessionCommandResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new StaffClient(current, true) { Execution = completion };
        type.GetProperty("StaffSessionEditor", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, client);
        type.GetField("_actorId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, "officer");
        type.GetField("_editingSessionId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, id);
        type.GetField("_editingVersion", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, 3L);
        type.GetField("_editingStatus", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, "Scheduled");

        var pending = (Task)type.GetMethod("RunCommandAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(page, [input.ToCommand(id, 3, "reason", DateTimeOffset.UtcNow), proposal])!;
        type.GetField("_identityEpoch", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, 1L);
        type.GetMethod("ClearEditorState", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, null);
        type.GetField("_actorId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, "another-officer");
        completion.SetException(new InvalidOperationException("Simulated response failure"));
        await pending;

        Assert.Null(type.GetField("_deferredProposal", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page));
        Assert.Null(type.GetField("_conflictProposal", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page));
        Assert.Equal("another-officer", type.GetField("_actorId", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page));
    }

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
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task DisplaysBothVersionsAndEscapesUserContent(bool outcomeUnknown, bool notSubmitted)
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
                new Dictionary<string, object?> { [nameof(SessionConflictReview.Proposal)] = proposal, [nameof(SessionConflictReview.Current)] = current, [nameof(SessionConflictReview.OutcomeUnknown)] = outcomeUnknown, [nameof(SessionConflictReview.NotSubmitted)] = notSubmitted }));
            return component.ToHtmlString();
        });

        Assert.Contains(notSubmitted ? "update was not submitted" : outcomeUnknown ? "outcome could not be confirmed" : "Your update was not saved", markup, StringComparison.OrdinalIgnoreCase);
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
        public bool IdentityUnavailable { get; set; }
        public string ActorId { get; set; } = "officer";
        public TaskCompletionSource<UiCapabilityResult<SessionCommandResult>>? Execution { get; set; }
        private static UiCapabilityResult<T> Success<T>(T value) => new(UiCapabilityResultCode.Success, value,
            new UiCapabilityMetadata("editor", "1", UiCapabilityProvenance.LiveService, DateTimeOffset.UtcNow, true));

        public Task<UiCapabilityResult<string>> GetCurrentActorIdAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(IdentityUnavailable ? UiCapabilityResults.Unavailable<string>("editor") : Success(ActorId));
        public Task<UiCapabilityResult<CommunitySessionView>> GetStaffAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(ReadUnavailable ? UiCapabilityResults.Unavailable<CommunitySessionView>("editor") : Success(current));
        public Task<UiCapabilityResult<StaffSessionManagementCapabilities>> GetManagementCapabilitiesAsync(Guid? sessionId, long? expectedVersion, CancellationToken cancellationToken = default) =>
            Task.FromResult(Success(sessionId is null
                ? new StaffSessionManagementCapabilities(true, false, false, false, false, false, null, null, null, true)
                : new StaffSessionManagementCapabilities(false, canEdit, false, false, false, false, current.SessionId, current.Version, current.Status, true)));
        public Task<UiCapabilityResult<SessionCommandResult>> ExecuteAsync(SessionCommand command, CancellationToken cancellationToken = default) =>
            Execution?.Task ?? throw new NotSupportedException();
    }

    private sealed class NoOpJsRuntime : JSRuntime
    {
        protected override void BeginInvokeJS(long taskId, string identifier, string? argsJson, JSCallResultType resultType, long targetInstanceId) { }
        protected override void EndInvokeDotNet(DotNetInvocationInfo callInfo, in DotNetInvocationResult result) { }
    }
}
