using System.Net;
using System.Net.Http.Headers;
using SwiftAid.Client.Intake;

namespace SwiftAid.Client.Tests;

public sealed class GuestIntakeHttpClientTests
{
    [Fact]
    public async Task GetCountriesAsync_PropagatesCallerCancellationWhileReadingResponseBody()
    {
        var body = new BlockingReadStream();
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(body)
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var httpClient = new HttpClient(new ImmediateResponseHandler(response))
        {
            BaseAddress = new Uri("https://gateway.test/")
        };
        var transport = new GuestIntakeHttpClient(httpClient);
        using var cancellation = new CancellationTokenSource();

        var request = transport.GetCountriesAsync(cancellation.Token);
        await body.ReadStarted.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(cancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task GetCountriesAsync_MapsBodyReadDeadlineToRetryableTransportException()
    {
        var body = new BlockingReadStream();
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(body)
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var httpClient = new HttpClient(new ImmediateResponseHandler(response))
        {
            BaseAddress = new Uri("https://gateway.test/")
        };
        var transport = new GuestIntakeHttpClient(httpClient);

        var request = transport.GetCountriesAsync(CancellationToken.None);
        await body.ReadStarted.WaitAsync(TimeSpan.FromSeconds(2));

        var exception = await Assert.ThrowsAsync<GuestIntakeTransportException>(() => request)
            .WaitAsync(TimeSpan.FromSeconds(8));

        Assert.True(exception.IsRetryable);
    }

    private sealed class ImmediateResponseHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(response);
    }

    private sealed class BlockingReadStream : Stream
    {
        private readonly TaskCompletionSource<bool> _readStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ReadStarted => _readStarted.Task;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) => WaitForCancellationAsync(cancellationToken);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            new(WaitForCancellationAsync(cancellationToken));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private async Task<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            _readStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
