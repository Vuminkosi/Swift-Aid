#pragma warning disable BL0005
using SwiftAid.UI.Components.Public;

namespace SwiftAid.Ui.Tests;

public sealed partial class GuidedReportDraftPersistenceTests
{
    [Fact]
    public async Task DraftCompletionRequiresStorageConfirmationAndCurrentUneditedValues()
    {
        var id = Guid.NewGuid();
        var stored = false;
        var component = CreateStepper(CreateDraft(), request =>
        {
            if (stored) request.ConfirmPersisted(id);
            return Task.CompletedTask;
        }, currentDraftId: id);
        bool Completed() => component.GetState().Controls.Single(control => control.ControlId == "report.save").CompletionAccepted;
        Assert.False(Completed());
        await InvokeSaveDraftAsync(component);
        Assert.False(Completed());
        Assert.Equal("Unconfirmed", GetSaveState(component));
        Assert.Equal("Observed concern; report details remain in the form.", component.Draft.Narrative);
        stored = true;
        await InvokeSaveDraftAsync(component);
        Assert.True(Completed());
        component.Draft.Narrative = "A newer unsaved observation.";
        Assert.False(Completed());
    }

    [Fact]
    public async Task StorageIdentityUpdatePreservesStepWithoutReplacingEditableDraft()
    {
        var component = CreateStepper(CreateDraft(), _ => Task.CompletedTask);
        SetField(component, "_step", 2);
        component.OnSaveDraft = Microsoft.AspNetCore.Components.EventCallback.Factory.Create<PublicReportSaveRequest>(new object(), request =>
        {
            component.CurrentDraftId = Guid.NewGuid();
            request.ConfirmPersisted(component.CurrentDraftId.Value);
            InvokeOnParametersSet(component);
        });
        await InvokeSaveDraftAsync(component);
        Assert.Equal(2, GetField<int>(component, "_step"));
        Assert.Equal("Saved", GetSaveState(component));
    }

    [Fact]
    public async Task EditsDuringPendingSaveAreNotReportedAsSaved()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var component = CreateStepper(CreateDraft(), _ => completed.Task, currentDraftId: Guid.NewGuid());
        var pending = InvokeSaveDraftAsync(component);
        component.Draft.Narrative = "New unsaved observation.";
        completed.SetResult();
        await pending;
        Assert.Equal("Unsaved", GetSaveState(component));
        Assert.Equal("New unsaved observation.", component.Draft.Narrative);
    }

    [Fact]
    public async Task NewEditsKeepTheirStepWhenTheSavedRevisionIdentityArrives()
    {
        var component = CreateStepper(CreateDraft(), _ => Task.CompletedTask, currentDraftId: Guid.NewGuid());
        SetField(component, "_step", 1);
        component.OnSaveDraft = Microsoft.AspNetCore.Components.EventCallback.Factory.Create<PublicReportSaveRequest>(new object(), request =>
        {
            component.Draft.Narrative = "New unsaved observation.";
            component.CurrentDraftId = Guid.NewGuid();
            request.ConfirmPersisted(component.CurrentDraftId.Value);
        });
        await InvokeSaveDraftAsync(component);
        InvokeOnParametersSet(component);
        Assert.Equal(1, GetField<int>(component, "_step"));
        Assert.Equal("Unsaved", GetSaveState(component));
    }

    [Fact]
    public async Task SwitchingDraftsDuringPendingSaveDoesNotTransferSuccess()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var component = CreateStepper(CreateDraft(), _ => completed.Task, currentDraftId: Guid.NewGuid());
        var pending = InvokeSaveDraftAsync(component);
        component.CurrentDraftId = Guid.NewGuid();
        component.Draft = CreateDraft("Another report.");
        InvokeOnParametersSet(component);
        completed.SetResult();
        await pending;
        Assert.Equal("Unsaved", GetSaveState(component));
        Assert.Equal("Another report.", component.Draft.Narrative);
    }

    [Fact]
    public async Task SwitchingAfterSuccessfulSaveClearsSavedState()
    {
        var id = Guid.NewGuid();
        var component = CreateStepper(CreateDraft(), request => { request.ConfirmPersisted(id); return Task.CompletedTask; }, currentDraftId: id);
        await InvokeSaveDraftAsync(component);
        Assert.Equal("Saved", GetSaveState(component));
        component.CurrentDraftId = Guid.NewGuid();
        component.Draft = CreateDraft("Another report.");
        InvokeOnParametersSet(component);
        Assert.Equal("Unsaved", GetSaveState(component));
    }

    [Fact]
    public async Task PersistedRevisionWithNewIdentityPreservesCurrentStep()
    {
        var component = CreateStepper(CreateDraft(), _ => Task.CompletedTask, currentDraftId: Guid.NewGuid());
        SetField(component, "_step", 2);
        component.OnSaveDraft = Microsoft.AspNetCore.Components.EventCallback.Factory.Create<PublicReportSaveRequest>(new object(), request =>
        {
            component.CurrentDraftId = Guid.NewGuid();
            request.ConfirmPersisted(component.CurrentDraftId.Value);
            component.Draft = PublicReportDraft.FromSubmission(request.Submission);
            InvokeOnParametersSet(component);
            return Task.CompletedTask;
        });
        await InvokeSaveDraftAsync(component);
        Assert.Equal(2, GetField<int>(component, "_step"));
        Assert.Equal("Saved", GetSaveState(component));
    }
}
#pragma warning restore BL0005
