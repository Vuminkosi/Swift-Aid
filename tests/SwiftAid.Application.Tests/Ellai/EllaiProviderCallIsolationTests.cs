using System.Runtime.CompilerServices;
using SwiftAid.Intelligence.Meta;

namespace SwiftAid.Application.Tests.Ellai;

public sealed class EllaiProviderCallIsolationTests
{
    [Fact]
    public async Task CancelledStreamRetainsItsSlotUntilProviderExits()
    {
        var isolation = new EllaiProviderCallIsolation(1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var firstCancellation = new CancellationTokenSource();
        async IAsyncEnumerable<int> Held([EnumeratorCancellation] CancellationToken ignored = default)
        {
            started.TrySetResult();
            try { await release.Task; yield return 1; }
            finally { finished.TrySetResult(); }
        }
        async IAsyncEnumerable<int> Immediate()
        {
            await Task.Yield();
            yield return 2;
        }

        await using var first = isolation.StreamAsync(token => Held(token).GetAsyncEnumerator(token), firstCancellation.Token).GetAsyncEnumerator();
        var firstMove = first.MoveNextAsync().AsTask();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            firstCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstMove.WaitAsync(TimeSpan.FromSeconds(5)));
            using var secondCancellation = new CancellationTokenSource();
            await using var second = isolation.StreamAsync(token => Immediate().GetAsyncEnumerator(token), secondCancellation.Token).GetAsyncEnumerator();
            var secondMove = second.MoveNextAsync().AsTask();
            Assert.False(secondMove.IsCompleted);
            secondCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondMove.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(finished.Task.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using var third = isolation.StreamAsync(token => Immediate().GetAsyncEnumerator(token), default).GetAsyncEnumerator();
        Assert.True(await third.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, third.Current);
    }
}
