using SwiftAid.Contracts.Ellai;
using SwiftAid.Contracts.Intake;
using SwiftAid.UI.Ellai.Guidance;

namespace SwiftAid.Ui.Tests;

public sealed class ReportVoiceAssistantContextTests
{
    [Fact]
    public async Task ConflictingSuggestionsAreNeverAutoApplied()
    {
        var context = new EllaiPublicReportDraftContext();
        var applied = 0;
        context.Register(new object(), _ => true, () => null, _ => true, _ => true, _ => true,
            new object(), (_, _) => { applied++; return true; });
        context.RegisterVoiceController(new object(), _ =>
        {
            context.BindVoiceCapture("capture");
            return Task.FromResult(true);
        }, () => Task.CompletedTask);
        await context.StartReportVoiceAsync(EllaiVoiceCaptureMode.Live);
        var lease = context.AdmitVoiceTurn("session", "turn", "capture")!;

        context.CompleteVoiceTurn(lease.Generation, "The patient is 35 years old.", new("Ready",
            [new("age", "35", "The patient is 35 years old"), new("age", "36", "The patient is 35 years old")]));

        Assert.Equal(0, applied);
        Assert.Equal(2, context.VoiceSuggestions.Count);
    }

    [Fact]
    public async Task LeavingDuringMicrophoneStartupStopsLateCapture()
    {
        var context = new EllaiPublicReportDraftContext();
        var owner = new object();
        var starting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = false;
        context.Register(owner, _ => true, () => null, _ => true, _ => true, _ => true, new object());
        context.RegisterVoiceController(new object(), _ => starting.Task,
            () => { stopped = true; return Task.CompletedTask; });

        var start = context.StartReportVoiceAsync(EllaiVoiceCaptureMode.Live);
        context.Remove(owner);
        starting.SetResult(true);

        Assert.False(await start);
        Assert.True(stopped);
    }

    [Fact]
    public async Task OnlyChosenCaptureUpdatesItsMountedDraftAndTurnIsDeduplicated()
    {
        var context = new EllaiPublicReportDraftContext();
        var firstDraft = new object();
        var transcript = string.Empty;
        var stopped = false;
        context.Register(new object(), _ => true, () => null, _ => true, _ => true,
            text => { transcript += text; return true; }, firstDraft);
        context.RegisterVoiceController(new object(), _ =>
        {
            context.BindVoiceCapture("report-capture");
            return Task.FromResult(true);
        }, () => { stopped = true; return Task.CompletedTask; });

        Assert.True(await context.StartReportVoiceAsync(EllaiVoiceCaptureMode.Live));
        Assert.Null(context.AdmitVoiceTurn("session", "turn", "other-capture"));
        var generation = context.AdmitVoiceTurn("session", "turn", "report-capture");
        Assert.NotNull(generation);
        Assert.Null(context.AdmitVoiceTurn("session", "turn", "report-capture"));
        Assert.True(context.AppendVoiceTranscript(generation.Generation, "No fever."));
        Assert.Equal("No fever.", transcript);

        context.Register(new object(), _ => true, () => null, _ => true, _ => true, _ => true, new object());
        Assert.True(stopped);
        context.CompleteVoiceTurn(generation.Generation, "No fever.", new("Ready", [new("observation", "fever", "No fever")]));
        Assert.Empty(context.VoiceSuggestions);
        Assert.False(context.AppendVoiceTranscript(generation.Generation, "Late result."));
        Assert.True(generation.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task SuggestionsRequireUserActionAndPreserveEvidence()
    {
        var context = new EllaiPublicReportDraftContext();
        string? observation = null;
        context.Register(new object(), _ => true, () => null, text => { observation = text; return true; },
            _ => true, _ => true, new object());
        context.RegisterVoiceController(new object(), _ =>
        {
            context.BindVoiceCapture("capture");
            return Task.FromResult(true);
        }, () => Task.CompletedTask);
        await context.StartReportVoiceAsync(EllaiVoiceCaptureMode.Recorded);
        var generation = context.AdmitVoiceTurn("session", "turn", "capture")!;
        context.CompleteVoiceTurn(generation.Generation, "No fever.", new("Ready", [new("observation", "fever", "No fever")]));

        Assert.Null(observation);
        Assert.True(context.ApplyVoiceSuggestion(Assert.Single(context.VoiceSuggestions)));
        Assert.Equal("No fever.", observation);
        Assert.Empty(context.VoiceSuggestions);
        await context.StopReportVoiceAsync();
        Assert.Null(context.AdmitVoiceTurn("session", "later", "capture"));
    }
}
