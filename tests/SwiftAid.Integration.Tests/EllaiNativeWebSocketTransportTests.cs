using System.IO.Pipelines;
using System.Net;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SwiftAid.UI.Ellai;

namespace SwiftAid.Integration.Tests;

public sealed class EllaiNativeWebSocketTransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PcmAndSilenceStreamBeforeEofThroughExistingAuthenticationHandlers(bool guest)
    {
        await using var server = await Server.StartAsync(false);
        using var http = new HttpClient(new AuthenticationHandler()) { BaseAddress = server.Address, Timeout = TimeSpan.FromSeconds(1) };
        var client = new EllaiReviewHttpClient(http, guest, useLiveVoiceWebSocket: true);
        var pipe = new Pipe();
        await using var input = pipe.Reader.AsStream(leaveOpen: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var sending = client.TranscribeLiveVoiceStreamAsync("owned-session", input, deadline.Token, "same-capture", 240);
        await pipe.Writer.WriteAsync(new byte[] { 1, 2, 3 }, deadline.Token);
        await pipe.Writer.WriteAsync(new byte[] { 4, 0, 0 }, deadline.Token);
        var first = await Task.WhenAny(server.FirstFrame.Task, sending).WaitAsync(deadline.Token);
        if (first == sending) await sending;
        await server.FirstFrame.Task.WaitAsync(deadline.Token);
        // HttpClient's request timeout must not become the microphone lifetime.
        await Task.Delay(1200, deadline.Token);
        Assert.False(sending.IsCompleted);
        await pipe.Writer.CompleteAsync();
        var result = await sending;
        Assert.True(result!.ServerSubmission);
        Assert.True(input.CanRead);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 0, 0 }, server.Pcm.ToArray());
        Assert.Equal("test-session=synthetic", server.Cookie);
        Assert.Equal(server.Address.GetLeftPart(UriPartial.Authority), server.Origin);
        Assert.Equal(guest ? "/api/v1/ellai/guest/sessions/owned-session/voice/live-stream" :
            "/api/v1/ellai/sessions/owned-session/voice/live-stream", server.Path);
        Assert.Contains("voiceCaptureId=same-capture", server.Query);
        Assert.Contains("voiceCaptureOffsetSamples=240", server.Query);
        await pipe.Reader.CompleteAsync();
    }

    [Fact]
    public async Task GatewayFailureReleasesSenderWithoutWaitingForMicrophoneEof()
    {
        await using var server = await Server.StartAsync(true);
        using var http = new HttpClient(new AuthenticationHandler()) { BaseAddress = server.Address };
        var client = new EllaiReviewHttpClient(http, true, useLiveVoiceWebSocket: true);
        var pipe = new Pipe();
        await using var input = pipe.Reader.AsStream(leaveOpen: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var sending = client.TranscribeLiveVoiceStreamAsync("owned-session", input, deadline.Token, "capture");
        await pipe.Writer.WriteAsync(new byte[] { 1, 2 }, deadline.Token);
        await Assert.ThrowsAsync<WebSocketException>(() => sending);
        Assert.True(server.FirstFrame.Task.IsCompletedSuccessfully);
        Assert.False(deadline.IsCancellationRequested);
        Assert.True(input.CanRead);
        await pipe.Writer.CompleteAsync();
        await pipe.Reader.CompleteAsync();
    }

    [Fact]
    public async Task CancellationStopsTransportWithoutDisposingTheMicrophoneStream()
    {
        await using var server = await Server.StartAsync(false);
        using var http = new HttpClient(new AuthenticationHandler()) { BaseAddress = server.Address };
        var pipe = new Pipe();
        await using var input = pipe.Reader.AsStream(leaveOpen: true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var client = new EllaiReviewHttpClient(http, true, useLiveVoiceWebSocket: true);
        var sending = client.TranscribeLiveVoiceStreamAsync("owned-session", input, cancellation.Token, "capture");
        await pipe.Writer.WriteAsync(new byte[] { 1, 2 }, cancellation.Token);
        await server.FirstFrame.Task.WaitAsync(cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        Assert.True(input.CanRead);
        await pipe.Writer.CompleteAsync();
        await pipe.Reader.CompleteAsync();
    }

    [Fact]
    public async Task ForeignOriginIsRejectedBeforeAuthenticationOrAudioRead()
    {
        using var http = new HttpClient { BaseAddress = new("https://gateway.test") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => EllaiLiveVoiceWebSocketClient.SendAsync(
            http, new("wss://foreign.test/voice"), Stream.Null, default));
    }

    private sealed class AuthenticationHandler() : DelegatingHandler(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false })
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("http", request.RequestUri!.Scheme);
            request.Headers.Add("Cookie", "test-session=synthetic");
            return base.SendAsync(request, ct);
        }
    }

    private sealed class Server(WebApplication app, bool fail) : IAsyncDisposable
    {
        public Uri Address { get; private set; } = null!;
        public TaskCompletionSource FirstFrame { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public MemoryStream Pcm { get; } = new();
        public string? Cookie, Origin, Path, Query;

        public static async Task<Server> StartAsync(bool fail)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            var server = new Server(app, fail);
            app.UseWebSockets();
            app.Run(server.HandleAsync);
            await app.StartAsync();
            server.Address = new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            return server;
        }

        private async Task HandleAsync(HttpContext context)
        {
            Cookie = context.Request.Headers.Cookie;
            Origin = context.Request.Headers.Origin;
            Path = context.Request.Path;
            Query = context.Request.QueryString.Value;
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[8192];
            while (true)
            {
                var frame = await socket.ReceiveAsync(buffer.AsMemory(), context.RequestAborted);
                if (frame.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Completed", context.RequestAborted);
                    break;
                }
                Pcm.Write(buffer, 0, frame.Count);
                FirstFrame.TrySetResult();
                if (fail)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "Recover", context.RequestAborted);
                    break;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
            Pcm.Dispose();
        }
    }
}
