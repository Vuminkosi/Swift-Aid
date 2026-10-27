using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using SwiftAid.Client.Attachments;
using SwiftAid.Client.LocalStore;

namespace SwiftAid.Client.Tests;

public sealed class OriginalAudioStoreTests
{
    [Fact]
    public async Task CompletedCapturePreservesExactBytesAndHash()
    {
        await using var fixture = new AudioStoreFixture();
        var audioId = await fixture.Store.BeginCaptureAsync("workspace-a", CancellationToken.None);
        var first = Encoding.UTF8.GetBytes("original voice bytes ");
        var second = new byte[] { 0, 1, 2, 255, 128 };
        var expected = first.Concat(second).ToArray();

        await fixture.Store.AppendSegmentAsync(audioId, first, TimeSpan.FromSeconds(15), CancellationToken.None);
        await fixture.Store.AppendSegmentAsync(audioId, second, TimeSpan.FromSeconds(8), CancellationToken.None);
        var ready = await fixture.Store.CompleteCaptureAsync(audioId, CancellationToken.None);

        Assert.Equal(LocalAudioState.Ready, ready.State);
        Assert.Equal(expected.LongLength, ready.LengthBytes);
        Assert.Equal(TimeSpan.FromSeconds(23), ready.Duration);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)), ready.Sha256);

        var opened = await fixture.Store.OpenOriginalAsync(audioId, CancellationToken.None);
        Assert.NotNull(opened);
        await using var original = opened;
        using var actual = new MemoryStream();
        await original.CopyToAsync(actual);
        Assert.Equal(expected, actual.ToArray());
    }

    [Fact]
    public async Task InterruptedCaptureCanRecoverAndResumeWithoutChangingOriginalBytes()
    {
        await using var fixture = new AudioStoreFixture();
        var audioId = await fixture.Store.BeginCaptureAsync("workspace-a", CancellationToken.None);
        var first = Encoding.UTF8.GetBytes("first capture segment");
        var second = Encoding.UTF8.GetBytes("second capture segment");
        await fixture.Store.AppendSegmentAsync(audioId, first, TimeSpan.FromSeconds(12), CancellationToken.None);

        var interrupted = await fixture.Store.MarkInterruptedAsync(audioId, CancellationToken.None);
        var recovered = await fixture.Store.RecoverInterruptedAsync(audioId, CancellationToken.None);
        Assert.Equal(LocalAudioState.Interrupted, interrupted.State);
        Assert.Equal(LocalAudioState.Interrupted, recovered.State);

        var resumed = await fixture.Store.ResumeCaptureAsync(audioId, CancellationToken.None);
        Assert.Equal(LocalAudioState.Recording, resumed.State);
        await fixture.Store.AppendSegmentAsync(audioId, second, TimeSpan.FromSeconds(10), CancellationToken.None);
        await fixture.Store.CompleteCaptureAsync(audioId, CancellationToken.None);

        var opened = await fixture.Store.OpenOriginalAsync(audioId, CancellationToken.None);
        Assert.NotNull(opened);
        await using var original = opened;
        using var actual = new MemoryStream();
        await original.CopyToAsync(actual);
        Assert.Equal(first.Concat(second), actual.ToArray());
    }

    [Fact]
    public async Task ProcessRestartRecoveryMarksOpenCaptureInterrupted()
    {
        await using var fixture = new AudioStoreFixture();
        var audioId = await fixture.Store.BeginCaptureAsync("workspace-a", CancellationToken.None);
        await fixture.Store.AppendSegmentAsync(
            audioId,
            Encoding.UTF8.GetBytes("recoverable segment"),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        // Startup recovery is invoked for an item whose owning recorder process no longer exists.
        var recovered = await fixture.Store.RecoverInterruptedAsync(audioId, CancellationToken.None);

        Assert.Equal(LocalAudioState.Interrupted, recovered.State);
        await Assert.ThrowsAsync<LocalAudioStateException>(() =>
            fixture.Store.OpenOriginalAsync(audioId, CancellationToken.None));
    }

    [Fact]
    public async Task LimitsRejectOversizeAndOverDurationSegmentsWithoutPromotingThem()
    {
        await using var fixture = new AudioStoreFixture();
        var audioId = await fixture.Store.BeginCaptureAsync("workspace-a", CancellationToken.None);
        await fixture.Store.AppendSegmentAsync(
            audioId,
            new byte[] { 42 },
            TimeSpan.FromSeconds(119),
            CancellationToken.None);

        await Assert.ThrowsAsync<LocalAudioLimitExceededException>(() => fixture.Store.AppendSegmentAsync(
            audioId,
            new byte[] { 43 },
            TimeSpan.FromSeconds(2),
            CancellationToken.None));

        await Assert.ThrowsAsync<LocalAudioLimitExceededException>(() => fixture.Store.AppendSegmentAsync(
            audioId,
            new byte[LocalAudioStore.MaximumAudioBytes],
            TimeSpan.FromSeconds(1),
            CancellationToken.None));

        var status = await fixture.Store.GetStatusAsync(audioId, CancellationToken.None);
        Assert.NotNull(status);
        Assert.Equal(LocalAudioState.Recording, status.State);
        Assert.Equal(1, status.LengthBytes);
        Assert.Equal(TimeSpan.FromSeconds(119), status.Duration);
        Assert.Null(status.Sha256);
        await Assert.ThrowsAsync<LocalAudioStateException>(() =>
            fixture.Store.OpenOriginalAsync(audioId, CancellationToken.None));
    }

    [Fact]
    public async Task ProtectedKeyFailureCreatesNoAudioArtifact()
    {
        await using var fixture = new AudioStoreFixture(keyUnavailable: true);

        await Assert.ThrowsAsync<LocalAudioKeyUnavailableException>(() =>
            fixture.Store.BeginCaptureAsync("workspace-a", CancellationToken.None));

        Assert.False(Directory.Exists(Path.Combine(fixture.AppPrivateRoot, "swiftaid-audio-v1")));
    }

    [Fact]
    public async Task QueuedStatusQueryRechecksAccessAfterGateAndReturnsNoMetadataWhenRevoked()
    {
        await using var fixture = new AudioStoreFixture();
        var audioId = await fixture.Store.BeginCaptureAsync("workspace-a", CancellationToken.None);
        await fixture.Store.AppendSegmentAsync(
            audioId,
            Encoding.UTF8.GetBytes("private status metadata"),
            TimeSpan.FromSeconds(3),
            CancellationToken.None);
        await fixture.Store.CompleteCaptureAsync(audioId, CancellationToken.None);

        var gateField = typeof(LocalAudioStore).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(gateField);
        var gate = Assert.IsType<SemaphoreSlim>(gateField.GetValue(fixture.Store));
        await gate.WaitAsync();
        var gateHeld = true;
        try
        {
            // Async methods run synchronously to the first incomplete await. With the gate held,
            // this request is queued before access is revoked.
            var queuedStatus = fixture.Store.GetStatusAsync(audioId, CancellationToken.None);
            fixture.Access.Current = fixture.Access.Current with { IsActive = false };

            gate.Release();
            gateHeld = false;

            await Assert.ThrowsAsync<LocalAudioAccessDeniedException>(() => queuedStatus);
        }
        finally
        {
            if (gateHeld) gate.Release();
        }
    }

    [Fact]
    public async Task StatusQueryRechecksAccessAfterAsyncManifestReadBeforeReturningMetadata()
    {
        await using var fixture = new AudioStoreFixture();
        var audioId = await fixture.Store.BeginCaptureAsync("workspace-a", CancellationToken.None);
        await fixture.Store.AppendSegmentAsync(
            audioId,
            Encoding.UTF8.GetBytes("private status metadata"),
            TimeSpan.FromSeconds(3),
            CancellationToken.None);
        await fixture.Store.CompleteCaptureAsync(audioId, CancellationToken.None);

        var blockingKeyStore = new BlockingKeyStore();
        var store = new LocalAudioStore(
            fixture.Access,
            blockingKeyStore,
            new TestPathProvider(fixture.AppPrivateRoot),
            TimeProvider.System);
        var pendingStatus = store.GetStatusAsync(audioId, CancellationToken.None);

        await blockingKeyStore.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Access.Current = fixture.Access.Current with { IsActive = false };
        blockingKeyStore.AllowRead.TrySetResult();

        await Assert.ThrowsAsync<LocalAudioAccessDeniedException>(() => pendingStatus);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("epoch")]
    [InlineData("workspace")]
    public async Task AccountEpochOrWorkspaceChangeDeniesOriginalPlayback(string changedValue)
    {
        await using var fixture = new AudioStoreFixture();
        var audioId = await fixture.Store.BeginCaptureAsync("workspace-a", CancellationToken.None);
        await fixture.Store.AppendSegmentAsync(
            audioId,
            Encoding.UTF8.GetBytes("private original bytes"),
            TimeSpan.FromSeconds(4),
            CancellationToken.None);
        await fixture.Store.CompleteCaptureAsync(audioId, CancellationToken.None);

        fixture.Access.Current = changedValue switch
        {
            "actor" => fixture.CreateAccess(actorId: "actor-b"),
            "epoch" => fixture.CreateAccess(accessEpoch: 2),
            "workspace" => fixture.CreateAccess(permittedWorkspaceIds: ["workspace-b"]),
            _ => throw new InvalidOperationException("Unexpected test case.")
        };

        await Assert.ThrowsAsync<LocalAudioAccessDeniedException>(() =>
            fixture.Store.OpenOriginalAsync(audioId, CancellationToken.None));
    }

    [Fact]
    public async Task SegmentFilesNeverContainTheUnencryptedOriginal()
    {
        await using var fixture = new AudioStoreFixture();
        var audioId = await fixture.Store.BeginCaptureAsync("workspace-a", CancellationToken.None);
        var originalBytes = Encoding.UTF8.GetBytes("do not leave this audio unencrypted");
        await fixture.Store.AppendSegmentAsync(audioId, originalBytes, TimeSpan.FromSeconds(2), CancellationToken.None);

        var files = Directory.GetFiles(
            Path.Combine(fixture.AppPrivateRoot, "swiftaid-audio-v1", audioId.ToString("N")),
            "*",
            SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        foreach (var path in files)
        {
            var storedBytes = await File.ReadAllBytesAsync(path);
            Assert.DoesNotContain(originalBytes, storedBytes);
        }
    }

    private sealed class AudioStoreFixture : IAsyncDisposable
    {
        public AudioStoreFixture(bool keyUnavailable = false)
        {
            AppPrivateRoot = Path.Combine(Path.GetTempPath(), $"swiftaid-audio-store-{Guid.NewGuid():N}");
            Access = new MutableAccessProvider { Current = CreateAccess() };
            Store = new LocalAudioStore(
                Access,
                new TestKeyStore(keyUnavailable),
                new TestPathProvider(AppPrivateRoot),
                TimeProvider.System);
        }

        public string AppPrivateRoot { get; }

        public MutableAccessProvider Access { get; }

        public LocalAudioStore Store { get; }

        public LocalDraftAccessContext CreateAccess(
            string actorId = "actor-a",
            long accessEpoch = 1,
            IReadOnlyCollection<string>? permittedWorkspaceIds = null) => new(
                actorId,
                IsProvisioned: true,
                IsActive: true,
                accessEpoch,
                DateTimeOffset.UtcNow.AddHours(1),
                permittedWorkspaceIds ?? ["workspace-a"]);

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(AppPrivateRoot))
            {
                Directory.Delete(AppPrivateRoot, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class MutableAccessProvider : ILocalDraftAccessContextProvider
    {
        public required LocalDraftAccessContext Current { get; set; }

        public ValueTask<LocalDraftAccessContext?> GetCurrentAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<LocalDraftAccessContext?>(Current);
        }
    }

    private sealed class TestKeyStore(bool keyUnavailable) : ILocalDraftKeyStore
    {
        public ValueTask<byte[]> GetOrCreateKeyAsync(string actorId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (keyUnavailable) throw new LocalDraftKeyUnavailableException();
            return ValueTask.FromResult(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
        }
    }

    private sealed class BlockingKeyStore : ILocalDraftKeyStore
    {
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<byte[]> GetOrCreateKeyAsync(string actorId, CancellationToken cancellationToken)
        {
            ReadStarted.TrySetResult();
            await AllowRead.Task.WaitAsync(cancellationToken);
            return Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        }
    }

    private sealed class TestPathProvider(string appPrivateRoot) : ILocalAudioPathProvider
    {
        public string GetAppPrivateDirectory() => appPrivateRoot;
    }
}
