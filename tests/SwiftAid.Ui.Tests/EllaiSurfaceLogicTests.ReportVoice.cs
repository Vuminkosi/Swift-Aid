using Microsoft.JSInterop;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Contracts.Intake;
using SwiftAid.UI.Ellai.Guidance;

namespace SwiftAid.Ui.Tests;

public sealed partial class EllaiSurfaceLogicTests
{
    [Fact]
    public async Task EllaiCaptureStartingDuringReportConsentRemainsUntouched()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var grant = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeReviewClient { ReportConsentHandler = _ => { entered.TrySetResult(); return grant.Task; } };
        using var host = await CreateHost(client, CreateSession(1), string.Empty);
        var capture = new RecordingJsModule();
        SetField(host, "_captureModule", capture);
        SetField(host, "_microphoneCaptureAvailable", true);
        var start = InvokeTask(host, "StartReportVoiceCaptureAsync", EllaiVoiceCaptureMode.Live, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        SetField(host, "_isRecordingAudio", true);
        SetField(host, "_reportVoiceCapture", false);
        SetField(host, "_liveVoiceCaptureId", "ellai-capture");
        grant.SetResult(true);
        await start.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(GetField<bool>(host, "_isRecordingAudio"));
        Assert.False(GetField<bool>(host, "_reportVoiceCapture"));
        Assert.Equal("ellai-capture", GetField<string>(host, "_liveVoiceCaptureId"));
        Assert.DoesNotContain("startCapture", capture.Invocations);
    }

    [Fact]
    public async Task ActiveEllaiCaptureIsNotClaimedAsAReportCapture()
    {
        var client = new FakeReviewClient { ReportConsentHandler = _ => Task.FromResult(true) };
        using var host = await CreateHost(client, CreateSession(1), string.Empty);
        SetField(host, "_isRecordingAudio", true);
        SetField(host, "_reportVoiceCapture", false);
        SetField(host, "_liveVoiceCaptureId", "ellai-capture");
        SetField(host, "_microphoneCaptureAvailable", true);
        SetField(host, "_captureModule", new RecordingJsModule());
        var method = typeof(EllaiGuidanceHost).GetMethod("StartReportVoiceCaptureAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var started = await host.Dispatcher.InvokeAsync(async () =>
            await (Task<bool>)method.Invoke(host.Component, [EllaiVoiceCaptureMode.Live, CancellationToken.None])!);
        Assert.False(started);
        Assert.True(GetField<bool>(host, "_isRecordingAudio"));
        Assert.False(GetField<bool>(host, "_reportVoiceCapture"));
        Assert.Equal("ellai-capture", GetField<string>(host, "_liveVoiceCaptureId"));
    }

    [Fact]
    public async Task WithdrawingWhileReportConsentIsPendingDoesNotStartMicrophone()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var grant = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeReviewClient { ReportConsentHandler = _ => { entered.TrySetResult(); return grant.Task; } };
        using var host = await CreateHost(client, CreateSession(1), string.Empty);
        var capture = new RecordingJsModule();
        SetField(host, "_captureModule", capture);
        SetField(host, "_microphoneCaptureAvailable", true);
        using var cancellation = new CancellationTokenSource();
        var start = InvokeTask(host, "StartReportVoiceCaptureAsync", EllaiVoiceCaptureMode.Live, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        grant.SetResult(true);
        await start.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(GetField<bool>(host, "_isRecordingAudio"));
        Assert.DoesNotContain("startCapture", capture.Invocations);
    }

    [Fact]
    public async Task WithdrawingReportConsentDuringMicrophoneAdmissionDoesNotStartCapture()
    {
        var admissionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admission = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeReviewClient { VoiceConsentHandler = _ => { admissionStarted.TrySetResult(); return admission.Task; } };
        using var host = await CreateHost(client, CreateSession(1), string.Empty);
        var capture = new RecordingJsModule();
        SetField(host, "_captureModule", capture);
        SetField(host, "_microphoneCaptureAvailable", true);
        using var cancellation = new CancellationTokenSource();
        var start = InvokeTask(host, "StartVoiceCaptureCoreAsync", EllaiVoiceCaptureMode.Live, true, cancellation.Token);
        await admissionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        admission.SetResult(true);
        await start.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(GetField<bool>(host, "_isRecordingAudio"));
        Assert.False(GetField<bool>(host, "_startingVoiceCapture"));
        Assert.DoesNotContain("startCapture", capture.Invocations);
        Assert.Equal(0, client.LiveTranscriptionCalls);
    }

    [Fact]
    public async Task EarlyCompletedLiveTurnIsNotLostDuringMicrophoneStartup()
    {
        var extracted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeReviewClient
        {
            ReportExtractionHandler = (_, _, _) =>
            {
                extracted.TrySetResult();
                return Task.FromResult<ReportVoiceExtractionResponse?>(new("Ready", []));
            }
        };
        using var host = await CreateHost(client, CreateSession(1), string.Empty);
        var context = new EllaiPublicReportDraftContext();
        var transcript = string.Empty;
        context.Register(new object(), _ => true, () => null, _ => true, _ => true,
            value => { transcript += value; return true; }, new object());
        context.RegisterVoiceController(new object(), async mode =>
        {
            await InvokeAsync(host, "StartVoiceCaptureAsync", mode, true);
            return GetField<bool>(host, "_isRecordingAudio");
        }, () => Task.CompletedTask);
        var capture = new BlockingCaptureStart();
        SetField(host, "_reportVoiceContext", context);
        SetField(host, "_captureModule", capture);
        SetField(host, "_microphoneCaptureAvailable", true);
        SetField(host, "_voiceNetworkOnline", false);
        var start = context.StartReportVoiceAsync(EllaiVoiceCaptureMode.Live);
        try
        {
            await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var captureId = GetField<string>(host, "_liveVoiceCaptureId");
            var status = new EllaiVoiceTurnStatusDto("TurnCompleted", 1, 1000, 500, null,
                [new(1, "provider", "No fever.", "session-1", "turn-early", 1,
                    VoiceCaptureId: captureId, ServerSubmission: false)], "session-1");
            await InvokeAsync(host, "SubmitConfirmedGatewayVoiceTurnsAsync", status, "session-1", false);
            await extracted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("No fever.", transcript);
            Assert.Empty(client.Submissions);
        }
        finally
        {
            capture.Release.TrySetResult();
            await start;
            await InvokeAsync(host, "StopAndDiscardVoiceCaptureAsync");
        }
    }

    private sealed class BlockingCaptureStart : IJSObjectReference
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "startCapture")
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                return (TValue)(object)"Microphone active";
            }
            return default!;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

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
