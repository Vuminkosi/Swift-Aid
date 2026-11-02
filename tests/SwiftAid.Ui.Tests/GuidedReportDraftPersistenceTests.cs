#pragma warning disable BL0005 // Logic tests exercise component save-state transitions directly; production parameters are renderer-owned.
using System.Reflection;
using Microsoft.AspNetCore.Components;
using SwiftAid.Contracts.Intake;
using SwiftAid.UI.Components.Public;
using SwiftAid.UI.Components.Public.GuidedReportStepper;

namespace SwiftAid.Ui.Tests;

public sealed class GuidedReportDraftPersistenceTests
{
    [Fact]
    public async Task SemanticCompletionComesFromAcceptedValidationAndNeverIncludesEnteredValues()
    {
        var component = CreateStepper(CreateDraft(string.Empty), _ => Task.CompletedTask);
        Assert.DoesNotContain(component.GetState().Controls, control => control.CompletionAccepted);
        await (Task)Invoke(component, "ContinueFromTypeAsync")!;
        Assert.True(component.GetState().Controls.Single(control => control.ControlId == "report.subject").CompletionAccepted);
        await (Task)Invoke(component, "ContinueToReviewAsync")!;
        Assert.False(component.GetState().Controls.Single(control => control.ControlId == "report.narrative").CompletionAccepted);
        component.Draft.Narrative = "Synthetic private observation for validation only.";
        await (Task)Invoke(component, "ContinueToReviewAsync")!;
        var state = component.GetState();
        Assert.True(state.Controls.Single(control => control.ControlId == "report.narrative").CompletionAccepted);
        Assert.True(state.Controls.Single(control => control.ControlId == "report.time-location").CompletionAccepted);
        Assert.False(state.Controls.Single(control => control.ControlId == "report.submit").CompletionAccepted);
        Assert.DoesNotContain("Synthetic private", System.Text.Json.JsonSerializer.Serialize(state));
        component.Draft = CreateDraft("A different draft.");
        component.CurrentDraftId = Guid.NewGuid();
        InvokeOnParametersSet(component);
        Assert.DoesNotContain(component.GetState().Controls, control => control.CompletionAccepted);
    }

    [Fact]
    public async Task UnavailableStorageDisablesSaveAndDoesNotReportSuccess()
    {
        var saveCalls = 0;
        var component = CreateStepper(
            CreateDraft(),
            request =>
            {
                saveCalls++;
                return Task.CompletedTask;
            },
            draftSavingAvailable: false);

        Assert.False(GetCanSaveDraft(component));

        await InvokeSaveDraftAsync(component);

        Assert.Equal(0, saveCalls);
        Assert.Equal("Unsaved", GetSaveState(component));
        Assert.Contains("report details", component.Draft.Narrative, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedStorageCallbackDoesNotReportSavedOrClearEntries()
    {
        var draft = CreateDraft();
        var component = CreateStepper(
            draft,
            _ => Task.FromException(new IOException("storage unavailable")));

        await InvokeSaveDraftAsync(component);

        Assert.Equal("Failed", GetSaveState(component));
        Assert.Same(draft, component.Draft);
        Assert.Equal("Observed concern; report details remain in the form.", component.Draft.Narrative);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulSavePreservesCurrentStepWhenDraftIsRehydrated(bool assignNewIdentity)
    {
        var draftId = assignNewIdentity ? (Guid?)null : Guid.NewGuid();
        var component = CreateStepper(
            CreateDraft(),
            _ => Task.CompletedTask,
            currentDraftId: draftId);
        SetField(component, "_step", 2);
        var savedId = draftId ?? Guid.NewGuid();
        IntakeSubmission? savedSubmission = null;
        component.OnSaveDraft = EventCallback.Factory.Create<PublicReportSaveRequest>(
            new object(),
            request =>
            {
                savedSubmission = request.Submission;
                return Task.CompletedTask;
            });

        await InvokeSaveDraftAsync(component);

        Assert.NotNull(savedSubmission);
        component.CurrentDraftId = savedId;
        component.Draft = PublicReportDraft.FromSubmission(savedSubmission);
        InvokeOnParametersSet(component);

        Assert.Equal(2, GetField<int>(component, "_step"));
        Assert.Equal("Saved", GetSaveState(component));
    }

    [Fact]
    public async Task LoadingADifferentDraftAfterSaveRefreshResetsTheWizardToItsFirstStep()
    {
        IntakeSubmission? savedSubmission = null;
        var component = CreateStepper(
            CreateDraft(),
            request =>
            {
                savedSubmission = request.Submission;
                return Task.CompletedTask;
            },
            currentDraftId: Guid.NewGuid());
        SetField(component, "_step", 2);
        await InvokeSaveDraftAsync(component);
        Assert.NotNull(savedSubmission);
        var savedId = component.CurrentDraftId;
        component.CurrentDraftId = savedId;
        component.Draft = PublicReportDraft.FromSubmission(savedSubmission);
        InvokeOnParametersSet(component);
        Assert.Equal(2, GetField<int>(component, "_step"));

        component.CurrentDraftId = Guid.NewGuid();
        component.Draft = CreateDraft("A different report.");

        InvokeOnParametersSet(component);

        Assert.Equal(0, GetField<int>(component, "_step"));
    }

    private static GuidedReportStepper CreateStepper(
        PublicReportDraft draft,
        Func<PublicReportSaveRequest, Task> save,
        bool draftSavingAvailable = true,
        Guid? currentDraftId = null)
    {
        var component = new GuidedReportStepper
        {
            Draft = draft,
            DraftSavingAvailable = draftSavingAvailable,
            CurrentDraftId = currentDraftId,
            OnSaveDraft = EventCallback.Factory.Create<PublicReportSaveRequest>(new object(), save)
        };

        InvokeOnParametersSet(component);
        return component;
    }

    private static PublicReportDraft CreateDraft(string narrative = "Observed concern; report details remain in the form.") => new()
    {
        SubjectScope = IntakeSubjectScope.Human,
        Narrative = narrative,
        LocationUnknown = true
    };

    private static Task InvokeSaveDraftAsync(GuidedReportStepper component) =>
        (Task)Invoke(component, "SaveDraftAsync")!;

    private static void InvokeOnParametersSet(GuidedReportStepper component) =>
        _ = Invoke(component, "OnParametersSet");

    private static object? Invoke(GuidedReportStepper component, string name) =>
        component.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(component, null);

    private static bool GetCanSaveDraft(GuidedReportStepper component) =>
        (bool)component.GetType().GetProperty("CanSaveDraft", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(component)!;

    private static string GetSaveState(GuidedReportStepper component) =>
        GetField<object>(component, "_saveState").ToString()!;

    private static T GetField<T>(GuidedReportStepper component, string name) =>
        (T)component.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(component)!;

    private static void SetField<T>(GuidedReportStepper component, string name, T value) =>
        component.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(component, value);
}
#pragma warning restore BL0005
