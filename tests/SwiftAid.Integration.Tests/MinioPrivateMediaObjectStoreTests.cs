using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Minio;
using SwiftAid.Application.Abstractions;
using SwiftAid.Application.Attachments;
using SwiftAid.Infrastructure.Configuration;
using SwiftAid.Infrastructure.Storage;

namespace SwiftAid.Integration.Tests;

/// <summary>
/// Opt-in tests against the isolated local provider fixture. These exercise the private MinIO
/// range adapter only; they do not verify upload authorization, resumable offsets, or promotion.
/// </summary>
public sealed class MinioPrivateMediaObjectStoreTests : IClassFixture<ProviderServicesFixture>
{
    private readonly ProviderServicesFixture _fixture;

    public MinioPrivateMediaObjectStoreTests(ProviderServicesFixture fixture) => _fixture = fixture;

    [ProviderFixtureFact]
    public async Task OpenReadAsyncReturnsOnlyTheRequestedRangeAndMetadata()
    {
        var context = await CreateStoredObjectAsync(Enumerable.Range(0, 40).Select(value => (byte)value).ToArray());
        await using var services = context.Services;
        var range = new AttachmentByteRange(7, 15);

        await using var result = await context.Store.OpenReadAsync(context.Reference, range, CancellationToken.None);

        Assert.NotNull(result);
        using var output = new MemoryStream();
        await result.Content.CopyToAsync(output);

        Assert.Equal(Enumerable.Range(7, 9).Select(value => (byte)value), output.ToArray());
        Assert.Equal("application/octet-stream", result.ContentType);
        Assert.Equal(range, result.Range);
        Assert.Equal(context.Payload.Length, result.TotalLength);
    }

    [ProviderFixtureFact]
    public async Task OpenReadAsyncRejectsRangesOutsideTheAuthoritativeObjectLength()
    {
        var context = await CreateStoredObjectAsync([1, 2, 3, 4]);
        await using var services = context.Services;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await context.Store.OpenReadAsync(context.Reference, new AttachmentByteRange(3, 4), CancellationToken.None));
    }

    [ProviderFixtureFact]
    public async Task OpenReadAsyncReturnsNullForMissingObjects()
    {
        await using var services = _fixture.CreateServiceProvider();
        var store = CreateStore(services);

        var result = await store.OpenReadAsync(
            new ObjectReference(Guid.NewGuid()),
            new AttachmentByteRange(0, 0),
            CancellationToken.None);

        Assert.Null(result);
    }

    [ProviderFixtureFact]
    public async Task OpenReadAsyncHonorsCancellationBeforeProviderAccess()
    {
        await using var services = _fixture.CreateServiceProvider();
        var store = CreateStore(services);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await store.OpenReadAsync(
                new ObjectReference(Guid.NewGuid()),
                new AttachmentByteRange(0, 0),
                cancellation.Token));
    }

    [Fact]
    public async Task OpenReadAsyncStopsAnInFlightProviderTransferWhenCancelled()
    {
        await using var context = CreateHttpStore(
            Enumerable.Range(0, 16).Select(value => (byte)value).ToArray(),
            blockGetUntilCancelled: true);
        using var cancellation = new CancellationTokenSource();

        await using var result = await context.Store.OpenReadAsync(
            new ObjectReference(Guid.NewGuid()),
            new AttachmentByteRange(0, 7),
            cancellation.Token);
        Assert.NotNull(result);
        await context.Handler.GetStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            using var output = new MemoryStream();
            await result.Content.CopyToAsync(output);
        });
    }

    [Fact]
    public async Task OpenReadAsyncFailsWhenProviderReturnsAShortRange()
    {
        var context = CreateHttpStore(
            Enumerable.Range(0, 16).Select(value => (byte)value).ToArray(),
            responseLengthAdjustment: -1);
        await using var minio = context;
        var reference = new ObjectReference(Guid.NewGuid());
        var range = new AttachmentByteRange(4, 10);

        await using var result = await minio.Store.OpenReadAsync(reference, range, CancellationToken.None);
        Assert.NotNull(result);

        using var output = new MemoryStream();
        var failure = await Record.ExceptionAsync(() => result.Content.CopyToAsync(output));

        Assert.NotNull(failure);
        Assert.Equal(range.Length - 1, output.Length);
        Assert.Equal($"bytes={range.Start}-{range.EndInclusive}", minio.Handler.RequestedRange);
    }

    [Fact]
    public async Task OpenReadAsyncRejectsExcessProviderBytesWithoutExposingThem()
    {
        var payload = Enumerable.Range(0, 80).Select(value => (byte)value).ToArray();
        await using var context = CreateHttpStore(payload, responseLengthAdjustment: 1);
        var range = new AttachmentByteRange(22, 38);

        await using var result = await context.Store.OpenReadAsync(
            new ObjectReference(Guid.NewGuid()),
            range,
            CancellationToken.None);
        Assert.NotNull(result);
        using var output = new MemoryStream();
        var failure = await Record.ExceptionAsync(() => result.Content.CopyToAsync(output));

        Assert.Equal(payload[22..39], output.ToArray());
        Assert.NotNull(failure);
        Assert.Equal(range.Length, output.Length);
        Assert.Equal($"bytes={range.Start}-{range.EndInclusive}", context.Handler.RequestedRange);
        Assert.Equal(range.Length + 1, context.Handler.BytesReturned);
    }

    private async Task<StoredObjectContext> CreateStoredObjectAsync(byte[] payload)
    {
        var services = _fixture.CreateServiceProvider();
        var storage = services.GetRequiredService<IObjectStorage>();
        await using var input = new MemoryStream(payload, writable: false);
        var stored = await storage.StoreAsync(input, "application/octet-stream", CancellationToken.None);
        Assert.Equal(ObjectStorageStatus.Succeeded, stored.Status);
        Assert.NotNull(stored.Reference);

        return new StoredObjectContext(
            services,
            CreateStore(services),
            stored.Reference!,
            payload);
    }

    private static MinioPrivateMediaObjectStore CreateStore(
        IServiceProvider services) =>
        new(
            services.GetRequiredService<IMinioClient>(),
            services.GetRequiredService<IOptions<MinioOptions>>(),
            services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MinioPrivateMediaObjectStore>>());

    private sealed record StoredObjectContext(
        ServiceProvider Services,
        MinioPrivateMediaObjectStore Store,
        ObjectReference Reference,
        byte[] Payload);

    private static HttpMinioContext CreateHttpStore(
        byte[] payload,
        int responseLengthAdjustment = 0,
        bool blockGetUntilCancelled = false)
    {
        var handler = new MinioHttpHandler(payload, responseLengthAdjustment, blockGetUntilCancelled);
        var httpClient = new HttpClient(handler, disposeHandler: true);
        var client = new MinioClient()
            .WithEndpoint("127.0.0.1", 9000)
            .WithCredentials("swiftaid-test", "not-a-secret-test-value")
            .WithSSL(false)
            .WithHttpClient(httpClient, false)
            .Build();
        var options = Options.Create(new MinioOptions
        {
            Enabled = true,
            Endpoint = "http://127.0.0.1:9000",
            Bucket = "swiftaid-private-test",
            MaxObjectSizeBytes = 1024,
            AccessKey = "swiftaid-test",
            SecretKey = "not-a-secret-test-value",
            UseSsl = false
        });
        var store = new MinioPrivateMediaObjectStore(client, options, NullLogger<MinioPrivateMediaObjectStore>.Instance);
        return new HttpMinioContext(client, httpClient, handler, store);
    }

    private sealed record HttpMinioContext(
        IMinioClient Client,
        HttpClient HttpClient,
        MinioHttpHandler Handler,
        MinioPrivateMediaObjectStore Store) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            Client.Dispose();
            HttpClient.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MinioHttpHandler(
        byte[] payload,
        int responseLengthAdjustment,
        bool blockGetUntilCancelled) : HttpMessageHandler
    {
        public string? RequestedRange { get; private set; }

        public long BytesReturned { get; private set; }

        public TaskCompletionSource<bool> GetStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Head)
            {
                var stat = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([])
                };
                stat.Headers.ETag = new EntityTagHeaderValue("\"swiftaid-test-etag\"");
                stat.Content.Headers.LastModified = DateTimeOffset.UtcNow;
                stat.Content.Headers.ContentLength = payload.LongLength;
                stat.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                return stat;
            }

            var rangeHeader = request.Headers.Range;
            if (request.Method != HttpMethod.Get || rangeHeader is null || rangeHeader.Ranges.Count != 1)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            }

            var requestedRange = rangeHeader.Ranges.Single();
            var start = requestedRange.From ?? 0;
            var endInclusive = requestedRange.To ?? (payload.LongLength - 1);
            if (start < 0 || endInclusive < start || endInclusive >= payload.LongLength)
            {
                return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
            }

            RequestedRange = rangeHeader.ToString();
            GetStarted.TrySetResult(true);
            if (blockGetUntilCancelled)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            var requestedLength = endInclusive - start + 1;
            var maxAvailableLength = payload.LongLength - start;
            var responseLength = checked((int)Math.Clamp(
                requestedLength + responseLengthAdjustment,
                0,
                maxAvailableLength));
            var bytes = payload.AsSpan(checked((int)start), responseLength).ToArray();
            BytesReturned = bytes.LongLength;

            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(bytes)
            };
            response.Headers.ETag = new EntityTagHeaderValue("\"swiftaid-test-etag\"");
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var returnedEndInclusive = responseLength == 0
                ? endInclusive
                : start + responseLength - 1;
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                start,
                returnedEndInclusive,
                payload.LongLength);
            return response;
        }
    }
}
