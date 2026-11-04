using SwiftAid.Contracts.Ellai;
using SwiftAid.Contracts.Intake;
using SwiftAid.UI.Ellai.Guidance;

namespace SwiftAid.Ui.Tests;

public sealed partial class EllaiSurfaceLogicTests
{
    [Fact]
    public async Task NativeRecordedModeBuffersPcmWithoutOpeningALiveStream()
    {
        var client = new FakeReviewClient();
        using var host = await CreateHost(client, CreateSession(1), string.Empty);
        SetField(host, "_capturedPcm", new MemoryStream());
        SetField(host, "_captureMode", EllaiVoiceCaptureMode.Recorded);
        SetField(host, "_isRecordingAudio", true);
        var method = typeof(EllaiGuidanceHost).GetMethod("QueueNativePcmChunkAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        await (ValueTask)method.Invoke(host.Component, [new ReadOnlyMemory<byte>(new byte[] { 1, 2, 3, 4 }), CancellationToken.None])!;

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, GetField<MemoryStream>(host, "_capturedPcm").ToArray());
        Assert.Equal(0, client.LiveTranscriptionCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReportVoiceUsesFinalTranscriptWithoutSubmittingEllaiConversation(bool live)
    {
        var extracted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeReviewClient
        {
            ReportVoiceHandler = (_, _, _, _) => Task.FromResult<EllaiVoiceTranscriptionResult?>(new("No fever.")),
            ReportExtractionHandler = (_, text, _) =>
            {
                Assert.Equal("No fever.", text);
                extracted.TrySetResult();
                return Task.FromResult<ReportVoiceExtractionResponse?>(new("Ready", []));
            }
        };
        using var host = await CreateHost(client, CreateSession(1), "unsent composer draft");
        var context = new EllaiPublicReportDraftContext();
        var transcript = string.Empty;
        context.Register(new object(), _ => true, () => null, _ => true, _ => true,
            value => { transcript += value; return true; }, new object());
        context.RegisterVoiceController(new object(), _ =>
        {
            context.BindVoiceCapture("report-capture");
            return Task.FromResult(true);
        }, () => Task.CompletedTask);
        await context.StartReportVoiceAsync(live ? EllaiVoiceCaptureMode.Live : EllaiVoiceCaptureMode.Recorded);
        SetField(host, "_reportVoiceContext", context);
        SetField(host, "_reportVoiceCapture", true);
        SetField(host, "_captureMode", live ? EllaiVoiceCaptureMode.Live : EllaiVoiceCaptureMode.Recorded);
        SetField(host, "_isRecordingAudio", true);
        SetField(host, "_liveVoiceCaptureId", "report-capture");

        if (live)
        {
            var status = new EllaiVoiceTurnStatusDto("TurnCompleted", 1, 1000, 500, null,
                [new(1, "provider", "No fever.", "session-1", "turn-1", 1,
                    VoiceCaptureId: "report-capture", ServerSubmission: false)], "session-1");
            await InvokeAsync(host, "SubmitConfirmedGatewayVoiceTurnsAsync", status, "session-1", false);
        }
        else
        {
            SetField(host, "_capturedPcm", new MemoryStream([1, 2, 3, 4]));
            SetField(host, "_capturedVoiceBytes", 4L);
            await InvokeAsync(host, "StopVoiceCaptureAsync");
        }

        await extracted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("No fever.", transcript);
        Assert.Empty(client.Submissions);
        Assert.Equal("unsent composer draft", GetField<string?>(host, "InputText"));
    }
}
