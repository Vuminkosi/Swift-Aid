using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIddict.EntityFrameworkCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Registration;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Ellai;
using SwiftAid.Application.Intake;
using SwiftAid.Application.Intake.Imports;
using SwiftAid.Contracts.Intake;
using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Domain.Intake;
using SwiftAid.Infrastructure.Authorization;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.IntakeImports;
using SwiftAid.Infrastructure.Persistence.Ellai;
using SwiftAid.Infrastructure.Persistence.Protection;
using SwiftAid.Infrastructure.Persistence.Registration;
using SwiftAid.Infrastructure.Persistence.Repositories.Cases;
using SwiftAid.Infrastructure.Persistence.Transactions;
using SwiftAid.Infrastructure.Memory;
using SwiftAid.Intelligence.Meta;
using SwiftAid.Gateway.Ellai;
using System.Runtime.CompilerServices;

namespace SwiftAid.Integration.Tests;

[Collection(IntakePostgresCollection.Name)]
public sealed class CaseImportPostgresTests
{
    [IntakePostgresFact]
    public Task ConcurrentFrameworkWorkersKeepDifferentActorsDurableSessionHistoryIsolated() => RunConcurrentFrameworkWorkersAsync(false);

    [LiveImportHistoryFact]
    public Task LiveMuseConcurrentFrameworkWorkersKeepDifferentActorsDurableSessionHistoryIsolated() => RunConcurrentFrameworkWorkersAsync(true);

    private static async Task RunConcurrentFrameworkWorkersAsync(bool liveModel)
    {
        await using var fixture = await Fixture.CreateAsync();
        var ids = new[] { "parallel-a-" + Guid.NewGuid().ToString("N"), "parallel-b-" + Guid.NewGuid().ToString("N") };
        var markers = new[] { "history-a-" + Guid.NewGuid().ToString("N"), "history-b-" + Guid.NewGuid().ToString("N") };
        var actors = new[] { Actor, CaseRegistrationBackendTests.CreateOfficer("parallel-owner-" + Guid.NewGuid().ToString("N")) };
        var sessions = new PostgresEllaiSessionStore(fixture.Db);
        for (var i = 0; i < ids.Length; i++)
        {
            var session = new EllaiSessionRecord(ids[i], EllaiMode.Ask, "Active", 1, 1, 1, 1, 0, 12,
                actors[i].ActorId, "access-epoch-7", null, null, DateTimeOffset.UtcNow);
            await sessions.CreateAsync(session, default);
            fixture.Db.Set<EllaiUtteranceEntity>().Add(new EllaiUtteranceEntity
            {
                SessionId = ids[i], ClientUtteranceId = "prior", Sequence = 1,
                AcceptedAtUtc = DateTimeOffset.UtcNow, CompletedAtUtc = DateTimeOffset.UtcNow,
                Outcome = "Completed", UserText = markers[i], AssistantText = "Synthetic previous answer.",
                ToolTraceJson = EllaiToolTrace.SerializeFlattened([], false, [])
            });
            await fixture.Db.SaveChangesAsync();
            var work = new EllaiUtteranceWork(ids[i], session.ScopeEpoch, "Synthetic follow-up", "public.product-help", null,
                EllaiMode.Ask, 1, "cycle-" + i, actors[i], true, "consent/0", EllaiProcessingClassification.UserProvidedNonSensitive,
                EllaiProcessingClassification.UserProvidedNonSensitive, new HashSet<string>(), EllaiConsentPurposes.PublicProductHelp, "current");
            Assert.NotNull(await sessions.TryAcceptUtteranceAsync(session with { SessionVersion = 2, CyclesUsed = 1 }, 1,
                "current", default, EllaiUtteranceWorkEnvelope.Serialize(work, "current")));
        }
        var options = liveModel ? EllaiOptions.Bind(Environment.GetEnvironmentVariable) :
            new EllaiOptions { Enabled = true, ApiKey = "synthetic", ApiUrl = "https://fixture.invalid", ReasoningModel = "fixture" };
        using var model = new ConcurrentHistoryClient(liveModel ? EllaiMuseChatClientFactory.Create(options) : null);
        var services = new ServiceCollection();
        var connection = fixture.Db.Database.GetConnectionString();
        services.AddScoped(_ => new SwiftAidDbContext(new DbContextOptionsBuilder<SwiftAidDbContext>().UseNpgsql(connection).UseOpenIddict().Options));
        services.AddScoped<PostgresEllaiSessionStore>();
        services.AddScoped<IEllaiSessionStore>(sp => sp.GetRequiredService<PostgresEllaiSessionStore>());
        services.AddScoped<IEllaiUtteranceOutboxStore>(sp => sp.GetRequiredService<PostgresEllaiSessionStore>());
        services.AddScoped<IEllaiUsageStore, PostgresEllaiUsageStore>();
        var consent = new EllaiConsentService();
        services.AddSingleton(consent).AddSingleton<EllaiUsageLedger>().AddSingleton<EllaiSessionResponseStore>();
        services.AddScoped(_ => new EllaiPromptBuilder("Synthetic policy", "fixture"));
        services.AddScoped(_ => new EllaiToolBroker(new EllaiPolicyService(), consent));
        services.AddEllaiReasoningExecutor(options, "framework");
        services.AddKeyedSingleton<IChatClient>(EllaiReasoningRegistration.FrameworkClientKey, model);
        await using var provider = services.BuildServiceProvider();
        using var firstWorker = new EllaiUtteranceWorker(provider, NullLogger<EllaiUtteranceWorker>.Instance);
        using var secondWorker = new EllaiUtteranceWorker(provider, NullLogger<EllaiUtteranceWorker>.Instance);
        try
        {
            await firstWorker.StartAsync(default);
            await secondWorker.StartAsync(default);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(liveModel ? 120 : 20));
            await model.BothStarted.Task.WaitAsync(timeout.Token);
            while (true)
            {
                await using var db = new SwiftAidDbContext(new DbContextOptionsBuilder<SwiftAidDbContext>().UseNpgsql(connection).UseOpenIddict().Options);
                if (await db.Set<EllaiUtteranceEntity>().CountAsync(turn => ids.Contains(turn.SessionId) && turn.ClientUtteranceId == "current" && turn.Outcome == "Completed", timeout.Token) == 2) break;
                await Task.Delay(50, timeout.Token);
            }
            var histories = model.Histories.ToArray();
            Assert.True(histories.Length >= 2);
            Assert.Contains(histories, history => history.Contains(markers[0]));
            Assert.Contains(histories, history => history.Contains(markers[1]));
            Assert.All(histories, history => Assert.True(history.Contains(markers[0]) ^ history.Contains(markers[1])));
            for (var i = 0; i < ids.Length; i++)
            {
                Assert.Equal(2, (await sessions.ReadConversationAsync(ids[i], actors[i].ActorId, timeout.Token)).Count);
                Assert.Empty(await sessions.ReadConversationAsync(ids[i], actors[1 - i].ActorId, timeout.Token));
            }
        }
        finally
        {
            await firstWorker.StopAsync(default);
            await secondWorker.StopAsync(default);
            await fixture.Db.Set<EllaiUsageSampleEntity>().Where(item => ids.Contains(item.SessionId)).ExecuteDeleteAsync();
            await fixture.Db.Set<EllaiSessionEntity>().Where(item => ids.Contains(item.SessionId)).ExecuteDeleteAsync();
        }
    }

    private sealed class ConcurrentHistoryClient(IChatClient? inner = null) : IChatClient
    {
        public System.Collections.Concurrent.ConcurrentBag<string[]> Histories { get; } = [];
        public TaskCompletionSource BothStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var snapshot = messages.ToArray();
            Histories.Add(snapshot.Select(message => message.Text).ToArray());
            return inner?.GetResponseAsync(snapshot, options, cancellationToken) ?? throw new NotSupportedException();
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var snapshot = messages.ToArray();
            Histories.Add(snapshot.Select(message => message.Text).ToArray());
            if (Histories.Count == 2) BothStarted.TrySetResult();
            await BothStarted.Task.WaitAsync(cancellationToken);
            if (inner is not null)
            {
                await foreach (var update in inner.GetStreamingResponseAsync(snapshot, options, cancellationToken).WithCancellation(cancellationToken))
                    yield return update;
                yield break;
            }
            yield return new ChatResponseUpdate(ChatRole.Assistant, """{"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"Synthetic answer.","spokenText":"Synthetic answer.","claims":[],"questions":[],"uiIntents":[],"planProposal":null,"topicRefs":[],"resumeSuggestion":null,"stopReason":null}}""");
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() => inner?.Dispose();
    }

    private sealed class LiveImportHistoryFactAttribute : FactAttribute
    {
        public LiveImportHistoryFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("ELLAI_LIVE") != "1" ||
                Environment.GetEnvironmentVariable("SWIFTAID_RUN_INTAKE_POSTGRES_FIXTURES") != "1")
                Skip = "Requires configured live Muse and a dedicated disposable PostgreSQL fixture.";
        }
    }

    private sealed class QdrantDownFactAttribute : FactAttribute
    {
        public QdrantDownFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("SWIFTAID_RUN_INTAKE_POSTGRES_FIXTURES") != "1" ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("QDRANT_TEST_DOWN_ENDPOINT")))
                Skip = "Requires disposable PostgreSQL and a stopped loopback Qdrant endpoint.";
        }
    }

    [QdrantDownFact]
    public Task FrameworkWorkerFallsBackToPostgresWhenQdrantIsStopped() => RunImportWorkerHistoryAsync(false, qdrantOutage: true);

    private sealed class LiveQdrantDownFactAttribute : FactAttribute
    {
        public LiveQdrantDownFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("ELLAI_LIVE") != "1" ||
                Environment.GetEnvironmentVariable("SWIFTAID_RUN_INTAKE_POSTGRES_FIXTURES") != "1" ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("QDRANT_TEST_DOWN_ENDPOINT")))
                Skip = "Requires live Muse, disposable PostgreSQL and a stopped loopback Qdrant endpoint.";
        }
    }

    [LiveQdrantDownFact]
    public Task LiveMuseFrameworkWorkerFallsBackToPostgresWhenQdrantIsStopped() => RunImportWorkerHistoryAsync(true, qdrantOutage: true);

    [IntakePostgresFact]
    public Task FrameworkWorkerRechecksDurableImportHistoryAfterGrantExpiry() => RunImportWorkerHistoryAsync(false);

    [IntakePostgresFact]
    public Task FrameworkWorkerRechecksImportHistoryBeyondThePostgresReadWindow() => RunImportWorkerHistoryAsync(false, longHistory: true);

    [IntakePostgresFact]
    public Task FrameworkWorkerStopsWhenGrantExpiresAfterHistorySelection() => RunImportWorkerHistoryAsync(false, raceGrant: true);

    [IntakePostgresFact]
    public Task FrameworkWorkerWithholdsAnswerWhenGrantExpiresDuringModelCall() => RunImportWorkerHistoryAsync(false, inFlightGrant: true);

    [IntakePostgresFact]
    public Task FrameworkWorkerDoesNotPublishAfterSessionCancellationDuringModelCall() => RunImportWorkerHistoryAsync(false, cancelSession: true);

    [IntakePostgresFact]
    public Task FrameworkWorkerRechecksRecentImportListAfterItChanges() => RunImportWorkerHistoryAsync(false, listHistory: true);

    [IntakePostgresFact]
    public Task FrameworkWorkerRechecksCaseListAfterItChanges() => RunImportWorkerHistoryAsync(false, caseHistory: true);

    [LiveImportHistoryFact]
    public Task LiveMuseWorkerRechecksDurableImportHistoryAfterGrantExpiry() => RunImportWorkerHistoryAsync(true);

    [LiveImportHistoryFact]
    public Task LiveMuseWorkerRechecksRecentImportListAfterItChanges() => RunImportWorkerHistoryAsync(true, listHistory: true);

    [LiveImportHistoryFact]
    public Task LiveMuseWorkerRechecksCaseListAfterItChanges() => RunImportWorkerHistoryAsync(true, caseHistory: true);

    private static async Task RunImportWorkerHistoryAsync(bool liveModel, bool longHistory = false,
        bool raceGrant = false, bool inFlightGrant = false, bool listHistory = false, bool caseHistory = false, bool cancelSession = false, bool qdrantOutage = false)
    {
        await using var fixture = await Fixture.CreateAsync();
        var scope = listHistory || caseHistory || qdrantOutage ? await fixture.CreateIsolatedWorkspaceAsync() : Context;
        var listScope = listHistory ? new EllaiImportListScope(scope.WorkspaceId,
            Assert.Single(await fixture.Store.GetWorkspacesAsync(Actor, default), item => item.WorkspaceId == scope.WorkspaceId).OrganizationId, "ZW") : null;
        var importId = Guid.NewGuid();
        var completed = await fixture.Service.ProcessAsync(Actor, importId, scope, 1, true,
            new StringReader(Csv(Guid.NewGuid())), default);
        Assert.Equal("Completed", completed.Summary!.State);
        var observedId = caseHistory || qdrantOutage ? Assert.Single(completed.Rows).CaseId!.Value : importId;
        var observedTool = qdrantOutage ? EllaiToolNames.Topic : caseHistory ? EllaiToolNames.State : listHistory ? EllaiToolNames.Imports : EllaiToolNames.ImportStatus;
        var sessions = new PostgresEllaiSessionStore(fixture.Db);
        var session = new EllaiSessionRecord("import-worker-" + Guid.NewGuid().ToString("N"), EllaiMode.Ask, "Active",
            1, 1, 1, 1, 0, 12, Actor.ActorId, "access-epoch-7", null, null, DateTimeOffset.UtcNow);
        await sessions.CreateAsync(session, default);
        using var qdrantHandler = new CountQdrantScrolls();
        using var qdrantHttp = new HttpClient(qdrantHandler) { Timeout = TimeSpan.FromSeconds(2) };
        EllaiMemoryIndex? downIndex = null;
        if (qdrantOutage)
        {
            var endpoint = new Uri(Environment.GetEnvironmentVariable("QDRANT_TEST_DOWN_ENDPOINT")!);
            Assert.True(endpoint.IsLoopback);
            var outage = await Record.ExceptionAsync(() => qdrantHttp.GetAsync(endpoint));
            Assert.True(outage is HttpRequestException or TaskCanceledException, "The disposable Qdrant endpoint must be unavailable.");
            downIndex = new EllaiMemoryIndex(qdrantHttp, endpoint.ToString(), "", "fixture-outage");
            var unit = new PostgresCaseCommandUnitOfWork(fixture.Db);
            var queue = new CaseQueueQuery(new PostgresCaseQueueStore(fixture.Db),
                new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(fixture.Db, unit), new PostgresCaseAccessDescriptorStore(fixture.Db, unit)), unit);
            var context = new EllaiToolContext(session.SessionId, session.ScopeEpoch, "cases", null, 4, true,
                Actor: Actor, AllowedTools: new HashSet<string> { EllaiToolNames.Topic });
            var row = Assert.Single((await queue.GetMineAsync(Actor, 20,
                new CaseQueueFilter(CaseIds: [observedId]), default)).Items);
            Assert.Equal(observedId, row.CaseId);
            await new EllaiCaseResultMemory(new EllaiTopicService(new PostgresEllaiTopicStore(fixture.Db)), queue)
                .RememberAsync(context, [row], default);
        }
        var consent = new EllaiConsentService();
        var modelOptions = liveModel ? EllaiOptions.Bind(Environment.GetEnvironmentVariable) : new EllaiOptions
        {
            Enabled = true, ApiKey = "synthetic", ApiUrl = "https://fixture.invalid", ReasoningModel = "fixture"
        };
        var modelGate = new ImportReadGate();
        using var model = new RecordingImportModel(liveModel ? EllaiMuseChatClientFactory.Create(modelOptions) : new ImportHistoryModel(observedId, listScope, caseHistory, qdrantOutage), modelGate);
        var grantGate = new ImportReadGate();
        IReadOnlyList<string> ModelMessages() => model.Messages;
        var services = new ServiceCollection();
        var connection = fixture.Db.Database.GetConnectionString();
        services.AddScoped(_ => new SwiftAidDbContext(new DbContextOptionsBuilder<SwiftAidDbContext>()
            .UseNpgsql(connection).UseOpenIddict().Options));
        services.AddScoped<PostgresEllaiSessionStore>();
        services.AddScoped<IEllaiSessionStore>(sp => sp.GetRequiredService<PostgresEllaiSessionStore>());
        services.AddScoped<IEllaiUtteranceOutboxStore>(sp => sp.GetRequiredService<PostgresEllaiSessionStore>());
        services.AddScoped<IEllaiUsageStore, PostgresEllaiUsageStore>();
        services.AddScoped(sp => new PostgresCaseImportStore(
            sp.GetRequiredService<SwiftAidDbContext>(), fixture.Protection, TimeProvider.System));
        services.AddScoped<ICaseImportStore>(sp => raceGrant
            ? new GatedImportStore(sp.GetRequiredService<PostgresCaseImportStore>(), grantGate)
            : sp.GetRequiredService<PostgresCaseImportStore>());
        services.AddScoped<ICaseImportWorkspaceReader>(sp => sp.GetRequiredService<PostgresCaseImportStore>());
        services.AddScoped<ICaseQueueQuery>(sp =>
        {
            var db = sp.GetRequiredService<SwiftAidDbContext>();
            var unit = new PostgresCaseCommandUnitOfWork(db);
            return new CaseQueueQuery(new PostgresCaseQueueStore(db),
                new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(db, unit), new PostgresCaseAccessDescriptorStore(db, unit)), unit);
        });
        services.AddSingleton(consent).AddSingleton<EllaiUsageLedger>().AddSingleton<EllaiSessionResponseStore>();
        services.AddScoped(_ => new EllaiPromptBuilder("Synthetic policy", "fixture"));
        services.AddScoped(sp => new EllaiToolBroker(new EllaiPolicyService(), consent,
            queue: sp.GetRequiredService<ICaseQueueQuery>(), imports: sp.GetRequiredService<ICaseImportStore>(), importWorkspaces: sp.GetRequiredService<ICaseImportWorkspaceReader>(),
            topics: qdrantOutage ? new EllaiTopicService(new PostgresEllaiTopicStore(sp.GetRequiredService<SwiftAidDbContext>())) : null,
            referenceIndex: downIndex));
        services.AddEllaiReasoningExecutor(modelOptions, "framework");
        services.AddKeyedSingleton<IChatClient>(EllaiReasoningRegistration.FrameworkClientKey, model);
        await using var provider = services.BuildServiceProvider();
        using var worker = new EllaiUtteranceWorker(provider, NullLogger<EllaiUtteranceWorker>.Instance);
        try
        {
            EllaiUtteranceWork Work(string id) => new(session.SessionId, session.ScopeEpoch, qdrantOutage && id == "outage-revoked"
                ? "Use ellai_topic with ref case-results and report only currently permitted cases."
                : id == "prior"
                ? qdrantOutage ? $"Use ellai_topic with ref case-results to recall {observedId:D}." : caseHistory ? $"Use ellai_state with projection queue and limit 20. List cases including {observedId:D}." : listHistory ? $"Use ellai_imports with workspace_id {listScope!.WorkspaceId}, organization_id {listScope.OrganizationId}, country {listScope.CountryCode}. List the imports and include identifier {importId:D} in your answer." : $"Use ellai_import_status with import_id {importId:D} to check this import. Include its identifier and status in your answer."
                : "What did we discuss? Reply briefly.",
                "public.product-help", null, EllaiMode.Ask, 1, "cycle-" + id, Actor, true, "consent/0",
                EllaiProcessingClassification.UserProvidedNonSensitive, EllaiProcessingClassification.UserProvidedNonSensitive,
                new HashSet<string> { observedTool },
                EllaiConsentPurposes.PublicProductHelp, id);
            await worker.StartAsync(default);
            async Task<EllaiConversationTurn> RunTurn(string id, string expectedOutcome = "Completed")
            {
                model.Reset();
                var current = (await sessions.GetAsync(session.SessionId, default))!;
                var work = Work(id);
                Assert.NotNull(await sessions.TryAcceptUtteranceAsync(current with
                    { SessionVersion = current.SessionVersion + 1, CyclesUsed = current.CyclesUsed + 1 },
                    current.SessionVersion, id, default, EllaiUtteranceWorkEnvelope.Serialize(work, id)));
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(liveModel ? 90 : 15));
                while (true)
                {
                    await Task.Delay(50, timeout.Token);
                    var turn = (await sessions.ReadConversationAsync(session.SessionId, Actor.ActorId, timeout.Token))
                        .Single(item => item.ClientUtteranceId == id);
                    if (turn.CompletedAtUtc is null) continue;
                    Assert.Equal(expectedOutcome, turn.Outcome);
                    return turn;
                }
            }
            var prior = await RunTurn("prior");
            Assert.True(EllaiToolTrace.TryRead(prior.ToolTraceJson!, out var priorTrace));
            Assert.Contains(priorTrace!.Observations, item => item.ToolName == observedTool &&
                item.Allowed && item.Evidence.Any(reference => reference.ResourceId == observedId.ToString("D")));
            if (listHistory) Assert.Equal(listScope, Assert.Single(priorTrace.Observations).ImportListScope);
            if (caseHistory) Assert.Equal(new EllaiCaseListQuery(20, null, null, null), Assert.Single(priorTrace.Observations).CaseListQuery);
            Assert.Contains(observedId.ToString("D"), prior.AssistantText!, StringComparison.OrdinalIgnoreCase);
            if (qdrantOutage)
            {
                Assert.True(Assert.Single(priorTrace.Observations).CaseSnapshotExpiresAtUtc > DateTimeOffset.UtcNow);
                Assert.True(qdrantHandler.Scrolls > 0, "The worker must attempt the unavailable Qdrant index.");
                var previousScrolls = qdrantHandler.Scrolls;
                await fixture.Db.AccessGrantGroups.Where(group => group.ActorId == Actor.ActorId && group.WorkspaceId == scope.WorkspaceId)
                    .ExecuteUpdateAsync(update => update.SetProperty(group => group.ExpiresAtUtc, DateTimeOffset.UtcNow.AddMinutes(-1)));
                var outageRevoked = await RunTurn("outage-revoked");
                Assert.True(qdrantHandler.Scrolls > previousScrolls);
                Assert.DoesNotContain(ModelMessages(), text => text.Contains(observedId.ToString("D"), StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(observedId.ToString("D"), outageRevoked.AssistantText!, StringComparison.OrdinalIgnoreCase);
                Assert.True(EllaiToolTrace.TryRead(outageRevoked.ToolTraceJson!, out var outageRevokedTrace));
                Assert.DoesNotContain(outageRevokedTrace!.Observations.SelectMany(item => item.Evidence), reference => reference.ResourceId == observedId.ToString("D"));
                return;
            }
            if (cancelSession)
            {
                modelGate.Arm();
                var pending = RunTurn("cancelled", "Cancelled");
                try
                {
                    await modelGate.ReadCompleted.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    Assert.Contains(ModelMessages(), text => text.Contains(observedId.ToString("D"), StringComparison.OrdinalIgnoreCase));
                    var changed = await fixture.Db.Set<EllaiSessionEntity>().Where(item => item.SessionId == session.SessionId)
                        .ExecuteUpdateAsync(update => update.SetProperty(item => item.Status, "Cancelled")
                            .SetProperty(item => item.Version, item => item.Version + 1));
                    Assert.Equal(1, changed);
                }
                finally { modelGate.Release.TrySetResult(); }
                var cancelled = await pending;
                Assert.Null(cancelled.AssistantText);
                Assert.Null(cancelled.ToolTraceJson);
                Assert.DoesNotContain(provider.GetRequiredService<EllaiSessionResponseStore>().ReadSince(session.SessionId, 0).Responses,
                    response => response.ClientUtteranceId == "cancelled" && !response.IsFinal);
                return;
            }
            if (inFlightGrant)
            {
                modelGate.Arm();
                var pending = RunTurn("in-flight", "TerminalFailure");
                try
                {
                    await modelGate.ReadCompleted.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    Assert.Contains(ModelMessages(), text => text.Contains(importId.ToString("D"), StringComparison.OrdinalIgnoreCase));
                    await fixture.Db.AccessGrantGroups.Where(group => group.ActorId == Actor.ActorId && group.WorkspaceId == Context.WorkspaceId)
                        .ExecuteUpdateAsync(update => update.SetProperty(group => group.ExpiresAtUtc, DateTimeOffset.UtcNow.AddMinutes(-1)));
                }
                finally { modelGate.Release.TrySetResult(); }
                var stopped = await pending;
                Assert.Null(stopped.AssistantText);
                return;
            }
            if (raceGrant)
            {
                grantGate.Arm();
                var pending = RunTurn("race", "TerminalFailure");
                try
                {
                    await grantGate.ReadCompleted.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    await fixture.Db.AccessGrantGroups.Where(group => group.ActorId == Actor.ActorId && group.WorkspaceId == Context.WorkspaceId)
                        .ExecuteUpdateAsync(update => update.SetProperty(group => group.ExpiresAtUtc, DateTimeOffset.UtcNow.AddMinutes(-1)));
                }
                finally { grantGate.Release.TrySetResult(); }
                var stopped = await pending;
                Assert.Null(stopped.AssistantText);
                Assert.Empty(ModelMessages());
                return;
            }
            var allowed = await RunTurn("allowed");
            Assert.Contains(ModelMessages(), text => text.Contains(observedId.ToString("D"), StringComparison.OrdinalIgnoreCase));
            Assert.True(EllaiToolTrace.TryRead(allowed.ToolTraceJson!, out var allowedTrace));
            Assert.Equal(3, allowedTrace!.Version);
            Assert.Contains(allowedTrace.Observations, item => item.ToolName == observedTool &&
                item.Evidence.Any(reference => reference.ResourceId == observedId.ToString("D")));
            if (listHistory || caseHistory)
            {
                var second = await fixture.Service.ProcessAsync(Actor, Guid.NewGuid(), scope, 1, true,
                    new StringReader(Csv(Guid.NewGuid())), default);
                Assert.Equal("Completed", second.Summary!.State);
                await RunTurn("changed-list");
                Assert.DoesNotContain(ModelMessages(), text => text.Contains(observedId.ToString("D"), StringComparison.OrdinalIgnoreCase));
                Assert.Contains(ModelMessages(), text => text.Contains("could not be verified", StringComparison.Ordinal));
                return;
            }
            if (longHistory)
            {
                // Insert synthetic completed exchanges carrying the provenance the
                // real worker just persisted; this places its source turn outside
                // the store's 100-row read without 100 provider calls.
                var before = (await sessions.GetAsync(session.SessionId, default))!;
                const int seededTurns = 101;
                var now = DateTimeOffset.UtcNow;
                fixture.Db.Set<EllaiUtteranceEntity>().AddRange(Enumerable.Range(1, seededTurns).Select(index => new EllaiUtteranceEntity
                {
                    SessionId = session.SessionId, ClientUtteranceId = $"seed-{index}",
                    Sequence = before.SessionVersion + index, AcceptedAtUtc = now, CompletedAtUtc = now,
                    Outcome = "Completed", UserText = $"Synthetic follow-up {index}",
                    AssistantText = $"Synthetic import {importId:D}", ToolTraceJson = allowed.ToolTraceJson
                }));
                await fixture.Db.SaveChangesAsync();
                Assert.NotNull(await sessions.TryUpdateAsync(before with { SessionVersion = before.SessionVersion + seededTurns }, before.SessionVersion, default));
                var window = await sessions.ReadConversationAsync(session.SessionId, Actor.ActorId, default);
                Assert.Equal(100, window.Count);
                Assert.DoesNotContain(window, turn => turn.ClientUtteranceId == "prior");
                await RunTurn("long-allowed");
                Assert.Contains(ModelMessages(), text => text.Contains(importId.ToString("D"), StringComparison.OrdinalIgnoreCase));
            }
            await fixture.Db.AccessGrantGroups.Where(group => group.ActorId == Actor.ActorId && group.WorkspaceId == Context.WorkspaceId)
                .ExecuteUpdateAsync(update => update.SetProperty(group => group.ExpiresAtUtc, DateTimeOffset.UtcNow.AddMinutes(-1)));
            var revoked = await RunTurn("revoked");
            Assert.DoesNotContain(ModelMessages(), text => text.Contains(importId.ToString("D"), StringComparison.OrdinalIgnoreCase));
            Assert.Contains(ModelMessages(), text => text.Contains("could not be verified", StringComparison.Ordinal));
            Assert.True(EllaiToolTrace.TryRead(revoked.ToolTraceJson!, out var revokedTrace));
            Assert.DoesNotContain(revokedTrace!.Observations, item => item.Evidence.Any(reference => reference.ResourceId == importId.ToString("D")));
        }
        finally
        {
            await worker.StopAsync(default);
            await fixture.Db.Set<EllaiUsageSampleEntity>().Where(item => item.SessionId == session.SessionId).ExecuteDeleteAsync();
            await fixture.Db.Set<EllaiSessionEntity>().Where(item => item.SessionId == session.SessionId).ExecuteDeleteAsync();
        }
    }

    private sealed class ImportReadGate
    {
        private int _armed;
        public TaskCompletionSource ReadCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Arm() => Interlocked.Exchange(ref _armed, 1);
        public bool Take() => Interlocked.Exchange(ref _armed, 0) == 1;
    }

    private sealed class CountQdrantScrolls() : DelegatingHandler(new HttpClientHandler())
    {
        private int _scrolls;
        public int Scrolls => Volatile.Read(ref _scrolls);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/points/scroll", StringComparison.Ordinal) == true)
                Interlocked.Increment(ref _scrolls);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class GatedImportStore(ICaseImportStore inner, ImportReadGate gate) : ICaseImportStore
    {
        public async Task<CaseImportSummary?> GetAsync(ActorContext actor, Guid importId, CancellationToken ct)
        {
            var result = await inner.GetAsync(actor, importId, ct);
            if (gate.Take())
            {
                gate.ReadCompleted.TrySetResult();
                await gate.Release.Task.WaitAsync(ct);
            }
            return result;
        }
        public Task<CaseImportLease> OpenAsync(ActorContext actor, Guid importId, CaseImportContext context, long startRow, CancellationToken ct) => inner.OpenAsync(actor, importId, context, startRow, ct);
        public Task<int> BeginRowAsync(CaseImportLease lease, long row, Guid? commandId, byte[] sourceHash, byte[] sourceBytes, CancellationToken ct) => inner.BeginRowAsync(lease, row, commandId, sourceHash, sourceBytes, ct);
        public Task CompleteRowAsync(CaseImportLease lease, CaseImportRowOutcome result, CancellationToken ct) => inner.CompleteRowAsync(lease, result, ct);
        public Task CloseAsync(CaseImportLease lease, string state, string? errorCode, CancellationToken ct) => inner.CloseAsync(lease, state, errorCode, ct);
    }

    private sealed class RecordingImportModel(IChatClient inner, ImportReadGate gate) : DelegatingChatClient(inner)
    {
        private readonly List<string> _messages = [];
        public IReadOnlyList<string> Messages { get { lock (_messages) return _messages.ToArray(); } }
        public void Reset() { lock (_messages) _messages.Clear(); }
        private void Capture(IEnumerable<ChatMessage> messages)
        {
            lock (_messages) _messages.AddRange(messages.Select(message => message.Text));
        }
        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var snapshot = messages.ToArray();
            Capture(snapshot);
            return base.GetResponseAsync(snapshot, options, cancellationToken);
        }
        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var snapshot = messages.ToArray();
            Capture(snapshot);
            if (gate.Take()) { gate.ReadCompleted.TrySetResult(); await gate.Release.Task.WaitAsync(cancellationToken); }
            await foreach (var update in base.GetStreamingResponseAsync(snapshot, options, cancellationToken).WithCancellation(cancellationToken))
                yield return update;
        }
    }

    [IntakePostgresFact]
    public async Task CaseListHistoryRequiresTheSameAuthorizedPostgresQueryResult()
    {
        await using var fixture = await Fixture.CreateAsync();
        var scope = await fixture.CreateIsolatedWorkspaceAsync();
        var first = await fixture.Service.ProcessAsync(Actor, Guid.NewGuid(), scope, 1, true,
            new StringReader(Csv(Guid.NewGuid())), default);
        Assert.Equal("Completed", first.Summary!.State);
        var unit = new PostgresCaseCommandUnitOfWork(fixture.Db);
        var queue = new CaseQueueQuery(new PostgresCaseQueueStore(fixture.Db),
            new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(fixture.Db, unit), new PostgresCaseAccessDescriptorStore(fixture.Db, unit)), unit);
        var before = await queue.GetMineAsync(Actor, 20, default);
        Assert.Equal(CaseQueueAccess.Allowed, before.Access);
        Assert.NotEmpty(before.Items);
        Assert.True(before.Items.Count < 20);
        var evidence = before.Items.Select(item => new EvidenceRef("CaseQueue", item.CaseId.ToString("D"),
            item.AggregateVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), null, item.CaseId.ToString("D"))).ToArray();
        var trace = EllaiToolTrace.SerializeFlattened([new(EllaiToolNames.State, true, "Ok", evidence, false,
            CaseListQuery: new EllaiCaseListQuery(20, null, null, null))], false, []);
        var turns = new[] { new EllaiConversationTurn("session", "prior", 1, "list cases", "Prior case list.", "Completed", 1, DateTimeOffset.UtcNow) { ToolTraceJson = trace },
            new EllaiConversationTurn("session", "current", 2, "what now?", null, "Queued", 0, null) };
        var unchanged = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor, queue, default);
        Assert.Contains(unchanged.Messages, item => item.Content == "Prior case list.");
        var second = await fixture.Service.ProcessAsync(Actor, Guid.NewGuid(), scope, 1, true,
            new StringReader(Csv(Guid.NewGuid())), default);
        Assert.Equal("Completed", second.Summary!.State);
        var after = await queue.GetMineAsync(Actor, 20, default);
        Assert.True(after.Items.Count > before.Items.Count);
        var changed = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor, queue, default);
        Assert.DoesNotContain(changed.Messages, item => item.Content == "Prior case list.");
    }

    [IntakePostgresFact]
    public async Task RecentImportHistoryRequiresTheSameAuthorizedCompleteList()
    {
        await using var fixture = await Fixture.CreateAsync();
        var scope = await fixture.CreateIsolatedWorkspaceAsync();
        var firstId = Guid.NewGuid();
        var first = await fixture.Service.ProcessAsync(Actor, firstId, scope, 1, true,
            new StringReader(Csv(Guid.NewGuid())), default);
        Assert.Equal("Completed", first.Summary!.State);
        var before = await fixture.Store.GetRecentAsync(Actor, scope.WorkspaceId, "ZW", default);
        Assert.Single(before);
        var evidence = new EvidenceRef("ImportJob", firstId.ToString("D"), first.Summary.UpdatedAtUtc.ToString("O"), "status", firstId.ToString("D"));
        var destination = Assert.Single(await fixture.Store.GetWorkspacesAsync(Actor, default), item => item.WorkspaceId == scope.WorkspaceId);
        var scopeEvidence = new EllaiImportListScope(scope.WorkspaceId, destination.OrganizationId, "ZW");
        var trace = EllaiToolTrace.SerializeFlattened([new(EllaiToolNames.Imports, true, "Ok", [evidence], false, scopeEvidence)], false, []);
        Assert.NotNull(trace);
        var turns = new[]
        {
            new EllaiConversationTurn("session", "prior", 1, "recent imports", "One synthetic import was listed.", "Completed", 1, DateTimeOffset.UtcNow) { ToolTraceJson = trace },
            new EllaiConversationTurn("session", "current", 2, "what about now?", null, "Queued", 0, null)
        };
        var unchanged = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor, null, default,
            imports: fixture.Store, importWorkspaces: fixture.Store);
        Assert.Contains(unchanged.Messages, message => message.Content == "One synthetic import was listed.");
        var oldTrace = EllaiToolTrace.SerializeFlattened([new(EllaiToolNames.Imports, true, "Ok", [evidence], false)], false, []);
        var oldTurns = new[] { turns[0] with { ToolTraceJson = oldTrace }, turns[1] };
        var unknownScope = await EllaiAuthorizedConversationContext.BuildAsync(oldTurns, "current", Actor, null, default,
            imports: fixture.Store, importWorkspaces: fixture.Store);
        Assert.DoesNotContain(unknownScope.Messages, message => message.Content == "One synthetic import was listed.");
        var otherActor = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current",
            CaseRegistrationBackendTests.CreateOfficer("another-actor"), null, default,
            imports: fixture.Store, importWorkspaces: fixture.Store);
        Assert.DoesNotContain(otherActor.Messages, message => message.Content == "One synthetic import was listed.");
        var secondId = Guid.NewGuid();
        var second = await fixture.Service.ProcessAsync(Actor, secondId, scope, 1, true,
            new StringReader(Csv(Guid.NewGuid())), default);
        Assert.Equal("Completed", second.Summary!.State);
        var after = await fixture.Store.GetRecentAsync(Actor, scope.WorkspaceId, "ZW", default);
        Assert.Equal(2, after.Count);
        var replay = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", Actor, null, default,
            imports: fixture.Store, importWorkspaces: fixture.Store);
        Assert.DoesNotContain(replay.Messages, message => message.Content == "One synthetic import was listed.");
    }

    [IntakePostgresFact]
    public async Task FrameworkImportHistoryRequiresCurrentPostgresOwnerScopeAndVersion()
    {
        await using var fixture = await Fixture.CreateAsync();
        var importId = Guid.NewGuid();
        var completed = await fixture.Service.ProcessAsync(Actor, importId, Context, 1, true,
            new StringReader(Csv(Guid.NewGuid())), default);
        Assert.Equal("Completed", completed.Summary!.State);
        var version = completed.Summary.UpdatedAtUtc;
        var reference = new EvidenceRef("ImportJob", importId.ToString("D"), version.ToString("O"), "status", importId.ToString("D"));
        var trace = EllaiToolTrace.Serialize([new(EllaiToolNames.ImportStatus, true, "Ok", [reference], false)], false, []);
        var turns = new[]
        {
            new EllaiConversationTurn("session", "prior", 1, "import question", "synthetic import answer", "Completed", 1, DateTimeOffset.UtcNow)
                { ToolTraceJson = trace },
            new EllaiConversationTurn("session", "current", 2, "follow up", null, "Queued", 0, null)
        };
        async Task<bool> Replayed(ActorContext actor)
        {
            var history = await EllaiAuthorizedConversationContext.BuildAsync(turns, "current", actor, null, default, imports: fixture.Store);
            return history.Messages.Any(message => message.Content == "synthetic import answer");
        }

        Assert.True(await Replayed(Actor));
        Assert.False(await Replayed(CaseRegistrationBackendTests.CreateOfficer("another-actor")));

        var sessions = new PostgresEllaiSessionStore(fixture.Db);
        var session = new EllaiSessionRecord("import-history-" + Guid.NewGuid().ToString("N"), EllaiMode.Ask, "Active", 1, 1, 1, 1,
            0, 12, Actor.ActorId, "access-epoch-7", null, null, DateTimeOffset.UtcNow);
        await sessions.CreateAsync(session, default);
        try
        {
            EllaiUtteranceWork Work(string id, string question) => new(session.SessionId, session.ScopeEpoch, question,
                "public.product-help", null, EllaiMode.Ask, 1, "cycle-" + id, Actor, true, "consent/0",
                EllaiProcessingClassification.UserProvidedNonSensitive, EllaiProcessingClassification.UserProvidedNonSensitive,
                new HashSet<string>(), EllaiConsentPurposes.PublicProductHelp, id);
            var prior = Work("prior", "import question");
            var afterPrior = session with { SessionVersion = 2, CyclesUsed = 1 };
            Assert.NotNull(await sessions.TryAcceptUtteranceAsync(afterPrior, 1, "prior", default,
                EllaiUtteranceWorkEnvelope.Serialize(prior, "prior")));
            var claim = (await sessions.ClaimNextAsync(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), default))!;
            Assert.NotNull(await sessions.CompleteAsync(session.SessionId, "prior", claim.AttemptCount, DateTimeOffset.UtcNow,
                default, EllaiUtteranceResolution.Completed("synthetic import answer", 1) with { ToolTraceJson = trace }));
            var savedPrior = Assert.Single(await sessions.ReadConversationAsync(session.SessionId, Actor.ActorId, default));
            Assert.True(EllaiToolTrace.TryRead(savedPrior.ToolTraceJson!, out var savedTrace));
            Assert.Contains(savedTrace!.Observations, item => item.Evidence.Any(referenceItem => referenceItem.ResourceId == importId.ToString("D")));
            var current = Work("current", "follow up");
            Assert.NotNull(await sessions.TryAcceptUtteranceAsync(afterPrior with { SessionVersion = 3, CyclesUsed = 2 }, 2,
                "current", default, EllaiUtteranceWorkEnvelope.Serialize(current, "current")));
            var options = new DbContextOptionsBuilder<SwiftAidDbContext>()
                .UseNpgsql(fixture.Db.Database.GetConnectionString()).UseOpenIddict().Options;
            await using var reconnected = new SwiftAidDbContext(options);
            var restoredSessions = new PostgresEllaiSessionStore(reconnected);
            var restoredImports = new PostgresCaseImportStore(reconnected, fixture.Protection, TimeProvider.System);
            using var model = new ImportHistoryModel();
            var consent = new EllaiConsentService();
            var executor = new EllaiFrameworkExecutor(model, new EllaiPromptBuilder("Synthetic policy", "fixture"),
                new EllaiToolBroker(new EllaiPolicyService(), consent), new EllaiUsageLedger(), new InMemoryEllaiUsageStore());
            var services = new EllaiUtteranceServices(executor, null, null, consent, Sessions: restoredSessions,
                Outbox: restoredSessions, Imports: restoredImports);
            var before = await EllaiUtteranceRunner.RunOneAsync(current, services, 200, default);
            Assert.True(before.Completed);
            Assert.Contains("synthetic import answer", model.Messages);

            await fixture.Db.Set<CaseImportEntity>().Where(row => row.ImportId == importId)
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.UpdatedAtUtc, version.AddMinutes(1)));
            Assert.False(await Replayed(Actor));
            await fixture.Db.Set<CaseImportEntity>().Where(row => row.ImportId == importId)
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.UpdatedAtUtc, version));
            await fixture.Db.AccessGrantGroups.Where(group => group.ActorId == Actor.ActorId && group.WorkspaceId == Context.WorkspaceId)
                .ExecuteUpdateAsync(update => update.SetProperty(group => group.ExpiresAtUtc, DateTimeOffset.UtcNow.AddMinutes(-1)));
            Assert.False(await Replayed(Actor));
            var after = await EllaiUtteranceRunner.RunOneAsync(current, services, 200, default);
            Assert.True(after.Completed);
            Assert.DoesNotContain("synthetic import answer", model.Messages);
            Assert.Contains(model.Messages, text => text.Contains("could not be verified", StringComparison.Ordinal));
        }
        finally
        {
            await fixture.Db.Set<EllaiSessionEntity>().Where(item => item.SessionId == session.SessionId).ExecuteDeleteAsync();
        }
    }

    private sealed class ImportHistoryModel(Guid? importId = null, EllaiImportListScope? listScope = null, bool caseHistory = false, bool qdrantOutage = false) : IChatClient
    {
        public IReadOnlyList<string> Messages { get; private set; } = [];
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Messages = messages.Select(message => message.Text).ToArray();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                """{"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"Synthetic reply.","spokenText":"Synthetic reply.","claims":[],"questions":[],"uiIntents":[],"planProposal":null,"topicRefs":[],"resumeSuggestion":null,"stopReason":null}}""")));
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = messages.ToArray();
            Messages = snapshot.Select(message => message.Text).ToArray();
            var toolName = qdrantOutage ? EllaiToolNames.Topic : caseHistory ? EllaiToolNames.State : listScope is null ? EllaiToolNames.ImportStatus : EllaiToolNames.Imports;
            if (importId is not null && !snapshot.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Any() &&
                snapshot.LastOrDefault(message => message.Role == ChatRole.User)?.Text.Contains(toolName, StringComparison.Ordinal) == true)
            {
                var arguments = qdrantOutage ? new Dictionary<string, object?> { ["ref"] = "case-results" } : caseHistory ? new Dictionary<string, object?> { ["projection"] = "queue", ["limit"] = "20" } : listScope is null ? new Dictionary<string, object?> { ["import_id"] = importId.Value.ToString("D") } :
                    new Dictionary<string, object?> { ["workspace_id"] = listScope.WorkspaceId, ["organization_id"] = listScope.OrganizationId, ["country"] = listScope.CountryCode };
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("import-call", toolName, arguments)]);
                yield break;
            }
            var results = snapshot.SelectMany(message => message.Contents).OfType<FunctionResultContent>().ToArray();
            var containsCase = importId is not null && results.Any(result => result.Result?.ToString()?.Contains(importId.Value.ToString("D"), StringComparison.OrdinalIgnoreCase) == true);
            var response = importId is not null && results.Length > 0 && (!qdrantOutage || containsCase)
                ? "Synthetic import " + importId.Value.ToString("D") : "Synthetic reply.";
            yield return new ChatResponseUpdate(ChatRole.Assistant,
                """{"final_decision":{"schemaVersion":2,"kind":"Answer","displayText":"Synthetic reply.","spokenText":"Synthetic reply.","claims":[],"questions":[],"uiIntents":[],"planProposal":null,"topicRefs":[],"resumeSuggestion":null,"stopReason":null}}"""
                    .Replace("Synthetic reply.", response, StringComparison.Ordinal));
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [IntakePostgresFact]
    public async Task Imported_case_projection_matches_initial_journal_and_legacy_zero_is_repaired()
    {
        await using var fixture = await Fixture.CreateAsync();
        var result = await fixture.Service.ProcessAsync(Actor, Guid.NewGuid(), Context, 1, true, new StringReader(Csv(Guid.NewGuid())), default);
        var caseId = Assert.Single(result.Rows).CaseId!.Value;
        var header = await fixture.Db.ManagedCases.AsNoTracking().SingleAsync(c => c.CaseId == caseId);
        Assert.Equal(1, header.WorkflowVersion);
        // Reproduce the legacy projection defect in this task-owned synthetic case only.
        await fixture.Db.ManagedCases.Where(c => c.CaseId == caseId).ExecuteUpdateAsync(s => s.SetProperty(c => c.WorkflowVersion, 0));
        var repair = new SwiftAid.Infrastructure.Persistence.Migrations.RepairInitialCaseWorkflowVersion();
        foreach (var operation in repair.UpOperations.OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation>())
            await fixture.Db.Database.ExecuteSqlRawAsync(operation.Sql);
        Assert.Equal(1, (await fixture.Db.ManagedCases.AsNoTracking().SingleAsync(c => c.CaseId == caseId)).WorkflowVersion);
        var grant = await fixture.Db.AccessGrantGroups.FirstAsync(g => g.WorkspaceId == Context.WorkspaceId && g.ActorId == Actor.ActorId && g.Status == PersistentGrantStatus.Active);
        fixture.Db.AccessGrantOperations.Add(new AccessGrantOperationEntity { GrantGroupId = grant.GrantGroupId, Operation = CaseOperation.ResolveCase });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var command = new SwiftAid.Contracts.Commands.ResolveCaseCommand(Guid.NewGuid(), 2, "Synthetic workflow completion", null, DateTimeOffset.UtcNow);
        var completed = await fixture.CommandProcessor().ProcessAsync(Actor, caseId, 1, command, default);
        Assert.Equal(SwiftAid.Application.Cases.Commands.CaseCommandProcessingStatus.Accepted, completed.Status);
    }

    internal static readonly CaseImportContext Context = new("workspace-1",
        new IntakeConfirmations(true, "ZW", "catalogue/fixture", true, true, "fixture-policy", "1", null, null, null));
    internal static readonly ActorContext Actor = CaseRegistrationBackendTests.CreateOfficer("officer-1");

    [IntakePostgresFact]
    public async Task Real_registration_persists_and_restart_replay_does_not_duplicate_cases()
    {
        await using var fixture = await Fixture.CreateAsync();
        var importId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        var csv = Csv(commandId);
        var first = await fixture.Service.ProcessAsync(Actor, importId, Context, 1, true, new StringReader(csv), default);
        Assert.Null(first.ErrorCode);
        Assert.Equal("Accepted", Assert.Single(first.Rows).Status);
        Assert.Equal("Completed", first.Summary!.State);
        Assert.Equal(2, first.Summary.NextRow);
        Assert.Equal(1, first.Summary.Accepted);
        var row = await fixture.Db.Set<CaseImportRowEntity>().AsNoTracking().SingleAsync(row => row.ImportId == importId);
        Assert.DoesNotContain("Original supplied concern", Encoding.UTF8.GetString(row.ProtectedSource));
        var original = fixture.Protection.CreateProtector("SwiftAid.CaseImport.Row.v1", Actor.ActorId, importId.ToString("N"), "1")
            .Unprotect(row.ProtectedSource);
        Assert.Contains("Original supplied concern", Encoding.UTF8.GetString(original));
        CryptographicOperations.ZeroMemory(original);
        // New context and store represent restart; the durable receipt and ledger are authoritative.
        await using var reopened = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        await using (var replayLease = await new PostgresCaseRegistrationUnitOfWork(reopened)
            .BeginExistingCaseReplayAsync(Actor, first.Rows[0].CaseId!.Value, default))
        {
            Assert.True(replayLease.IsActive);
        }
        var replay = await fixture.CreateService(reopened).ProcessAsync(Actor, importId, Context, 1, true, new StringReader(csv), default);
        Assert.Equal("Replayed", Assert.Single(replay.Rows).Status);
        Assert.Equal(first.Rows[0].CaseId, replay.Rows[0].CaseId);
        Assert.Equal(1, await reopened.CommandReceipts.CountAsync(row => row.CommandId == commandId));
        Assert.Equal(2, await reopened.CaseEvents.CountAsync(row => row.CommandId == commandId));
        var eventIds = await reopened.CaseEvents.Where(row => row.CommandId == commandId).Select(row => row.EventId).ToArrayAsync();
        Assert.Equal(2, await reopened.DomainOutbox.CountAsync(row => eventIds.Contains(row.EventId)));
    }

    [IntakePostgresFact]
    public async Task Case_commit_before_checkpoint_failure_recovers_using_the_original_command()
    {
        await using var fixture = await Fixture.CreateAsync();
        var importId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        var store = new FailCheckpointOnce(fixture.Store);
        var service = fixture.CreateService(fixture.Db, store);
        var first = await service.ProcessAsync(Actor, importId, Context, 1, false, new StringReader(Csv(commandId)), default);
        Assert.Equal("import-unavailable", first.ErrorCode);
        Assert.Empty(first.Rows);
        Assert.Equal(1, first.Summary!.Unresolved);
        Assert.Equal(1, await fixture.Db.CommandReceipts.CountAsync(row => row.CommandId == commandId));
        var replay = await fixture.Service.ProcessAsync(Actor, importId, Context, 1, true, new StringReader(Csv(commandId)), default);
        Assert.Equal("Replayed", Assert.Single(replay.Rows).Status);
        Assert.Equal(1, replay.Summary!.Accepted);
        Assert.Equal(0, replay.Summary.Unresolved);
        Assert.Equal(1, await fixture.Db.CommandReceipts.CountAsync(row => row.CommandId == commandId));
    }

    [IntakePostgresFact]
    public async Task Invalid_row_is_durable_and_does_not_roll_back_a_later_valid_row()
    {
        await using var fixture = await Fixture.CreateAsync();
        var csv = Csv(Guid.NewGuid(), observations: "[{}]") + Csv(Guid.NewGuid()).Split('\n', 2)[1];
        var result = await fixture.Service.ProcessAsync(Actor, Guid.NewGuid(), Context, 1, true, new StringReader(csv), default);
        Assert.Equal(new[] { "Invalid", "Accepted" }, result.Rows.Select(row => row.Status));
        Assert.Equal(1, result.Summary!.Rejected);
        Assert.Equal(1, result.Summary.Accepted);
        Assert.Equal(3, result.Summary.NextRow);
        var restored = await fixture.Store.GetIssuesAsync(Actor, result.ImportId, 0, default);
        Assert.Equal("Invalid", Assert.Single(restored).Status);
        Assert.Empty(await fixture.Store.GetIssuesAsync(Actor, result.ImportId, 1, default));
        Assert.Empty(await fixture.Store.GetIssuesAsync(CaseRegistrationBackendTests.CreateOfficer("another-actor"), result.ImportId, 0, default));
    }

    [IntakePostgresFact]
    public async Task Immutable_row_content_checkpoint_gaps_and_active_leases_are_rejected()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = Guid.NewGuid();
        var lease = await fixture.Store.OpenAsync(Actor, id, Context, 1, default);
        await using var otherDb = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var other = new PostgresCaseImportStore(otherDb, fixture.Protection, TimeProvider.System);
        var busy = await Assert.ThrowsAsync<CaseImportException>(() => other.OpenAsync(Actor, id, Context, 1, default));
        Assert.Equal("import-busy", busy.Code);
        await fixture.Store.BeginRowAsync(lease, 1, null, SHA256.HashData([1]), [1], default);
        var changed = await Assert.ThrowsAsync<CaseImportException>(() => fixture.Store.BeginRowAsync(lease, 1, null, SHA256.HashData([2]), [2], default));
        Assert.Equal("row-content-conflict", changed.Code);
        fixture.Db.ChangeTracker.Clear();
        await fixture.Store.CloseAsync(lease, "Paused", null, default);
        otherDb.ChangeTracker.Clear();
        var gap = await Assert.ThrowsAsync<CaseImportException>(() => other.OpenAsync(Actor, id, Context, 10, default));
        Assert.Equal("checkpoint-gap", gap.Code);
    }

    [IntakePostgresFact]
    public async Task Owner_scope_and_current_access_epoch_are_required_for_history()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = Guid.NewGuid();
        var lease = await fixture.Store.OpenAsync(Actor, id, Context, 1, default);
        await fixture.Store.CloseAsync(lease, "Paused", null, default);
        Assert.Null(await fixture.Store.GetAsync(CaseRegistrationBackendTests.CreateOfficer("another-actor"), id, default));
        await fixture.Db.ActorAccessStates.Where(row => row.ActorId == Actor.ActorId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.AccessEpoch, Actor.AccessEpoch + 1));
        var denied = await Assert.ThrowsAsync<CaseImportException>(() => fixture.Store.GetAsync(Actor, id, default));
        Assert.Equal("not-authorized", denied.Code);
    }

    internal static string Csv(Guid commandId, string observations = "[\"Cough reported\"]") =>
        string.Join(',', CaseCsvImportAdapter.Columns) + "\n" +
        string.Join(',', new[] { "1", commandId.ToString("D"), Guid.Empty.ToString("D"), "SYNTHETIC-1", "Human",
            "Original supplied concern", observations, "2026-06-04T16:46:59+02:00", "2026-06-02T21:51:59+02:00", "ZW", "Manicaland", "", "Rusape", "ZW:place:rusape", "", "", "Approximate", "", "", "sn", "SYNTHETIC:SWIFTAID:1" }
            .Select((value, index) => index == 2 ? "" : "\"" + value.Replace("\"", "\"\"") + "\"")) + "\n";

    [IntakePostgresFact]
    public async Task Workspace_destinations_and_recent_imports_follow_current_owner_scope()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Contains(await fixture.Store.GetWorkspacesAsync(Actor, default),
            scope => scope.WorkspaceId == Context.WorkspaceId && scope.CountryCode == "ZW");
        Assert.Empty(await fixture.Store.GetWorkspacesAsync(CaseRegistrationBackendTests.CreateOfficer("another-actor"), default));
        var id = Guid.NewGuid();
        await fixture.Service.ProcessAsync(Actor, id, Context, 1, true, new StringReader(Csv(Guid.NewGuid())), default);
        var history = await fixture.Store.GetRecentAsync(Actor, Context.WorkspaceId, "ZW", default);
        Assert.Contains(history, item => item.ImportId == id && item.State == "Completed");
        Assert.DoesNotContain(await fixture.Store.GetWorkspacesAsync(Actor, default), scope => scope.CountryCode == "SZ");
        await Assert.ThrowsAsync<CaseImportException>(() => fixture.Store.GetRecentAsync(Actor, "another-workspace", "ZW", default));
        await fixture.Db.ActorAccessStates.Where(row => row.ActorId == Actor.ActorId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.AccessEpoch, Actor.AccessEpoch + 1));
        Assert.Empty(await fixture.Store.GetWorkspacesAsync(Actor, default));
        await Assert.ThrowsAsync<CaseImportException>(() => fixture.Store.GetRecentAsync(Actor, Context.WorkspaceId, "ZW", default));
    }

    [IntakePostgresFact]
    public async Task Completed_import_is_sealed_and_denied_replay_cannot_erase_saved_case()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = Guid.NewGuid();
        var command = Guid.NewGuid();
        await fixture.Service.ProcessAsync(Actor, id, Context, 1, true, new StringReader(Csv(command)), default);
        var append = await Assert.ThrowsAsync<CaseImportException>(() => fixture.Store.OpenAsync(Actor, id, Context, 2, default));
        Assert.Equal("import-completed", append.Code);
        var lease = await fixture.Store.OpenAsync(Actor, id, Context, 1, default);
        await fixture.Store.CompleteRowAsync(lease, new(1, command, "Denied", null, "registration-denied"), default);
        await fixture.Store.CloseAsync(lease, "Paused", "registration-denied", default);
        var status = await fixture.Store.GetAsync(Actor, id, default);
        Assert.Equal("Completed", status!.State);
        Assert.Equal(1, status.Accepted);
        Assert.Equal(0, status.Rejected);
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        private readonly CaseRegistrationBackendTests.SyntheticRegistrationKeyRing _keys;
        public SwiftAidDbContext Db { get; }
        public ProtectedPayloadDataProtectionProvider Protection { get; }
        internal SwiftAid.Infrastructure.Persistence.Protection.PostgresProtectedCaseEventPayloadStore ClassificationPayloadStore(SwiftAidDbContext db) =>
            new(db, new SwiftAid.Infrastructure.Persistence.Transactions.PostgresCaseCommandUnitOfWork(db), _keys.CreateOptions(), Protection);
        internal SwiftAid.Application.Cases.Commands.CaseCommandProcessor CommandProcessor()
        {
            var unit = new SwiftAid.Infrastructure.Persistence.Transactions.PostgresCaseCommandUnitOfWork(Db);
            var payloads = new PostgresProtectedCaseEventPayloadStore(Db, unit, _keys.CreateOptions(), Protection);
            var policy = new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(Db, unit), new PostgresCaseAccessDescriptorStore(Db, unit));
            var persistence = new SwiftAid.Infrastructure.Persistence.Processing.PostgresCaseCommandPersistence(Db, unit, payloads,
                new SwiftAid.Application.Serialization.CaseEventSerializer());
            return new(unit, policy, persistence);
        }
        public PostgresCaseImportStore Store { get; }
        public CaseCsvImportService Service { get; }

        private Fixture(SwiftAidDbContext db, CaseRegistrationBackendTests.SyntheticRegistrationKeyRing keys)
        {
            Db = db;
            _keys = keys;
            Protection = ProtectedPayloadDataProtectionProviderFactory.Create(keys.CreateOptions(), new CaseRegistrationBackendTests.TestHostEnvironment(Environments.Production));
            Store = new(db, Protection, TimeProvider.System);
            Service = CreateService(db, Store);
        }

        public static async Task<Fixture> CreateAsync()
        {
            var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
            if (!await db.Actors.AnyAsync(row => row.ActorId == "granter-1"))
            {
                db.Actors.Add(new ActorEntity { ActorId = "granter-1", Status = PersistentActorStatus.Active,
                    CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();
            }
            await CaseRegistrationBackendTests.SeedRegistrationGrantAsync(db);
            return new(db, CaseRegistrationBackendTests.SyntheticRegistrationKeyRing.Create());
        }

        internal async Task<CaseImportContext> CreateIsolatedWorkspaceAsync()
        {
            var workspace = "classification-fixture-" + Guid.NewGuid().ToString("N");
            Db.Workspaces.Add(new WorkspaceEntity { WorkspaceId = workspace, OrganizationId = "org-zw-1", Status = PersistentResourceStatus.Active });
            var original = await Db.AccessGrantGroups.AsNoTracking().FirstAsync(g => g.WorkspaceId == "workspace-1" && g.ActorId == Actor.ActorId && g.Status == PersistentGrantStatus.Active);
            original.GrantGroupId = "classification-grant-" + Guid.NewGuid().ToString("N");
            original.WorkspaceId = workspace;
            Db.Add(original);
            Db.AccessGrantOperations.Add(new AccessGrantOperationEntity { GrantGroupId = original.GrantGroupId, Operation = CaseOperation.ReadCase });
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return Context with { WorkspaceId = workspace };
        }

        public CaseCsvImportService CreateService(SwiftAidDbContext db, ICaseImportStore? store = null, IIntakeSubmissionValidator? validator = null) => new(
            store ?? new PostgresCaseImportStore(db, Protection, TimeProvider.System),
            new CaseRegistrationProcessor(new PostgresCaseRegistrationUnitOfWork(db), new PostgresCaseRegistrationAuthorization(db),
                CaseRegistrationBackendTests.CreatePersistence(db, _keys.CreateOptions(), Protection)), validator ?? new FixtureValidator());

        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); Protection.Dispose(); _keys.Dispose(); }
    }

    internal sealed class FixtureValidator : IIntakeSubmissionValidator
    {
        public Task<bool> ValidateAsync(IntakeSubmission submission, IntakeLimits limits, CancellationToken ct)
        { IntakeSubmissionRules.Validate(submission, limits); return Task.FromResult(submission.Location.CountryCode == "ZW"); }
    }

    [IntakePostgresFact]
    public async Task Validation_dependency_failure_is_durable_bounded_and_recoverable()
    {
        await using var fixture = await Fixture.CreateAsync();
        var importId = Guid.NewGuid();
        var command = Guid.NewGuid();
        var service = fixture.CreateService(fixture.Db, validator: new UnavailableValidator());
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var result = await service.ProcessAsync(Actor, importId, Context, 1, true, new StringReader(Csv(command)), default);
            var row = Assert.Single(result.Rows);
            Assert.Equal(attempt == 5 ? "Failed" : "RetryableFailure", row.Status);
            Assert.Equal("validation-unavailable", row.ErrorCode);
            Assert.Equal(attempt == 5 ? 2 : 1, result.Summary!.NextRow);
            Assert.Equal(0, result.Summary.Unresolved);
        }
        Assert.False(await fixture.Db.CommandReceipts.AnyAsync(row => row.CommandId == command));
        var ledger = await fixture.Db.Set<CaseImportRowEntity>().AsNoTracking().SingleAsync(row => row.ImportId == importId);
        Assert.Equal(5, ledger.Attempts);
        Assert.Equal("Failed", ledger.State);
        // Explicit reprocessing after the dependency recovers retains the original command identity.
        var recovered = await fixture.Service.ProcessAsync(Actor, importId, Context, 1, true, new StringReader(Csv(command)), default);
        Assert.Equal("Accepted", Assert.Single(recovered.Rows).Status);
        Assert.Equal(1, await fixture.Db.CommandReceipts.CountAsync(row => row.CommandId == command));
    }

    private sealed class UnavailableValidator : IIntakeSubmissionValidator
    {
        public Task<bool> ValidateAsync(IntakeSubmission submission, IntakeLimits limits, CancellationToken ct) =>
            throw new HttpRequestException("Synthetic policy dependency outage.");
    }

    [IntakePostgresFact]
    public async Task Registration_outages_have_a_stable_failure_code_and_five_attempt_limit()
    {
        await using var fixture = await Fixture.CreateAsync();
        foreach (var throws in new[] { false, true })
        {
            var service = new CaseCsvImportService(fixture.Store, new UnavailableRegistration(throws), new FixtureValidator());
            var importId = Guid.NewGuid();
            var csv = Csv(Guid.NewGuid());
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                var result = await service.ProcessAsync(Actor, importId, Context, 1, true, new StringReader(csv), default);
                var row = Assert.Single(result.Rows);
                Assert.Equal(attempt == 5 ? "Failed" : "RetryableFailure", row.Status);
                Assert.Equal("registration-unavailable", row.ErrorCode);
                Assert.Equal(0, result.Summary!.Unresolved);
                Assert.Equal(attempt == 5 ? 2 : 1, result.Summary.NextRow);
            }
        }
    }

    private sealed class UnavailableRegistration(bool throws) : IManagedCaseRegistrationProcessor
    {
        public ValueTask<CaseRegistrationResult> RegisterAsync(ActorContext actor, RegisterManagedCaseRequest request, CancellationToken ct) =>
            throws ? throw new IOException("Synthetic registration outage.") :
            ValueTask.FromResult(CaseRegistrationResult.WithoutCase(CaseRegistrationStatus.Unavailable, request.CommandId));
    }

    private sealed class FailCheckpointOnce(ICaseImportStore inner) : ICaseImportStore
    {
        public Task<CaseImportLease> OpenAsync(ActorContext actor, Guid id, CaseImportContext context, long row, CancellationToken ct) => inner.OpenAsync(actor, id, context, row, ct);
        public Task<int> BeginRowAsync(CaseImportLease lease, long row, Guid? command, byte[] hash, byte[] bytes, CancellationToken ct) => inner.BeginRowAsync(lease, row, command, hash, bytes, ct);
        public Task CompleteRowAsync(CaseImportLease lease, CaseImportRowOutcome result, CancellationToken ct) => throw new IOException("Synthetic checkpoint loss after case commit.");
        public Task CloseAsync(CaseImportLease lease, string state, string? error, CancellationToken ct) => inner.CloseAsync(lease, state, error, ct);
        public Task<CaseImportSummary?> GetAsync(ActorContext actor, Guid id, CancellationToken ct) => inner.GetAsync(actor, id, ct);
    }
}
