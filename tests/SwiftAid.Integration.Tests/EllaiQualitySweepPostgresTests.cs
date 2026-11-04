using System.Runtime.CompilerServices;
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
        Assert.Equal("disabled", await sweep.RunAsync(scope.WorkspaceId, new EllaiQualityScheduleOptions(), Open, default));
        Assert.Equal("disabled", await sweep.RunAsync("other", options, Open, default));
        var schedules = new PostgresEllaiOperationalScheduleStore(fixture.Db);
        Assert.True(await schedules.CreateAsync(scope.WorkspaceId, "ops.quality", 4, "policy/1", DateTimeOffset.UtcNow.AddMinutes(-1), default));
        Assert.Equal("policy-mismatch", await sweep.RunAsync(scope.WorkspaceId, new EllaiQualityScheduleOptions { Enabled = true, Workspaces = [scope.WorkspaceId], PolicyVersion = "policy/2" }, Open, default));
        Assert.Empty(sessions);
        Assert.Equal("batch-limit", await sweep.RunAsync(scope.WorkspaceId, options, Open, default));
        Assert.Equal("batch-limit", await sweep.RunAsync(scope.WorkspaceId, options, Open, default));
        Assert.Equal("complete", await sweep.RunAsync(scope.WorkspaceId, options, Open, default));
        Assert.Equal("not-due", await sweep.RunAsync(scope.WorkspaceId, options, Open, default));
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
        Assert.Equal("scope-unavailable", await deniedSweep.RunAsync(scope.WorkspaceId, options, Open, default));
        var schedules = new PostgresEllaiOperationalScheduleStore(fixture.Db);
        Assert.True(await schedules.CreateAsync(scope.WorkspaceId, "ops.quality", 4, "policy/1", DateTimeOffset.UtcNow.AddMinutes(-1), default));
        await using var replicaDb = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var first = new EllaiQualitySweep(fixture.Db, new EllaiQualityWork(fixture.Db, fixture.ClassificationPayloadStore(fixture.Db), settings), settings);
        var second = new EllaiQualitySweep(replicaDb, new EllaiQualityWork(replicaDb, fixture.ClassificationPayloadStore(replicaDb), settings), settings);
        var results = await Task.WhenAll(first.RunAsync(scope.WorkspaceId, options, Open, default), second.RunAsync(scope.WorkspaceId, options, Open, default));
        Assert.Contains("complete", results);
        Assert.Contains("not-due", results);
        Assert.Equal(1, await fixture.Db.Set<ReportQualityEntity>().AsNoTracking().CountAsync(item => item.CaseId == caseId));
        Assert.Equal(0, client.Calls);
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
