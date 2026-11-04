using System.Reflection;
using SwiftAid.UI.Components.Public;
using SwiftAid.UI.Components.Public.GuidedReportStepper;

namespace SwiftAid.Ui.Tests;

public sealed partial class GuidedReportDraftPersistenceTests
{
    [Theory]
    [InlineData("OnsetCalendarDateChangedAsync", true)]
    [InlineData("OccurrenceCalendarDateChangedAsync", false)]
    public async Task Calendar_selection_preserves_canonical_draft_dates_and_can_clear(string method, bool onset)
    {
        var draft = CreateDraft();
        var stepper = CreateStepper(draft, _ => Task.CompletedTask);
        var callback = typeof(GuidedReportStepper).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)callback.Invoke(stepper, [new DateTime(2026, 10, 5)])!;
        Assert.Equal("2026-10-05", onset ? draft.OnsetDateText : draft.OccurrenceDateText);
        Assert.Equal(new DateTime(2026, 10, 5), onset ? draft.OnsetDate : draft.OccurrenceDate);
        await (Task)callback.Invoke(stepper, [null])!;
        Assert.Null(onset ? draft.OnsetDate : draft.OccurrenceDate);
        Assert.Null(onset ? draft.OnsetDateText : draft.OccurrenceDateText);
    }

    [Theory]
    [InlineData("_focusValidationFieldAfterRender")]
    [InlineData("_focusStepHeadingAfterRender")]
    [InlineData("_focusValidationSummaryAfterRender")]
    public async Task Missing_focus_target_does_not_terminate_report_circuit(string flag)
    {
        var stepper = CreateStepper(CreateDraft(), _ => Task.CompletedTask);
        SetField(stepper, flag, true);
        await (Task)typeof(GuidedReportStepper).GetMethod("OnAfterRenderAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(stepper, [false])!;
    }
}
