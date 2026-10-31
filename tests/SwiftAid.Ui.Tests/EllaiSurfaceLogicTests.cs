using System.Security.Claims;
using System.Reflection;
using Bit.Bonboarding;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MudBlazor.Services;
using SwiftAid.Contracts.Ellai;
using SwiftAid.UI.Ellai;
using SwiftAid.UI.Ellai.Components;
using SwiftAid.UI.Ellai.Guidance;
using SwiftAid.UI.Components.Layout;

namespace SwiftAid.Ui.Tests;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T13")]
public sealed class EllaiSurfaceLogicTests
{
    [Fact]
    public async Task PublicLayoutExposesOnlyGuestAskModeToUnauthenticatedVisitors()
    {
        var markup = await RenderPublicLayoutAsync();

        Assert.Contains("Public page", markup, StringComparison.Ordinal);
        Assert.Contains("Open Ellai actions", markup, StringComparison.Ordinal);
        Assert.Contains("Ask", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Guide Me", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Help Me Work", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComposerKeepsSendDisabledWhenHostCannotSubmit()
    {
        var markup = await RenderComposerAsync();

        Assert.Contains("aria-label=\"Send to Ellai\"", markup, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Ellai voice and text controls\"", markup, StringComparison.Ordinal);
        Assert.Contains("Message Ellai", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Start voice input", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Stop live voice", markup, StringComparison.Ordinal);
        Assert.Matches("""(?s)<button(?=[^>]*aria-label="Send to Ellai")(?=[^>]*disabled)[^>]*>""", markup);
    }

    [Fact]
    public async Task LivePartialTranscriptIsDisplayedSeparatelyFromTheEditableDraft()
    {
        var markup = await RenderComposerAsync(
            inputText: "existing draft",
            livePartialTranscript: "transient recognized words");

        Assert.Contains("class=\"ellai-composer__partial-transcript\" aria-live=\"off\"", markup,
            StringComparison.Ordinal);
        Assert.Contains("transient recognized words", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Heard so far:", markup, StringComparison.Ordinal);
        Assert.Contains("existing draft", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LiveCaptureCanStartAlongsideEllaiPlaybackForBargeIn()
    {
        var streamCompletion = new TaskCompletionSource<EllaiVoiceTranscriptionResult?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeReviewClient
        {
            LiveTranscriptionHandler = (_, _) => streamCompletion.Task,
        };
        using var host = await CreateHost(client, CreateSession(version: 1), string.Empty);
        var captureModule = new RecordingJsModule();
        const string playbackId = "playback-current";
        SetField(host, "_captureModule", captureModule);
        SetField(host, "_microphoneCaptureAvailable", true);
        SetField(host, "_voiceNetworkOnline", true);
        SetField(host, "_isPlayingResponse", true);
        SetField(host, "_activeResponseSequence", 7L);
        SetField(host, "_activeResponsePlaybackId", playbackId);

        await InvokeAsync(host, "StartLiveCaptureAsync");

        Assert.True(GetField<bool>(host, "_isRecordingAudio"));
        Assert.True(GetField<bool>(host, "_isPlayingResponse"),
            $"Response playback was cancelled. Status: {GetField<string?>(host, "StatusText")}");
        Assert.Equal(7L, GetField<long>(host, "_activeResponseSequence"));
        Assert.Equal(playbackId, GetField<string?>(host, "_activeResponsePlaybackId"));
        Assert.Contains("setPlaybackState", captureModule.Invocations);
        Assert.Contains("I’m listening while Ellai speaks", GetField<string?>(host, "StatusText"),
            StringComparison.Ordinal);

        await InvokeAsync(host, "OnVoiceBargeInDetected", 7L, playbackId);

        Assert.False(GetField<bool>(host, "_isPlayingResponse"));
        Assert.True(GetField<bool>(host, "_isRecordingAudio"));
        Assert.Contains("You interrupted Ellai", GetField<string?>(host, "StatusText"),
            StringComparison.Ordinal);

        streamCompletion.SetResult(null);
        await InvokeAsync(host, "StopAndDiscardVoiceCaptureAsync");
    }

    [Fact]
    public async Task InterruptedLiveStreamRetriesPcmOnceAndSubmitsTranscriptOnce()
    {
        var client = new FakeReviewClient(
            new EllaiUtteranceSubmitResult(
                EllaiClientOperationStatus.Accepted,
                CreateSession(version: 2)))
        {
            LiveTranscriptionHandler = (_, _) => Task.FromResult<EllaiVoiceTranscriptionResult?>(null),
            VoiceTranscriptionHandler = (_, _, _, _) =>
                Task.FromResult<EllaiVoiceTranscriptionResult?>(new("recovered words")),
        };
        using var host = await CreateHost(client, CreateSession(version: 1), string.Empty);
        SetField(host, "_capturedPcm", new MemoryStream());
        SetField(host, "_captureMode", EllaiVoiceCaptureMode.Live);
        SetField(host, "_isRecordingAudio", true);
        await InvokeAsync(host, "StartLiveVoiceStream", "session-1");

        await InvokeAsync(host, "ReceiveVoicePcmChunk", new[] { 1234, -2345 });
        await InvokeAsync(host, "StopVoiceCaptureAsync");

        var recovery = Assert.Single(client.VoiceTranscriptions);
        Assert.Equal("session-1", recovery.SessionId);
        Assert.Equal(EllaiVoiceCaptureMode.Live, recovery.Mode);
        Assert.Equal(new byte[] { 0xD2, 0x04, 0xD7, 0xF6 }, recovery.Audio);
        var submitted = Assert.Single(client.Submissions);
        Assert.Equal("recovered words", submitted.FinalizedText);
    }

    [Fact]
    public async Task FailedLiveStreamRecoveryExplainsThatTheTurnWasNotSent()
    {
        var client = new FakeReviewClient
        {
            LiveTranscriptionHandler = (_, _) => Task.FromResult<EllaiVoiceTranscriptionResult?>(null),
            VoiceTranscriptionHandler = (_, _, _, _) => Task.FromResult<EllaiVoiceTranscriptionResult?>(null),
        };
        using var host = await CreateHost(client, CreateSession(version: 1), "draft");
        SetField(host, "_capturedPcm", new MemoryStream());
        SetField(host, "_captureMode", EllaiVoiceCaptureMode.Live);
        SetField(host, "_isRecordingAudio", true);
        await InvokeAsync(host, "StartLiveVoiceStream", "session-1");

        await InvokeAsync(host, "ReceiveVoicePcmChunk", new[] { 100 });
        await InvokeAsync(host, "StopVoiceCaptureAsync");

        Assert.Single(client.VoiceTranscriptions);
        Assert.Contains("was not sent", GetField<string?>(host, "StatusText"), StringComparison.Ordinal);
        Assert.Equal("draft", GetField<string?>(host, "InputText"));
        Assert.Empty(client.Submissions);
    }

    [Fact]
    public async Task BrowserChannelBackpressureSwitchesToBoundedPcmRecovery()
    {
        var streamCompletion = new TaskCompletionSource<EllaiVoiceTranscriptionResult?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeReviewClient
        {
            LiveTranscriptionHandler = (_, _) => streamCompletion.Task,
            VoiceTranscriptionHandler = (_, _, _, _) => Task.FromResult<EllaiVoiceTranscriptionResult?>(null),
        };
        using var host = await CreateHost(client, CreateSession(version: 1), "draft");
        SetField(host, "_capturedPcm", new MemoryStream());
        SetField(host, "_captureMode", EllaiVoiceCaptureMode.Live);
        SetField(host, "_isRecordingAudio", true);
        await InvokeAsync(host, "StartLiveVoiceStream", "session-1");

        for (var sample = 0; sample < 9; sample++)
        {
            await InvokeAsync(host, "ReceiveVoicePcmChunk", new[] { sample + 1 });
        }

        Assert.True(GetField<bool>(host, "_liveVoiceReconnectRequired"));
        streamCompletion.SetResult(null);
        await InvokeAsync(host, "StopVoiceCaptureAsync");

        var recovery = Assert.Single(client.VoiceTranscriptions);
        Assert.Equal(18, recovery.Audio.Length);
        Assert.Equal("draft", GetField<string?>(host, "InputText"));
        Assert.Empty(client.Submissions);
    }

    [Fact]
    public async Task BrowserCaptureQueueOverflowProcessesBufferedLiveTurnInsteadOfDiscardingIt()
    {
        var client = new FakeReviewClient
        {
            LiveTranscriptionHandler = (_, _) => Task.FromResult<EllaiVoiceTranscriptionResult?>(null),
            VoiceTranscriptionHandler = (_, _, _, _) =>
                Task.FromResult<EllaiVoiceTranscriptionResult?>(new("buffered words")),
        };
        using var host = await CreateHost(client, CreateSession(version: 1), "draft");
        var pcm = new MemoryStream([1, 2, 3, 4]);
        SetField(host, "_capturedPcm", pcm);
        SetField(host, "_capturedVoiceBytes", 4L);
        SetField(host, "_captureMode", EllaiVoiceCaptureMode.Live);
        SetField(host, "_isRecordingAudio", true);
        await InvokeAsync(host, "StartLiveVoiceStream", "session-1");

        await InvokeAsync(host, "OnVoiceCaptureBackpressure");

        Assert.Single(client.VoiceTranscriptions);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, client.VoiceTranscriptions[0].Audio);
        Assert.Equal("draft\nbuffered words", GetField<string?>(host, "InputText"));
        Assert.Empty(client.Submissions);
    }

    [Fact]
    public async Task ConnectivityRestorationReplaysBufferedAudioAndResumesLiveCapture()
    {
        var call = 0;
        var streams = new List<byte[]>();
        var client = new FakeReviewClient
        {
            LiveTranscriptionHandler = async (_, stream) =>
            {
                var callNumber = Interlocked.Increment(ref call);
                using var replayed = new MemoryStream();
                await stream.CopyToAsync(replayed);
                streams.Add(replayed.ToArray());
                if (callNumber == 1)
                {
                    return null;
                }

                return new EllaiVoiceTranscriptionResult("resumed words");
            },
        };
        using var host = await CreateHost(client, CreateSession(version: 1), "draft");
        SetField(host, "_capturedPcm", new MemoryStream());
        SetField(host, "_captureMode", EllaiVoiceCaptureMode.Live);
        SetField(host, "_isRecordingAudio", true);
        SetField(host, "_voiceNetworkOnline", true);
        await InvokeAsync(host, "StartLiveVoiceStream", "session-1");
        await InvokeAsync(host, "ReceiveVoicePcmChunk", new[] { 10, 11 });

        await InvokeAsync(host, "OnVoiceConnectivityChanged", false);
        await InvokeAsync(host, "OnVoiceConnectivityChanged", true);
        await InvokeAsync(host, "ReceiveVoicePcmChunk", new[] { 12 });

        Assert.False(GetField<bool>(host, "_liveVoiceReconnectRequired"));
        Assert.True(GetField<bool>(host, "_voiceNetworkOnline"));
        Assert.Contains("resumed", GetField<string?>(host, "StatusText"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, client.LiveTranscriptionCalls);

        await InvokeAsync(host, "StopVoiceCaptureAsync");

        Assert.Empty(client.VoiceTranscriptions);
        Assert.Equal("draft\nresumed words", GetField<string?>(host, "InputText"));
        Assert.Equal(2, client.LiveTranscriptionCalls);
        Assert.Equal(new byte[] { 10, 0, 11, 0, 12, 0 }, Assert.Single(streams.Skip(1)));
        Assert.Empty(client.Submissions);
    }

    [Fact]
    public async Task FailedLiveReconnectFallsBackToOneBufferedUpload()
    {
        var call = 0;
        var client = new FakeReviewClient
        {
            LiveTranscriptionHandler = async (_, stream) =>
            {
                Interlocked.Increment(ref call);
                await stream.CopyToAsync(Stream.Null);
                return null;
            },
            VoiceTranscriptionHandler = (_, _, _, _) =>
                Task.FromResult<EllaiVoiceTranscriptionResult?>(new("uploaded words")),
        };
        using var host = await CreateHost(client, CreateSession(version: 1), "draft");
        SetField(host, "_capturedPcm", new MemoryStream());
        SetField(host, "_captureMode", EllaiVoiceCaptureMode.Live);
        SetField(host, "_isRecordingAudio", true);
        SetField(host, "_voiceNetworkOnline", true);
        await InvokeAsync(host, "StartLiveVoiceStream", "session-1");
        await InvokeAsync(host, "ReceiveVoicePcmChunk", new[] { 21, 22 });

        await InvokeAsync(host, "OnVoiceConnectivityChanged", false);
        await InvokeAsync(host, "OnVoiceConnectivityChanged", true);
        await InvokeAsync(host, "ReceiveVoicePcmChunk", new[] { 23 });
        await InvokeAsync(host, "StopVoiceCaptureAsync");

        var upload = Assert.Single(client.VoiceTranscriptions);
        Assert.Equal(new byte[] { 21, 0, 22, 0, 23, 0 }, upload.Audio);
        Assert.Equal("draft\nuploaded words", GetField<string?>(host, "InputText"));
        Assert.Equal(2, client.LiveTranscriptionCalls);
        Assert.Empty(client.Submissions);
    }

    [Fact]
    public async Task NativeCaptureBuffersPcmWithoutStartingLiveStreamWhenOffline()
    {
        var client = new FakeReviewClient
        {
            VoiceTranscriptionHandler = (_, _, _, _) =>
                Task.FromResult<EllaiVoiceTranscriptionResult?>(new("offline words")),
        };
        using var host = await CreateHost(client, CreateSession(version: 1), "draft");
        SetField(host, "_capturedPcm", new MemoryStream());
        SetField(host, "_captureMode", EllaiVoiceCaptureMode.Live);
        SetField(host, "_isRecordingAudio", true);
        SetField(host, "_liveVoiceReconnectRequired", true);
        SetField(host, "_voiceNetworkOnline", false);
        var queueMethod = typeof(EllaiGuidanceHost).GetMethod(
            "QueueNativePcmChunkAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var chunk = new ReadOnlyMemory<byte>(new byte[] { 8, 9, 10, 11 });

        await (ValueTask)queueMethod.Invoke(host.Component, [chunk, CancellationToken.None])!;
        await InvokeAsync(host, "StopVoiceCaptureAsync");

        var recovery = Assert.Single(client.VoiceTranscriptions);
        Assert.Equal(new byte[] { 8, 9, 10, 11 }, recovery.Audio);
        Assert.Equal("draft\noffline words", GetField<string?>(host, "InputText"));
        Assert.Equal(0, client.LiveTranscriptionCalls);
    }

    [Fact]
    public async Task EmptyComposerShowsLiveVoiceControlInsteadOfSend()
    {
        var markup = await RenderComposerAsync(inputText: string.Empty, canUseMicrophone: true,
            onStartRealtimeVoice: EventCallback.Factory.Create(new object(), () => Task.CompletedTask),
            isLiveSpeaking: false);

        Assert.Contains("aria-label=\"Start realtime voice dialog\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("aria-label=\"Send to Ellai\"", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyComposerKeepsUnavailableVoiceControlVisibleAndDisabled()
    {
        var markup = await RenderComposerAsync(inputText: string.Empty, canUseMicrophone: false,
            isLiveSpeaking: false);

        Assert.Contains("aria-label=\"Realtime voice dialog unavailable\"", markup, StringComparison.Ordinal);
        Assert.Matches("""(?s)<button(?=[^>]*aria-label="Realtime voice dialog unavailable")(?=[^>]*disabled)[^>]*>""", markup);
        Assert.DoesNotContain("aria-label=\"Send to Ellai\"", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeLiveCaptureCanBeAvailableWhenBrowserRecordingIsUnavailable()
    {
        var callback = EventCallback.Factory.Create(new object(), () => Task.CompletedTask);
        var markup = await RenderComposerAsync(inputText: string.Empty,
            canUseMicrophone: false,
            canUseRealtimeMicrophone: true,
            onStartVoice: callback,
            onStartRealtimeVoice: callback,
            isLiveSpeaking: false);

        Assert.Matches("""(?s)<button(?=[^>]*aria-label="Audio transcription unavailable")(?=[^>]*disabled)[^>]*>""", markup);
        Assert.Contains("aria-label=\"Start realtime voice dialog\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("aria-label=\"Realtime voice dialog unavailable\"", markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComposerGroupsVoiceControlsSettingsEndSessionAndClose()
    {
        var callback = EventCallback.Factory.Create(new object(), () => Task.CompletedTask);
        var markup = await RenderComposerAsync(
            inputText: string.Empty,
            canUseMicrophone: true,
            onStartVoice: callback,
            onStartRealtimeVoice: callback,
            onOpenSettings: callback,
            isLiveSpeaking: false);

        var fieldActionsStart = markup.IndexOf("ellai-composer__field-actions", StringComparison.Ordinal);
        var microphoneIndex = markup.IndexOf("Record audio for transcription", fieldActionsStart, StringComparison.Ordinal);
        var liveVoiceIndex = markup.IndexOf("Start realtime voice dialog", fieldActionsStart, StringComparison.Ordinal);
        Assert.True(microphoneIndex > fieldActionsStart);
        Assert.True(liveVoiceIndex > microphoneIndex);

        var headerActionsStart = markup.IndexOf("ellai-composer__header-actions", StringComparison.Ordinal);
        var headerActionsEnd = markup.IndexOf("ellai-composer__input-row", headerActionsStart, StringComparison.Ordinal);
        var headerActions = markup[headerActionsStart..headerActionsEnd];
        Assert.Contains("Ellai About and settings", headerActions, StringComparison.Ordinal);
        Assert.Contains("End Ellai session", headerActions, StringComparison.Ordinal);
        Assert.Contains("Close Ellai composer", headerActions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClosingComposerHidesItButKeepsActiveSessionAndDoesNotCancelIt()
    {
        var client = new FakeReviewClient();
        var session = CreateSession(version: 1);
        using var host = await CreateHost(client, session, string.Empty);
        SetField(host, "ShowComposer", true);

        await InvokeAsync(host, "CloseComposerAsync");

        Assert.False(GetField<bool>(host, "ShowComposer"));
        Assert.Same(session, GetField<EllaiSessionView?>(host, "_sessionView"));
        Assert.Empty(client.Cancellations);
        Assert.Contains("still active", GetField<string?>(host, "StatusText"), StringComparison.Ordinal);

        await InvokeAsync(host, "SelectModeAsync", EllaiMode.Ask);

        Assert.True(GetField<bool>(host, "ShowComposer"));
        Assert.Same(session, GetField<EllaiSessionView?>(host, "_sessionView"));
        Assert.Equal(0, client.SessionStartCalls);
    }

    [Fact]
    public async Task ApprovalOutcomeWithoutConfirmingReceiptStaysUnknown()
    {
        var client = new FakeReviewClient
        {
            ListedReceipts = null,
        };
        using var host = await CreateHost(client, CreateSession(version: 1), string.Empty);
        var review = new EllaiReviewDto(
            "review-1", "session-1", "plan-hash", 1, "Pending",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5),
            [new EllaiApprovalActionDescriptor("action-1", "Open case", "Open case details")]);

        await InvokeAsync(host, "SubmitSelectionAsync", review, (IReadOnlyList<string>)["action-1"]);

        Assert.Contains("outcome unknown", GetField<string?>(host, "StatusText"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, client.ApprovalCalls);
        Assert.Equal(1, client.ReceiptListCalls);
    }

    [Fact]
    public async Task RejectionFailureStaysUnknownInsteadOfClaimingRejected()
    {
        var client = new FakeReviewClient
        {
            ActiveReview = new EllaiReviewDto(
                "review-1", "session-1", "plan-hash", 1, "Pending",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5),
                [new EllaiApprovalActionDescriptor("action-1", "Open case", "Open case details")]),
        };
        using var host = await CreateHost(client, CreateSession(version: 1), string.Empty);
        var review = client.ActiveReview;

        await InvokeAsync(host, "RejectAsync", review);

        Assert.Contains("Rejection outcome unknown", GetField<string?>(host, "StatusText"));
        Assert.NotEqual("Rejected.", GetField<string?>(host, "StatusText"));
        Assert.Equal(EllaiStatusPhases.Blocked, GetField<string?>(host, "Phase"));
        Assert.Equal(1, client.RejectionCalls);
        Assert.Equal(1, client.ActiveReviewReads);
    }

    [Fact]
    public async Task ConfirmedRejectionClosesReviewAndReportsSuccess()
    {
        var client = new FakeReviewClient
        {
            RejectionResult = true,
            ActiveReview = new EllaiReviewDto(
                "review-1", "session-1", "plan-hash", 1, "Pending",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5),
                [new EllaiApprovalActionDescriptor("action-1", "Open case", "Open case details")]),
        };
        using var host = await CreateHost(client, CreateSession(version: 1), string.Empty);
        var review = client.ActiveReview;

        await InvokeAsync(host, "RejectAsync", review);

        Assert.Equal("Review rejected.", GetField<string?>(host, "StatusText"));
        Assert.Equal(EllaiStatusPhases.Ready, GetField<string?>(host, "Phase"));
        Assert.Null(GetField<object?>(host, "_reviewDialog"));
        Assert.Equal(1, client.RejectionCalls);
        Assert.Equal(0, client.ActiveReviewReads);
    }

    [Fact]
    public async Task RejectionTransportFailureDoesNotClaimSuccess()
    {
        var client = new FakeReviewClient
        {
            RejectionException = new HttpRequestException("response unavailable"),
            ActiveReview = new EllaiReviewDto(
                "review-1", "session-1", "plan-hash", 1, "Pending",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5),
                [new EllaiApprovalActionDescriptor("action-1", "Open case", "Open case details")]),
        };
        using var host = await CreateHost(client, CreateSession(version: 1), string.Empty);

        await InvokeAsync(host, "RejectAsync", client.ActiveReview);

        Assert.Contains("Rejection outcome unknown", GetField<string?>(host, "StatusText"));
        Assert.NotEqual("Review rejected.", GetField<string?>(host, "StatusText"));
        Assert.Equal(EllaiStatusPhases.Blocked, GetField<string?>(host, "Phase"));
        Assert.Equal(1, client.ActiveReviewReads);
    }

    [Fact]
    public async Task AmbiguousStartDoesNotOfferAnotherStartOrComposer()
    {
        using var host = await CreateHost(new FakeReviewClient(), CreateSession(version: 1), "");
        SetField(host, "_sessionView", null);

        await InvokeAsync(host, "SelectModeAsync", EllaiMode.Ask);

        Assert.True(GetField<bool>(host, "_startOutcomeUnknown"));
        Assert.False(GetField<bool>(host, "ShowComposer"));
        Assert.False(GetProperty<bool>(host, "CanSubmitText"));
        Assert.Contains("Do not start another session", GetField<string?>(host, "StatusText"));
    }

    [Fact]
    public async Task ConfirmedAccessDenialDoesNotClaimRemoteSessionIsUnknown()
    {
        var client = new FakeReviewClient
        {
            StartHandler = (_, _) => Task.FromResult(
                new EllaiSessionStartResult(EllaiClientOperationStatus.PolicyBlocked)),
        };
        using var host = await CreateHost(client, CreateSession(version: 1), "");
        SetField(host, "_sessionView", null);

        await InvokeAsync(host, "SelectModeAsync", EllaiMode.Ask);

        Assert.False(GetField<bool>(host, "_startOutcomeUnknown"));
        Assert.True(GetField<bool>(host, "_showStartFailure"));
        Assert.False(GetField<bool>(host, "ShowComposer"));
        Assert.Contains("unavailable for this account or workspace", GetField<string?>(host, "StatusText"));
        Assert.DoesNotContain("contact support", GetField<string?>(host, "StatusText"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ServerCancellationEventUsesCommonTerminalCleanup()
    {
        using var host = await CreateHost(new FakeReviewClient(), CreateSession(version: 1), "draft");
        SetField(host, "ShowComposer", true);
        SetField(host, "IsActive", true);

        await InvokeAsync(host, "OnSessionCancelled");

        Assert.Null(GetField<EllaiSessionView?>(host, "_sessionView"));
        Assert.False(GetField<bool>(host, "ShowComposer"));
        Assert.False(GetField<bool>(host, "IsActive"));
        Assert.Null(GetField<string?>(host, "InputText"));
        Assert.Equal("Ellai session ended.", GetField<string?>(host, "StatusText"));
    }

    [Fact]
    public async Task CurrentModelResponseUpdatesOnlyItsMatchingCompactSessionStatus()
    {
        using var host = await CreateHost(new FakeReviewClient(), CreateSession(version: 1), string.Empty);

        await InvokeAsync(host, "OnResponseReceived",
            new EllaiSessionResponseDto(1, "other-session", "Foreign response.", DateTimeOffset.UtcNow));
        Assert.NotEqual("Foreign response.", GetField<string?>(host, "StatusText"));

        const string currentResponse = "Here is the current result.";
        await InvokeAsync(host, "OnResponseReceived",
            new EllaiSessionResponseDto(1, "session-1", currentResponse, DateTimeOffset.UtcNow));

        Assert.Equal(currentResponse, GetField<string?>(host, "StatusText"));
        Assert.Equal(EllaiStatusPhases.Ready, GetField<string?>(host, "Phase"));
    }

    [Fact]
    public async Task UnavailableSubmitRetriesWithSameIdAndAcceptedNewSendGetsNewId()
    {
        var session = CreateSession(version: 1);
        var updatedSession = CreateSession(version: 2);
        var client = new FakeReviewClient(
            new EllaiUtteranceSubmitResult(EllaiClientOperationStatus.Unavailable, null),
            new EllaiUtteranceSubmitResult(EllaiClientOperationStatus.Accepted, updatedSession),
            new EllaiUtteranceSubmitResult(
                EllaiClientOperationStatus.Accepted,
                CreateSession(version: 3)));
        using var host = await CreateHost(client, session, "  first message  ");

        await InvokeAsync(host, "OnSend");
        await InvokeAsync(host, "OnSend");
        SetField(host, "InputText", "second message");
        await InvokeAsync(host, "OnSend");

        Assert.Equal(3, client.Submissions.Count);
        Assert.Equal(client.Submissions[0].ClientUtteranceId, client.Submissions[1].ClientUtteranceId);
        Assert.NotEqual(client.Submissions[1].ClientUtteranceId, client.Submissions[2].ClientUtteranceId);
        Assert.Equal("first message", client.Submissions[0].FinalizedText);
        Assert.Equal("typed", client.Submissions[0].InputSource);
        Assert.Equal(1, client.Submissions[1].ExpectedSessionVersion);
        Assert.Equal(2, client.Submissions[2].ExpectedSessionVersion);
    }

    [Fact]
    public async Task PolicyBlockedSubmissionKeepsTextAndAllowsCorrectedRetry()
    {
        var client = new FakeReviewClient(
            new EllaiUtteranceSubmitResult(EllaiClientOperationStatus.PolicyBlocked, null));
        using var host = await CreateHost(client, CreateSession(version: 1), "private report text");

        await InvokeAsync(host, "OnSend");

        Assert.Equal("private report text", GetField<string?>(host, "InputText"));
        Assert.True(GetProperty<bool>(host, "CanSubmitText"));
        Assert.Contains("remove any sensitive details", GetField<string?>(host, "StatusText"));
        Assert.Single(client.Submissions);
    }

    [Fact]
    public async Task SubmitConflictRebasesVersionBeforeStartingANewAttempt()
    {
        var client = new FakeReviewClient(
            new EllaiUtteranceSubmitResult(EllaiClientOperationStatus.Conflict, CreateSession(version: 2)),
            new EllaiUtteranceSubmitResult(
                EllaiClientOperationStatus.Accepted,
                CreateSession(version: 3)));
        using var host = await CreateHost(client, CreateSession(version: 1), "preserved question");

        await InvokeAsync(host, "OnSend");

        Assert.Equal("preserved question", GetField<string?>(host, "InputText"));
        Assert.Equal(2, GetField<EllaiSessionView?>(host, "_sessionView")?.SessionVersion);
        Assert.True(GetProperty<bool>(host, "CanSubmitText"));
        Assert.Contains("Review it, then send again", GetField<string?>(host, "StatusText"));

        await InvokeAsync(host, "OnSend");

        Assert.NotEqual(client.Submissions[0].ClientUtteranceId, client.Submissions[1].ClientUtteranceId);
        Assert.Equal(2, client.Submissions[1].ExpectedSessionVersion);
        Assert.Equal("preserved question", client.Submissions[1].FinalizedText);
    }

    [Fact]
    public async Task SubmitConflictWithoutCurrentViewKeepsTextAndBlocksResend()
    {
        var client = new FakeReviewClient(
            new EllaiUtteranceSubmitResult(EllaiClientOperationStatus.Conflict, null));
        using var host = await CreateHost(client, CreateSession(version: 1), "preserved question");

        await InvokeAsync(host, "OnSend");

        Assert.Equal("preserved question", GetField<string?>(host, "InputText"));
        Assert.False(GetProperty<bool>(host, "CanSubmitText"));
        Assert.Equal(1, GetField<EllaiSessionView?>(host, "_sessionView")?.SessionVersion);
        Assert.Contains("remote state may still be active", GetField<string?>(host, "StatusText"));
        Assert.Single(client.Submissions);
    }

    [Fact]
    public async Task CancelConflictRefreshesVersionAndOnlyConfirmedCancelEndsSession()
    {
        var first = CreateSession(version: 1);
        var refreshed = CreateSession(version: 2);
        var client = new FakeReviewClient()
        {
            CancelResults =
                new Queue<EllaiSessionCancelResult>(new[]
                {
                    new EllaiSessionCancelResult(EllaiClientOperationStatus.Conflict, refreshed),
                    new EllaiSessionCancelResult(EllaiClientOperationStatus.Cancelled),
                }),
        };
        using var host = await CreateHost(client, first, "preserved draft");
        SetProperty(host, "Bonboarding", CreateBonboardingService());
        SetProperty(host, "Launcher", new EllaiLauncher());
        SetField(host, "IsActive", true);
        SetField(host, "ShowComposer", true);

        await InvokeAsync(host, "OnCancel");

        Assert.Equal("preserved draft", GetField<string?>(host, "InputText"));
        Assert.Equal(refreshed, GetField<EllaiSessionView?>(host, "_sessionView"));
        Assert.True(GetField<bool>(host, "IsActive"));
        Assert.True(GetField<bool>(host, "ShowComposer"));
        Assert.True(GetField<bool>(host, "_sessionVersionConflict"));
        Assert.False(GetField<bool>(host, "_cancelOutcomeUnknown"));
        Assert.Contains("Press Cancel again", GetField<string?>(host, "StatusText"));
        Assert.Single(client.Cancellations);
        Assert.Equal(("session-1", 1L), client.Cancellations[0]);

        await InvokeAsync(host, "OnCancel");

        Assert.Equal(2, client.Cancellations.Count);
        Assert.Equal(2, client.Cancellations[1].ExpectedSessionVersion);
        Assert.Null(GetField<EllaiSessionView?>(host, "_sessionView"));
        Assert.False(GetField<bool>(host, "ShowComposer"));
        Assert.Equal("Ellai session ended.", GetField<string?>(host, "StatusText"));
    }

    [Fact]
    public async Task CancelDuringPendingUtteranceCancelsRequestThenCancelsServerSession()
    {
        var enteredSubmit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeReviewClient
        {
            SubmitHandler = async (_, cancellationToken) =>
            {
                enteredSubmit.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return new EllaiUtteranceSubmitResult(EllaiClientOperationStatus.Unavailable, null);
            },
            CancelResults = new Queue<EllaiSessionCancelResult>(new[]
            {
                new EllaiSessionCancelResult(EllaiClientOperationStatus.Cancelled),
            }),
        };
        using var host = await CreateHost(client, CreateSession(version: 1), "keep this draft");
        SetProperty(host, "Bonboarding", CreateBonboardingService());

        var sending = InvokeTask(host, "OnSend");
        await enteredSubmit.Task;
        await InvokeAsync(host, "OnCancel");
        await sending;

        Assert.Single(client.Submissions);
        Assert.Single(client.Cancellations);
        Assert.Null(GetField<EllaiSessionView?>(host, "_sessionView"));
        Assert.Equal("Ellai session ended.", GetField<string?>(host, "StatusText"));
    }

    [Fact]
    public async Task CancelDuringStartCancelsLateCreatedSessionBeforeHiding()
    {
        var enteredStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeReviewClient
        {
            StartHandler = async (_, _) =>
            {
                enteredStart.TrySetResult();
                await finishStart.Task;
                return new EllaiSessionStartResult(
                    EllaiClientOperationStatus.Accepted,
                    CreateSession(version: 1));
            },
            CancelResults = new Queue<EllaiSessionCancelResult>(new[]
            {
                new EllaiSessionCancelResult(EllaiClientOperationStatus.Cancelled),
            }),
        };
        using var host = await CreateHost(client, CreateSession(version: 1), "");
        SetField(host, "_sessionView", null);
        SetField(host, "ShowComposer", false);
        SetProperty(host, "Bonboarding", CreateBonboardingService());

        var starting = InvokeTask(host, "SelectModeAsync", EllaiMode.Ask);
        await enteredStart.Task;
        await InvokeAsync(host, "OnCancel");
        finishStart.TrySetResult();
        await starting;

        Assert.Single(client.Cancellations);
        Assert.Null(GetField<EllaiSessionView?>(host, "_sessionView"));
        Assert.False(GetField<bool>(host, "ShowComposer"));
        Assert.Equal("Ellai session ended.", GetField<string?>(host, "StatusText"));
    }

    [Fact]
    public void PositionResolveClampsInsideViewport()
    {
        var inside = EllaiComposerPosition.Resolve(new EllaiComposerPosition(100, 100), 1280, 800);
        Assert.Equal(100, inside.X);
        Assert.Equal(100, inside.Y);

        var outside = EllaiComposerPosition.Resolve(new EllaiComposerPosition(-50, 2000), 1280, 800);
        Assert.Equal(0, outside.X);
        Assert.Equal(640, outside.Y);

        var tiny = EllaiComposerPosition.Resolve(new EllaiComposerPosition(500, 500), 100, 100);
        Assert.Equal(0, tiny.X);
        Assert.Equal(0, tiny.Y);
    }

    [Fact]
    public void PositionMoveAndResetBehave()
    {
        var moved = EllaiComposerPosition.Default.Move(24, -24);
        Assert.Equal(EllaiComposerPosition.Default.X + 24, moved.X);

        var reset = EllaiComposerPosition.Default;
        Assert.Equal(0, reset.X);
        Assert.Equal(0, reset.Y);
    }

    [Theory]
    [InlineData("Preparing")]
    [InlineData("Discovering")]
    [InlineData("Planning")]
    [InlineData("NeedsInput")]
    [InlineData("Ready")]
    [InlineData("Active")]
    [InlineData("Replanning")]
    [InlineData("Blocked")]
    public void KnownPhasesMapToLabels(string phase)
    {
        Assert.Equal(phase, EllaiStatusPhases.Label(phase));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Chatting")]
    public void UnknownPhasesRenderNothing(string? phase)
    {
        Assert.Null(EllaiStatusPhases.Label(phase));
    }

    private static async Task<string> RenderPublicLayoutAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMudServices();
        services.AddAuthorizationCore();
        services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());
        services.AddSingleton<NavigationManager>(new TestNavigationManager());
        services.AddSingleton<AuthenticationStateProvider>(new UnauthenticatedStateProvider());
        BitBonboarding.AddBitBonboardingServices(services, _ => { });
        BitBonboarding.AddBitBonboardingInMemoryStore(services);
        services.AddSingleton<IBonboardingService>(CreateBonboardingService());
        services.AddSingleton<IEllaiGuidanceControl>(CreateGuidanceControl());
        services.AddScoped<EllaiLauncher>();
        var reviewClient = new FakeReviewClient();
        services.AddSingleton<IEllaiReviewClient>(reviewClient);
        services.AddSingleton(new EllaiEventPoller(reviewClient));

        await using var serviceProvider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(
            serviceProvider,
            serviceProvider.GetRequiredService<ILoggerFactory>());

        RenderFragment publicLayout = builder =>
        {
            builder.OpenComponent<PublicLayout>(0);
            builder.AddAttribute(1, nameof(PublicLayout.ChildContent), (RenderFragment)(content =>
                content.AddContent(0, "Public page")));
            builder.CloseComponent();
        };

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<CascadingAuthenticationState>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(CascadingAuthenticationState.ChildContent)] = publicLayout,
                }));

            return component.ToHtmlString();
        });
    }

    private static async Task<string> RenderComposerAsync(
        string? inputText = "test question",
        string? livePartialTranscript = null,
        bool canUseMicrophone = true,
        bool? canUseRealtimeMicrophone = null,
        EventCallback onStartVoice = default,
        EventCallback onStartRealtimeVoice = default,
        EventCallback onOpenSettings = default,
        bool isLiveSpeaking = true)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMudServices();
        services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());

        await using var serviceProvider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(
            serviceProvider,
            serviceProvider.GetRequiredService<ILoggerFactory>());

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<EllaiCompactComposer>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(EllaiCompactComposer.InputText)] = inputText,
                    [nameof(EllaiCompactComposer.LivePartialTranscript)] = livePartialTranscript,
                    [nameof(EllaiCompactComposer.StatusText)] = "Messaging is unavailable.",
                    [nameof(EllaiCompactComposer.CanSend)] = false,
                    [nameof(EllaiCompactComposer.CanUseMicrophone)] = canUseMicrophone,
                    [nameof(EllaiCompactComposer.CanUseRealtimeMicrophone)] =
                        canUseRealtimeMicrophone ?? canUseMicrophone,
                    [nameof(EllaiCompactComposer.IsLiveSpeaking)] = isLiveSpeaking,
                    [nameof(EllaiCompactComposer.OnStartVoice)] = onStartVoice,
                    [nameof(EllaiCompactComposer.OnStartRealtimeVoice)] = onStartRealtimeVoice,
                    [nameof(EllaiCompactComposer.OnOpenSettings)] = onOpenSettings,
                }));

            return component.ToHtmlString();
        });
    }

    private static EllaiSessionView CreateSession(long version) => new(
        "session-1", EllaiMode.Ask, null, "Active", "scope-1", "consent/1", "caps/1",
        0, 12, null, 0, version);

    private static async Task<TestComponentHost<EllaiGuidanceHost>> CreateHost(
        FakeReviewClient client,
        EllaiSessionView session,
        string inputText)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMudServices();
        services.AddSingleton<IJSRuntime>(new NoOpJsRuntime());
        services.AddSingleton<NavigationManager>(new TestNavigationManager());
        BitBonboarding.AddBitBonboardingServices(services, _ => { });
        BitBonboarding.AddBitBonboardingInMemoryStore(services);
        services.AddSingleton<IBonboardingService>(CreateBonboardingService());
        services.AddSingleton<IEllaiGuidanceControl>(CreateGuidanceControl());
        services.AddSingleton<EllaiLauncher>();
        services.AddSingleton<IEllaiReviewClient>(client);
        var eventPoller = new EllaiEventPoller(client);
        services.AddSingleton(eventPoller);

        var serviceProvider = services.BuildServiceProvider();
        var renderer = new StateTestRenderer(
            serviceProvider,
            serviceProvider.GetRequiredService<ILoggerFactory>());
        var testHost = await renderer.RenderAsync<EllaiGuidanceHost>();
        SetField(testHost, "_sessionView", session);
        SetField(testHost, "InputText", inputText);
        SetField(testHost, "_publicProductHelpConsentGranted", true);
        if (session is not null)
        {
            await renderer.Dispatcher.InvokeAsync(() => eventPoller.TrackSession(session.SessionId));
        }

        return testHost;
    }

    private static async Task InvokeAsync(object target, string methodName, params object?[] parameters)
    {
        await InvokeTask(target, methodName, parameters);
    }

    private static Task InvokeTask(object target, string methodName, params object?[] parameters)
    {
        var testHost = Assert.IsType<TestComponentHost<EllaiGuidanceHost>>(target);
        return testHost.Dispatcher.InvokeAsync(() => InvokeTaskCore(testHost.Component, methodName, parameters));
    }

    private static Task InvokeTaskCore(object target, string methodName, params object?[] parameters)
    {
        var method = target.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing test target method {methodName}.");
        var result = method.Invoke(target, parameters);
        if (result is Task task)
        {
            return task;
        }

        if (method.ReturnType == typeof(void))
        {
            return Task.CompletedTask;
        }

        throw new InvalidOperationException($"Test target {methodName} did not return a task or void.");
    }

    private static T GetField<T>(object target, string fieldName)
    {
        var testHost = Assert.IsType<TestComponentHost<EllaiGuidanceHost>>(target);
        return testHost.Dispatcher.InvokeAsync(() => GetFieldCore<T>(testHost.Component, fieldName))
            .GetAwaiter().GetResult();
    }

    private static T GetFieldCore<T>(object target, string fieldName)
    {
        var field = target.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing test target field {fieldName}.");
        return (T)field.GetValue(target)!;
    }

    private static T GetProperty<T>(object target, string propertyName)
    {
        var testHost = Assert.IsType<TestComponentHost<EllaiGuidanceHost>>(target);
        return testHost.Dispatcher.InvokeAsync(() => GetPropertyCore<T>(testHost.Component, propertyName))
            .GetAwaiter().GetResult();
    }

    private static T GetPropertyCore<T>(object target, string propertyName)
    {
        var property = target.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing test target property {propertyName}.");
        return (T)property.GetValue(target)!;
    }

    private static void SetField(object target, string fieldName, object? value)
    {
        var testHost = Assert.IsType<TestComponentHost<EllaiGuidanceHost>>(target);
        testHost.Dispatcher.InvokeAsync(() => SetFieldCore(testHost.Component, fieldName, value))
            .GetAwaiter().GetResult();
    }

    private static void SetFieldCore(object target, string fieldName, object? value)
    {
        var field = target.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing test target field {fieldName}.");
        field.SetValue(target, value);
    }

    private static void SetProperty(object target, string propertyName, object? value)
    {
        var testHost = Assert.IsType<TestComponentHost<EllaiGuidanceHost>>(target);
        testHost.Dispatcher.InvokeAsync(() => SetPropertyCore(testHost.Component, propertyName, value))
            .GetAwaiter().GetResult();
    }

    private static void SetPropertyCore(object target, string propertyName, object? value)
    {
        var property = target.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing test target property {propertyName}.");
        property.SetValue(target, value);
    }

    private static IEllaiGuidanceControl CreateGuidanceControl() =>
        DispatchProxy.Create<IEllaiGuidanceControl, NoOpGuidanceControlProxy>();

    private static IBonboardingService CreateBonboardingService() =>
        DispatchProxy.Create<IBonboardingService, NoOpBonboardingServiceProxy>();

    private sealed class UnauthenticatedStateProvider : AuthenticationStateProvider
    {
        private static readonly AuthenticationState State = new(
            new ClaimsPrincipal(new ClaimsIdentity()));

        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(State);
    }

    private sealed class NoOpJsRuntime : IJSRuntime
    {
        private static readonly IJSObjectReference Module = new NoOpJsModule();

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeCore<TValue>(identifier);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args) =>
            InvokeCore<TValue>(identifier);

        private static ValueTask<TValue> InvokeCore<TValue>(string identifier)
        {
            if (identifier == "import" && typeof(TValue) == typeof(IJSObjectReference))
            {
                return ValueTask.FromResult((TValue)Module);
            }

            return ValueTask.FromResult(default(TValue)!);
        }
    }

    private sealed class NoOpJsModule : IJSObjectReference
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingJsModule : IJSObjectReference
    {
        public List<string> Invocations { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            Invocations.Add(identifier);
            var result = identifier == "startCapture" && typeof(TValue) == typeof(string)
                ? (TValue)(object)"Microphone active; test capture."
                : default!;
            return ValueTask.FromResult(result);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args) => InvokeAsync<TValue>(identifier, args);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("http://localhost/", "http://localhost/");

        protected override void NavigateToCore(string uri, bool forceLoad) =>
            NotifyLocationChanged(isInterceptedLink: false);

        protected override void NavigateToCore(string uri, NavigationOptions options) =>
            NotifyLocationChanged(isInterceptedLink: false);
    }

#pragma warning disable BL0006 // The minimal test renderer must use Blazor's renderer APIs to exercise a mounted component.
    private sealed class StateTestRenderer(IServiceProvider services, ILoggerFactory loggerFactory)
        : Renderer(services, loggerFactory)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        public async Task<TestComponentHost<TComponent>> RenderAsync<TComponent>()
            where TComponent : IComponent
        {
            TComponent component = default!;
            var componentId = -1;
            await Dispatcher.InvokeAsync(async () =>
            {
                component = (TComponent)InstantiateComponent(typeof(TComponent));
                componentId = AssignRootComponentId(component);
                await RenderRootComponentAsync(componentId);
            });

            return new TestComponentHost<TComponent>(this, component, componentId);
        }

        public void Unmount(int componentId) => RemoveRootComponent(componentId);

        protected override void HandleException(Exception exception) => throw exception;

        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
    }
#pragma warning restore BL0006

    private sealed class TestComponentHost<TComponent>(
        StateTestRenderer renderer,
        TComponent component,
        int componentId) : IDisposable
        where TComponent : IComponent
    {
        public TComponent Component { get; } = component;

        public Dispatcher Dispatcher => renderer.Dispatcher;

        public void Dispose() => renderer.Dispatcher.InvokeAsync(() => renderer.Unmount(componentId))
            .GetAwaiter().GetResult();
    }

    private sealed class FakeReviewClient(params EllaiUtteranceSubmitResult[] submitResults)
        : IEllaiReviewClient
    {
        private readonly Queue<EllaiUtteranceSubmitResult> _submitResults = new(submitResults);

        public List<SubmitUtteranceRequest> Submissions { get; } = [];

        public List<(string SessionId, byte[] Audio, EllaiVoiceCaptureMode Mode)> VoiceTranscriptions { get; } = [];

        public int LiveTranscriptionCalls { get; private set; }

        public Func<string, byte[], EllaiVoiceCaptureMode, CancellationToken,
            Task<EllaiVoiceTranscriptionResult?>>? VoiceTranscriptionHandler { get; init; }

        public Func<string, Stream, Task<EllaiVoiceTranscriptionResult?>>? LiveTranscriptionHandler { get; init; }

        public int ProductHelpConsentGrants { get; private set; }

        public List<(string SessionId, long ExpectedSessionVersion)> Cancellations { get; } = [];

        public int SessionStartCalls { get; private set; }

        public Queue<EllaiSessionCancelResult> CancelResults { get; init; } = new();

        public Func<EllaiMode, CancellationToken, Task<EllaiSessionStartResult>>? StartHandler { get; init; }

        public Func<SubmitUtteranceRequest, CancellationToken, Task<EllaiUtteranceSubmitResult>>? SubmitHandler { get; init; }

        public IReadOnlyList<EllaiApprovedReceiptDto>? ListedReceipts { get; init; } = [];

        public int ApprovalCalls { get; private set; }

        public int ReceiptListCalls { get; private set; }

        public EllaiReviewDto? ActiveReview { get; init; }

        public int ActiveReviewReads { get; private set; }

        public int RejectionCalls { get; private set; }

        public bool RejectionResult { get; init; }

        public Exception? RejectionException { get; init; }

        public Task<bool> GrantVoiceCaptureConsentAsync(string sessionId, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<EllaiVoiceTranscriptionResult?> TranscribeVoiceAsync(
            string sessionId,
            byte[] audio,
            EllaiVoiceCaptureMode mode,
            CancellationToken cancellationToken)
        {
            VoiceTranscriptions.Add((sessionId, audio.ToArray(), mode));
            return VoiceTranscriptionHandler?.Invoke(sessionId, audio, mode, cancellationToken)
                ?? Task.FromResult<EllaiVoiceTranscriptionResult?>(null);
        }

        public Task<EllaiVoiceTranscriptionResult?> TranscribeLiveVoiceStreamAsync(
            string sessionId,
            Stream audio,
            CancellationToken cancellationToken)
        {
            LiveTranscriptionCalls++;
            return LiveTranscriptionHandler?.Invoke(sessionId, audio)
                ?? Task.FromResult<EllaiVoiceTranscriptionResult?>(null);
        }

        public Task<EllaiSessionStartResult> StartSessionAsync(EllaiMode mode, CancellationToken cancellationToken)
        {
            SessionStartCalls++;
            return StartHandler?.Invoke(mode, cancellationToken)
                ?? Task.FromResult(new EllaiSessionStartResult(EllaiClientOperationStatus.OutcomeUnknown));
        }

        public Task<EllaiUtteranceSubmitResult> SubmitUtteranceAsync(
            SubmitUtteranceRequest request,
            CancellationToken cancellationToken)
        {
            Submissions.Add(request);
            if (SubmitHandler is not null)
            {
                return SubmitHandler(request, cancellationToken);
            }

            return Task.FromResult(_submitResults.Dequeue());
        }

        public Task<bool> GrantPublicProductHelpConsentAsync(
            string sessionId, CancellationToken cancellationToken)
        {
            ProductHelpConsentGrants++;
            return Task.FromResult(true);
        }

        public Task<EllaiSessionCancelResult> CancelSessionAsync(
            string sessionId,
            long expectedSessionVersion,
            CancellationToken cancellationToken)
        {
            Cancellations.Add((sessionId, expectedSessionVersion));
            return Task.FromResult(CancelResults.Dequeue());
        }

        public Task<EllaiSessionEventPageDto?> PollEventsAsync(
            string sessionId,
            long cursor,
            int waitSeconds,
            CancellationToken cancellationToken) =>
            Task.FromResult<EllaiSessionEventPageDto?>(null);

        public Task<EllaiReviewDto?> GetActiveReviewAsync(
            string sessionId,
            CancellationToken cancellationToken)
        {
            ActiveReviewReads++;
            return Task.FromResult(ActiveReview);
        }

        public Task<bool> ReportUiObservationAsync(
            string sessionId,
            UiObservationReport report,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<IReadOnlyList<EllaiApprovedReceiptDto>?> ListReceiptsAsync(
            string sessionId,
            CancellationToken cancellationToken)
        {
            ReceiptListCalls++;
            return Task.FromResult(ListedReceipts);
        }

        public Task<IReadOnlyList<EllaiApprovedReceiptDto>?> ApproveSelectedAsync(
            string sessionId,
            string requestId,
            IReadOnlyList<string> actionIds,
            CancellationToken cancellationToken)
        {
            ApprovalCalls++;
            return Task.FromResult<IReadOnlyList<EllaiApprovedReceiptDto>?>(null);
        }

        public Task<bool> RejectReviewAsync(
            string sessionId,
            string requestId,
            CancellationToken cancellationToken)
        {
            RejectionCalls++;
            if (RejectionException is not null)
            {
                return Task.FromException<bool>(RejectionException);
            }

            return Task.FromResult(RejectionResult);
        }
    }

    public class NoOpBonboardingServiceProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.ReturnType == typeof(void)
                ? null
                : targetMethod?.ReturnType == typeof(Task)
                    ? Task.CompletedTask
                : targetMethod?.ReturnType.IsValueType == true
                    ? Activator.CreateInstance(targetMethod.ReturnType)
                    : null;
    }

    public class NoOpGuidanceControlProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.ReturnType == typeof(Task)
                ? Task.CompletedTask
                : targetMethod?.ReturnType == typeof(Task<GuidanceSnapshot>)
                    ? Task.FromResult<GuidanceSnapshot>(null!)
                    : targetMethod?.ReturnType == typeof(Task<GuidanceSnapshot?>)
                        ? Task.FromResult<GuidanceSnapshot?>(null)
                        : targetMethod?.ReturnType.IsValueType == true
                            ? Activator.CreateInstance(targetMethod.ReturnType)
                            : null;
    }
}
