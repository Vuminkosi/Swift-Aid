using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SwiftAid.Client.Cases;
using SwiftAid.Client.LocalStore;
using SwiftAid.Client.Outbox;
using SwiftAid.Client.Sync;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Client.Tests;

/// <summary>
/// Lane D: durable offline transport for staff case commands. Fake handler plus
/// file-backed outbox; no device, no network.
/// </summary>
public sealed class CaseCommandOfflineTransportTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Send_WhenOffline_QueuesIntentAndReplaysWithSameCommandIdOnReconnect()
    {
        await using var fixture = new OfflineFixture();
        var caseId = Guid.NewGuid();
        var input = new CaseCommandInput("Resolve", null, "Resolved offline.", null, null, null, 5);

        fixture.Gateway.EnqueueThrow(new HttpRequestException("No network."));
        fixture.Gateway.EnqueueFunc(request =>
        {
            var commandId = ReadBody(request).RootElement.GetProperty("commandId").GetGuid();
            return JsonResponse(HttpStatusCode.OK, new
            {
                caseId,
                commandId,
                eventIds = new[] { Guid.NewGuid() },
                aggregateVersion = 7,
                reportRevision = 1,
            });
        });

        var offline = await fixture.Client.SendAsync(caseId, input);

        Assert.Equal(CaseCommandStatus.Unavailable, offline.Status);
        Assert.NotEqual(Guid.Empty, offline.CommandId);
        var pending = await fixture.Tracker.GetAsync(offline.CommandId, CancellationToken.None);
        Assert.NotNull(pending);
        Assert.Equal(CaseCommandOutboxOutcome.Pending, pending.Outcome);

        await fixture.Sync.SynchronizeAsync(SyncTrigger.Manual, CancellationToken.None);

        var accepted = await fixture.Tracker.GetAsync(offline.CommandId, CancellationToken.None);
        Assert.NotNull(accepted);
        Assert.Equal(CaseCommandOutboxOutcome.Accepted, accepted.Outcome);
        Assert.Equal(7, accepted.AcceptedAggregateVersion);

        var posts = fixture.Gateway.Requests.Where(r => r.Method == HttpMethod.Post).ToList();
        Assert.Equal(2, posts.Count);
        using var first = ReadBody(posts[0]);
        using var second = ReadBody(posts[1]);
        Assert.Equal(offline.CommandId, first.RootElement.GetProperty("commandId").GetGuid());
        Assert.Equal(offline.CommandId, second.RootElement.GetProperty("commandId").GetGuid());
        Assert.Equal(5, first.RootElement.GetProperty("expectedVersion").GetInt64());
        Assert.Equal(5, second.RootElement.GetProperty("expectedVersion").GetInt64());
        Assert.Equal(1, first.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("Resolve", first.RootElement.GetProperty("commandType").GetString());
        Assert.Equal(
            first.RootElement.GetProperty("reason").GetString(),
            second.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Send_WhenOnlineFirstTry_ReturnsAcceptedWithStableId()
    {
        await using var fixture = new OfflineFixture();
        var caseId = Guid.NewGuid();
        fixture.Gateway.EnqueueFunc(request =>
        {
            var commandId = ReadBody(request).RootElement.GetProperty("commandId").GetGuid();
            return JsonResponse(HttpStatusCode.OK, new
            {
                caseId,
                commandId,
                eventIds = new[] { Guid.NewGuid() },
                aggregateVersion = 6,
                reportRevision = 1,
            });
        });

        var result = await fixture.Client.SendAsync(
            caseId, new CaseCommandInput("Resolve", null, "Resolved.", null, null, null, 5));

        Assert.Equal(CaseCommandStatus.Accepted, result.Status);
        Assert.Equal(6, result.AggregateVersion);
        var post = Assert.Single(fixture.Gateway.Requests, r => r.Method == HttpMethod.Post);
        Assert.Equal(result.CommandId, ReadBody(post).RootElement.GetProperty("commandId").GetGuid());
        post.Dispose();
    }

    [Fact]
    public async Task Send_ConflictFromServer_MarksConflictWithLatestVersion()
    {
        await using var fixture = new OfflineFixture();
        var caseId = Guid.NewGuid();
        fixture.Gateway.EnqueueStatus(HttpStatusCode.Conflict);
        fixture.Gateway.EnqueueJson(HttpStatusCode.OK, new CaseTimeline(
            new CaseTimelineHeader(caseId, "org-zw-1", "workspace-a", "ZW", "Active", "officer-1", 9, 2, DateTimeOffset.UtcNow), []));

        var result = await fixture.Client.SendAsync(
            caseId, new CaseCommandInput("Resolve", null, "Stale write.", null, null, null, 5));

        Assert.Equal(CaseCommandStatus.Conflict, result.Status);
        Assert.Equal(9, result.AggregateVersion);
        var entry = await fixture.Tracker.GetAsync(result.CommandId, CancellationToken.None);
        Assert.NotNull(entry);
        Assert.Equal(CaseCommandOutboxOutcome.Conflict, entry.Outcome);
    }

    [Fact]
    public async Task Send_DeniedFromServer_MarksDenied()
    {
        await using var fixture = new OfflineFixture();
        fixture.Gateway.EnqueueStatus(HttpStatusCode.NotFound);

        var result = await fixture.Client.SendAsync(
            Guid.NewGuid(), new CaseCommandInput("Resolve", null, "No access.", null, null, null, 5));

        Assert.Equal(CaseCommandStatus.Denied, result.Status);
        var entry = await fixture.Tracker.GetAsync(result.CommandId, CancellationToken.None);
        Assert.NotNull(entry);
        Assert.Equal(CaseCommandOutboxOutcome.Denied, entry.Outcome);
    }

    [Fact]
    public async Task Send_InvalidFromServer_MarksInvalid()
    {
        await using var fixture = new OfflineFixture();
        fixture.Gateway.EnqueueStatus(HttpStatusCode.BadRequest);

        var result = await fixture.Client.SendAsync(
            Guid.NewGuid(), new CaseCommandInput("Resolve", null, "Bad.", null, null, null, 5));

        Assert.Equal(CaseCommandStatus.Invalid, result.Status);
        var entry = await fixture.Tracker.GetAsync(result.CommandId, CancellationToken.None);
        Assert.NotNull(entry);
        Assert.Equal(CaseCommandOutboxOutcome.Invalid, entry.Outcome);
    }

    [Fact]
    public async Task Send_ServerError_KeepsIntentPendingAndReportsUnavailable()
    {
        await using var fixture = new OfflineFixture();
        fixture.Gateway.EnqueueStatus(HttpStatusCode.ServiceUnavailable);

        var result = await fixture.Client.SendAsync(
            Guid.NewGuid(), new CaseCommandInput("Resolve", null, "Retry me.", null, null, null, 5));

        Assert.Equal(CaseCommandStatus.Unavailable, result.Status);
        var entry = await fixture.Tracker.GetAsync(result.CommandId, CancellationToken.None);
        Assert.NotNull(entry);
        Assert.Equal(CaseCommandOutboxOutcome.Pending, entry.Outcome);
    }

    [Fact]
    public async Task Send_InvalidInput_ReturnsInvalidWithoutQueueing()
    {
        await using var fixture = new OfflineFixture();

        var result = await fixture.Client.SendAsync(
            Guid.Empty, new CaseCommandInput("Resolve", null, "No case.", null, null, null, 5));

        Assert.Equal(CaseCommandStatus.Invalid, result.Status);
        Assert.Empty(fixture.Gateway.Requests);
        Assert.Empty(await fixture.Tracker.GetForCaseAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Send_WithoutProvisionedAccess_ReturnsDeniedWithoutQueueing()
    {
        await using var fixture = new OfflineFixture();
        fixture.Access.Context = null;
        var caseId = Guid.NewGuid();

        var result = await fixture.Client.SendAsync(
            caseId, new CaseCommandInput("Resolve", null, "Signed out.", null, null, null, 5));

        Assert.Equal(CaseCommandStatus.Denied, result.Status);
        Assert.Empty(fixture.Gateway.Requests);
    }

    [Fact]
    public async Task Send_WithoutExplicitVersion_ResolvesVersionFromTimeline()
    {
        await using var fixture = new OfflineFixture();
        var caseId = Guid.NewGuid();
        fixture.Timeline.Result = new CaseTimelineResult(
            CaseTimelineAccess.Found,
            new CaseTimeline(
                new CaseTimelineHeader(caseId, "org-zw-1", "workspace-a", "ZW", "Active", "officer-1", 8, 2, DateTimeOffset.UtcNow), []));
        fixture.Gateway.EnqueueFunc(request =>
        {
            var commandId = ReadBody(request).RootElement.GetProperty("commandId").GetGuid();
            return JsonResponse(HttpStatusCode.OK, new
            {
                caseId,
                commandId,
                eventIds = new[] { Guid.NewGuid() },
                aggregateVersion = 9,
                reportRevision = 2,
            });
        });

        var result = await fixture.Client.SendAsync(
            caseId, new CaseCommandInput("Resolve", null, "Resolved.", null, null, null, null));

        Assert.Equal(CaseCommandStatus.Accepted, result.Status);
        var post = Assert.Single(fixture.Gateway.Requests, r => r.Method == HttpMethod.Post);
        Assert.Equal(8, ReadBody(post).RootElement.GetProperty("expectedVersion").GetInt64());
    }

    [Fact]
    public async Task Tracker_GetForCaseAsync_ListsOnlyThatCase()
    {
        await using var fixture = new OfflineFixture();
        fixture.Gateway.EnqueueStatus(HttpStatusCode.ServiceUnavailable);
        fixture.Gateway.EnqueueStatus(HttpStatusCode.ServiceUnavailable);
        var caseId = Guid.NewGuid();
        var otherCaseId = Guid.NewGuid();

        await fixture.Client.SendAsync(caseId, new CaseCommandInput("Resolve", null, "One.", null, null, null, 5));
        await fixture.Client.SendAsync(otherCaseId, new CaseCommandInput("Resolve", null, "Two.", null, null, null, 5));

        var entries = await fixture.Tracker.GetForCaseAsync(caseId, CancellationToken.None);
        var only = Assert.Single(entries);
        Assert.Equal(caseId, only.CaseId);
        Assert.Equal(CaseCommandOutboxOutcome.Pending, only.Outcome);
    }

    private static HttpResponseMessage JsonResponse<T>(HttpStatusCode status, T value) =>
        new(status) { Content = JsonContent.Create(value, options: JsonOptions) };

    private static JsonDocument ReadBody(HttpRequestMessage request) =>
        JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());

    private sealed class OfflineFixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"swiftaid-casecmd-{Guid.NewGuid():N}");

        public OfflineFixture()
        {
            Directory.CreateDirectory(_directory);
            Clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-10-27T10:00:00Z"));
            Access = new MutableAccessContextProvider(new LocalDraftAccessContext(
                "actor-a", true, true, 1, Clock.GetUtcNow().AddHours(1), ["workspace-a"]));
            var outbox = new CommandOutbox(
                new CommandOutboxDbContextFactory(Path.Combine(_directory, "outbox.sqlite")),
                Access, Keys, Clock, () => 0.5d);
            Outbox = outbox;
            Gateway = new GatewayStub();
            var http = new HttpClient(Gateway) { BaseAddress = new Uri("https://gateway.test/") };
            var transport = new CaseCommandOutboxTransport(http);
            Sync = new SyncCoordinator(
                outbox,
                new FixedAccessRefresher(new SyncAccessResult(
                    true, "actor-a", 1, new HashSet<string>(["workspace-a"], StringComparer.Ordinal))),
                new CaseCommandPayloadBinder(),
                transport,
                new StaticProjectionRefresher(new ProjectionRefreshResult(false, new HashSet<Guid>())));
            Timeline = new FuncTimelineClient();
            Client = new OfflineCaseCommandClient(outbox, Sync, Timeline, Access);
            Tracker = new CaseCommandOutboxTracker(outbox);
        }

        public MutableTimeProvider Clock { get; }
        public MutableAccessContextProvider Access { get; }
        public TestKeyStore Keys { get; } = new();
        public CommandOutbox Outbox { get; }
        public GatewayStub Gateway { get; }
        public SyncCoordinator Sync { get; }
        public FuncTimelineClient Timeline { get; }
        public OfflineCaseCommandClient Client { get; }
        public CaseCommandOutboxTracker Tracker { get; }

        public ValueTask DisposeAsync()
        {
            Gateway.Dispose();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset initialTime) : TimeProvider
    {
        private DateTimeOffset _now = initialTime;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class MutableAccessContextProvider(LocalDraftAccessContext? context) : ILocalDraftAccessContextProvider
    {
        public LocalDraftAccessContext? Context { get; set; } = context;
        public ValueTask<LocalDraftAccessContext?> GetCurrentAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Context);
    }

    private sealed class TestKeyStore : ILocalDraftKeyStore
    {
        public ValueTask<byte[]> GetOrCreateKeyAsync(string actorId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
    }

    private sealed class FixedAccessRefresher(SyncAccessResult result) : ISyncAccessRefresher
    {
        public Task<SyncAccessResult> RefreshAsync(CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class StaticProjectionRefresher(ProjectionRefreshResult result) : IAuthorizedProjectionRefresher
    {
        public Task<ProjectionRefreshResult> RefreshAsync(CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class FuncTimelineClient : ICaseTimelineClient
    {
        public CaseTimelineResult Result { get; set; } = new(CaseTimelineAccess.NotFoundOrNotAuthorized, null);
        public Task<CaseTimelineResult> GetAsync(Guid caseId, int? limit, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result);
    }

    private sealed class GatewayStub : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _scripts = new();

        public List<HttpRequestMessage> Requests { get; } = [];

        public void EnqueueFunc(Func<HttpRequestMessage, HttpResponseMessage> script) => _scripts.Enqueue(script);
        public void EnqueueJson<T>(HttpStatusCode status, T value) =>
            _scripts.Enqueue(_ => new HttpResponseMessage(status) { Content = JsonContent.Create(value, options: JsonOptions) });
        public void EnqueueStatus(HttpStatusCode status) =>
            _scripts.Enqueue(_ => new HttpResponseMessage(status));
        public void EnqueueThrow(Exception exception) =>
            _scripts.Enqueue(_ => throw exception);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (_scripts.Count == 0)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            return Task.FromResult(_scripts.Dequeue()(request));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var request in Requests) request.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
