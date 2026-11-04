using System.Runtime.CompilerServices;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OpenIddict.EntityFrameworkCore;
using SwiftAid.Application.Classification;
using SwiftAid.Infrastructure.Persistence;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using SwiftAid.Gateway.Ellai;
using SwiftAid.Infrastructure.Classification;
using SwiftAid.Infrastructure.Persistence.Classification;
using SwiftAid.Infrastructure.Persistence.Ellai;
using SwiftAid.Intelligence.Meta;

namespace SwiftAid.Integration.Tests;

[Collection(IntakePostgresCollection.Name)]
public sealed class EllaiQualitySweepPostgresTests
{
    [IntakePostgresFact]
    public async Task BoundedSweepsUseSeparateSessionsAndAdvanceOnlyAfterDraining()
    {
        await using var fixture = await CaseImportPostgresTests.Fixture.CreateAsync();
        var scope = await fixture.CreateIsolatedWorkspaceAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 2; i++)
        {
            var imported = await fixture.Service.ProcessAsync(CaseImportPostgresTests.Actor, Guid.NewGuid(), scope,
                1, true, new StringReader(CaseImportPostgresTests.Csv(Guid.NewGuid())), default);
            ids.Add(Assert.Single(imported.Rows).CaseId!.Value);
        }
        fixture.Db.ChangeTracker.Clear();
        var settings = Options.Create(new ClassificationOptions { Enabled = true, WorkerTokenSha256 = new string('a', 64), Organizations = ["org-zw-1"] });
        var sweep = new EllaiQualitySweep(fixture.Db,
            new EllaiQualityWork(fixture.Db, fixture.ClassificationPayloadStore(fixture.Db), settings), settings);
        var options = new EllaiQualityScheduleOptions { Enabled = true, Workspaces = [scope.WorkspaceId], PolicyVersion = "policy/1", BatchSize = 1 };
        using var client = new NoInferenceClient();
        var agent = EllaiFrameworkAgentFactory.Instance.Create(client, new ChatClientAgentOptions { Name = "test", UseProvidedChatClientAsIs = true });
        var sessions = new List<AgentSession>();
        async Task<AgentSession> Open(CancellationToken ct)
        {
            var session = await agent.CreateSessionAsync(cancellationToken: ct);
            sessions.Add(session);
            return session;
        }
        Assert.Equal("disabled", await sweep.RunAsync(scope.WorkspaceId, new EllaiQualityScheduleOptions(), Open, new EllaiQualitySweepProgress(), default));
        Assert.Equal("disabled", await sweep.RunAsync("other", options, Open, new EllaiQualitySweepProgress(), default));
        var schedules = new PostgresEllaiOperationalScheduleStore(fixture.Db);
        Assert.True(await schedules.CreateAsync(scope.WorkspaceId, "ops.quality", 4, "policy/1", DateTimeOffset.UtcNow.AddMinutes(-1), default));
        Assert.Equal("policy-mismatch", await sweep.RunAsync(scope.WorkspaceId, new EllaiQualityScheduleOptions { Enabled = true, Workspaces = [scope.WorkspaceId], PolicyVersion = "policy/2" }, Open, new EllaiQualitySweepProgress(), default));
        Assert.Empty(sessions);
        var firstRun = new EllaiQualitySweepProgress();
        Assert.Equal("batch-limit", await sweep.RunAsync(scope.WorkspaceId, options, Open, firstRun, default));
        Assert.Equal(1, firstRun.WorkTurns);
        Assert.Equal(1, firstRun.ChangesCommitted);
        var recordedRun = await fixture.Db.Database.SqlQuery<Guid>($"SELECT run_id AS \"Value\" FROM ellai_case_quality_activity WHERE run_id = {firstRun.RunId}").SingleAsync();
        Assert.Equal(firstRun.RunId, recordedRun);
        Assert.Equal("batch-limit", await sweep.RunAsync(scope.WorkspaceId, options, Open, new EllaiQualitySweepProgress(), default));
        var emptyRun = new EllaiQualitySweepProgress();
        Assert.Equal("complete", await sweep.RunAsync(scope.WorkspaceId, options, Open, emptyRun, default));
        Assert.Equal(1, emptyRun.WorkTurns);
        Assert.Equal(0, emptyRun.ChangesCommitted);
        Assert.Equal(0, await fixture.Db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM ellai_case_quality_activity WHERE run_id = {emptyRun.RunId}").SingleAsync());
        Assert.Equal("not-due", await sweep.RunAsync(scope.WorkspaceId, options, Open, new EllaiQualitySweepProgress(), default));
        Assert.Equal(3, sessions.Count);
        Assert.Equal(3, sessions.Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.Equal(2, await fixture.Db.Set<ReportQualityEntity>().AsNoTracking().CountAsync(item => ids.Contains(item.CaseId)));
        Assert.Equal(0, client.Calls);
    }

    [IntakePostgresFact]
    public async Task ConcurrentReplicasCommitOnceAndUnauthorizedScopeDoesNotCreateSchedule()
    {
        await using var fixture = await CaseImportPostgresTests.Fixture.CreateAsync();
        var scope = await fixture.CreateIsolatedWorkspaceAsync();
        var imported = await fixture.Service.ProcessAsync(CaseImportPostgresTests.Actor, Guid.NewGuid(), scope,
            1, true, new StringReader(CaseImportPostgresTests.Csv(Guid.NewGuid())), default);
        var caseId = Assert.Single(imported.Rows).CaseId!.Value;
        fixture.Db.ChangeTracker.Clear();
        var settings = Options.Create(new ClassificationOptions { Enabled = true, WorkerTokenSha256 = new string('a', 64), Organizations = ["org-zw-1"] });
        var denied = Options.Create(new ClassificationOptions { Enabled = true, WorkerTokenSha256 = new string('a', 64), Organizations = ["other-org"] });
        var options = new EllaiQualityScheduleOptions { Enabled = true, Workspaces = [scope.WorkspaceId], PolicyVersion = "policy/1" };
        using var client = new NoInferenceClient();
        var agent = EllaiFrameworkAgentFactory.Instance.Create(client, new ChatClientAgentOptions { Name = "test", UseProvidedChatClientAsIs = true });
        async Task<AgentSession> Open(CancellationToken ct) => await agent.CreateSessionAsync(cancellationToken: ct);
        var deniedSweep = new EllaiQualitySweep(fixture.Db, new EllaiQualityWork(fixture.Db, fixture.ClassificationPayloadStore(fixture.Db), denied), denied);
        Assert.Equal("scope-unavailable", await deniedSweep.RunAsync(scope.WorkspaceId, options, Open, new EllaiQualitySweepProgress(), default));
        var schedules = new PostgresEllaiOperationalScheduleStore(fixture.Db);
        Assert.True(await schedules.CreateAsync(scope.WorkspaceId, "ops.quality", 4, "policy/1", DateTimeOffset.UtcNow.AddMinutes(-1), default));
        await using var replicaDb = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var first = new EllaiQualitySweep(fixture.Db, new EllaiQualityWork(fixture.Db, fixture.ClassificationPayloadStore(fixture.Db), settings), settings);
        var second = new EllaiQualitySweep(replicaDb, new EllaiQualityWork(replicaDb, fixture.ClassificationPayloadStore(replicaDb), settings), settings);
        var results = await Task.WhenAll(first.RunAsync(scope.WorkspaceId, options, Open, new EllaiQualitySweepProgress(), default), second.RunAsync(scope.WorkspaceId, options, Open, new EllaiQualitySweepProgress(), default));
        Assert.Contains("complete", results);
        Assert.Contains("not-due", results);
        Assert.Equal(1, await fixture.Db.Set<ReportQualityEntity>().AsNoTracking().CountAsync(item => item.CaseId == caseId));
        Assert.Equal(0, client.Calls);
    }

    [IntakePostgresFact]
    public async Task FailedSecondTurnRetainsCommittedCountAndReleasesLeaseForRetry()
    {
        await using var fixture = await CaseImportPostgresTests.Fixture.CreateAsync();
        var scope = await fixture.CreateIsolatedWorkspaceAsync();
        for (var i = 0; i < 2; i++)
            await fixture.Service.ProcessAsync(CaseImportPostgresTests.Actor, Guid.NewGuid(), scope,
                1, true, new StringReader(CaseImportPostgresTests.Csv(Guid.NewGuid())), default);
        fixture.Db.ChangeTracker.Clear();
        var settings = Options.Create(new ClassificationOptions { Enabled = true, WorkerTokenSha256 = new string('a', 64), Organizations = ["org-zw-1"] });
        var options = new EllaiQualityScheduleOptions { Enabled = true, Workspaces = [scope.WorkspaceId], PolicyVersion = "policy/1", BatchSize = 2 };
        var schedules = new PostgresEllaiOperationalScheduleStore(fixture.Db);
        Assert.True(await schedules.CreateAsync(scope.WorkspaceId, "ops.quality", 4, "policy/1", DateTimeOffset.UtcNow.AddMinutes(-1), default));
        var sweep = new EllaiQualitySweep(fixture.Db, new EllaiQualityWork(fixture.Db, fixture.ClassificationPayloadStore(fixture.Db), settings), settings);
        using var client = new NoInferenceClient();
        var agent = EllaiFrameworkAgentFactory.Instance.Create(client, new ChatClientAgentOptions { Name = "test", UseProvidedChatClientAsIs = true });
        var progress = new EllaiQualitySweepProgress();
        var calls = 0;
        async Task<AgentSession> FailSecond(CancellationToken ct)
        {
            if (++calls == 2) throw new InvalidOperationException("synthetic");
            return await agent.CreateSessionAsync(cancellationToken: ct);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => sweep.RunAsync(scope.WorkspaceId, options, FailSecond, progress, default));
        Assert.Equal(2, progress.WorkTurns);
        Assert.Equal(1, progress.ChangesCommitted);
        Assert.Equal(1, await fixture.Db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM ellai_case_quality_activity WHERE run_id = {progress.RunId}").SingleAsync());
        async Task<AgentSession> Open(CancellationToken ct) => await agent.CreateSessionAsync(cancellationToken: ct);
        var retry = new EllaiQualitySweepProgress();
        Assert.Equal("complete", await sweep.RunAsync(scope.WorkspaceId, options, Open, retry, default));
        Assert.Equal(1, retry.ChangesCommitted);
        Assert.Equal(2, retry.WorkTurns);
        Assert.Equal(0, client.Calls);
    }

    [IntakePostgresFact]
    public async Task ConcurrentClassifierWinnerDoesNotEndSweepBeforeRemainingCase()
    {
        await using var fixture = await CaseImportPostgresTests.Fixture.CreateAsync();
        var scope = await fixture.CreateIsolatedWorkspaceAsync();
        var caseIds = new List<Guid>();
        for (var index = 0; index < 2; index++)
        {
            var imported = await fixture.Service.ProcessAsync(CaseImportPostgresTests.Actor, Guid.NewGuid(), scope,
                1, true, new StringReader(CaseImportPostgresTests.Csv(Guid.NewGuid())), default);
            caseIds.Add(Assert.Single(imported.Rows).CaseId!.Value);
        }
        fixture.Db.ChangeTracker.Clear();
        var firstCaseId = caseIds.OrderBy(id => id).First();
        var source = await fixture.Db.CaseEvents.AsNoTracking().SingleAsync(e => e.CaseId == firstCaseId && e.EventType == "CaseReported");
        var schedules = new PostgresEllaiOperationalScheduleStore(fixture.Db);
        Assert.True(await schedules.CreateAsync(scope.WorkspaceId, "ops.quality", 4, "policy/1", DateTimeOffset.UtcNow.AddMinutes(-1), default));
        var barrier = new QualityInsertBarrier();
        var dbOptions = new DbContextOptionsBuilder<SwiftAidDbContext>().UseNpgsql(fixture.Db.Database.GetConnectionString())
            .UseOpenIddict().AddInterceptors(barrier).Options;
        await using var workerDb = new SwiftAidDbContext(dbOptions);
        var settings = Options.Create(new ClassificationOptions { Enabled = true, WorkerTokenSha256 = new string('a', 64), Organizations = ["org-zw-1"] });
        var worker = new EllaiQualitySweep(workerDb,
            new EllaiQualityWork(workerDb, fixture.ClassificationPayloadStore(workerDb), settings), settings);
        var config = new EllaiQualityScheduleOptions { Enabled = true, Workspaces = [scope.WorkspaceId], PolicyVersion = "policy/1", BatchSize = 3 };
        using var client = new NoInferenceClient();
        var agent = EllaiFrameworkAgentFactory.Instance.Create(client, new ChatClientAgentOptions { Name = "test", UseProvidedChatClientAsIs = true });
        async Task<AgentSession> Open(CancellationToken ct) => await agent.CreateSessionAsync(cancellationToken: ct);
        var progress = new EllaiQualitySweepProgress();
        var pending = worker.RunAsync(scope.WorkspaceId, config, Open, progress, default);
        try
        {
            await barrier.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
            const string emptyAssessment = "{}";
            await fixture.Db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO swiftaid_classification.quality_assessments
                    ("SourceEventId", "Version", "CaseId", "ReportRevision", "AssessmentJson", "CreatedAtUtc")
                VALUES ({source.EventId}, {ReportQualityRules.Version}, {source.CaseId}, {source.ReportRevision}, {emptyAssessment}::jsonb, clock_timestamp())
                """);
        }
        finally { barrier.Release.TrySetResult(); }
        Assert.Equal("complete", await pending.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal(3, progress.WorkTurns);
        Assert.Equal(1, progress.ChangesCommitted);
        Assert.Equal(2, await fixture.Db.Set<ReportQualityEntity>().AsNoTracking().CountAsync(item => caseIds.Contains(item.CaseId)));
        Assert.Equal(1, await fixture.Db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM ellai_case_quality_activity WHERE run_id = {progress.RunId}").SingleAsync());
        Assert.Equal(0, client.Calls);
    }

    private sealed class QualityInsertBarrier : DbCommandInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _waited;

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO swiftaid_classification.quality_assessments", StringComparison.Ordinal) &&
                Interlocked.Exchange(ref _waited, 1) == 0)
            {
                Reached.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    private sealed class NoInferenceClient : IChatClient
    {
        public int Calls { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Unexpected inference.");
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            await Task.Yield();
            throw new InvalidOperationException("Unexpected inference.");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
