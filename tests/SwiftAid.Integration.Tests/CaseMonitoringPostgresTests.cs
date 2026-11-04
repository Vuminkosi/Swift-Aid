using System.Security.Claims;
using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Components.Authorization;
using SwiftAid.Gateway.Cases;
using Microsoft.Extensions.AI;
using SwiftAid.Gateway.Ellai;
using SwiftAid.Intelligence.Meta;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SwiftAid.Infrastructure.Classification;
using SwiftAid.Infrastructure.Persistence.Ellai;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Ellai;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Application.Serialization;
using SwiftAid.Contracts.Commands;
using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Events;
using SwiftAid.Domain.Cases;
using SwiftAid.Infrastructure.Authorization;
using SwiftAid.Infrastructure.Configuration;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Processing;
using SwiftAid.Infrastructure.Persistence.Protection;
using SwiftAid.Infrastructure.Persistence.Repositories.Cases;
using SwiftAid.Infrastructure.Persistence.Transactions;

namespace SwiftAid.Integration.Tests;

/// <summary>
/// Opt-in PostgreSQL tests for the P5.2 case-monitoring persistence slice: observation,
/// follow-up, and escalation commands stage journal rows and maintain the monitoring
/// read models inside the same lease transaction, and the monitoring summary read
/// re-verifies <c>ReadCase</c>. Uses the same disposable loopback database gate as
/// <see cref="IntakePostgresFactAttribute"/>.
/// </summary>
[Trait("Category", "IntakePostgres")]
[Collection(IntakePostgresCollection.Name)]
public sealed class CaseMonitoringPostgresTests
{
    private static readonly DateTimeOffset FirstDueAtUtc = new(2026, 10, 30, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SecondDueAtUtc = new(2026, 11, 2, 8, 0, 0, TimeSpan.Zero);

    [IntakePostgresFact]
    public async Task FollowUpScheduledSweepDrainsBoundedBatchesWithSeparateSessions()
    {
        // Immutable recommendation rows require a disposable database.
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        await SeedMonitoringScopeAsync(db, [CaseOperation.ReadCase, CaseOperation.ManageFollowUp]);
        using var keyRing = SyntheticMonitoringKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        var caseId = await SeedMonitoredCaseAsync(db, keyRing.CreateOptions(), provider);
        var actor = CreateOfficer("officer-1");
        for (var index = 0; index < 2; index++)
        {
            var scheduled = await ProcessCommandAsync(db, actor, caseId,
                new ScheduleFollowUpCommand(Guid.NewGuid(), 2 + index, Guid.NewGuid(), "synthetic", "officer-1",
                    FirstDueAtUtc.AddMinutes(index), "staff scheduled", null, DateTimeOffset.UtcNow), keyRing.CreateOptions(), provider);
            Assert.Equal(CaseCommandProcessingStatus.Accepted, scheduled.Status);
        }
        var settings = Options.Create(new EllaiFollowUpOptions { Enabled = true, Organizations = ["org-zw-1"] });
        var sweep = new EllaiFollowUpSweep(db, new EllaiFollowUpWork(db, settings), settings);
        var options = new EllaiFollowUpScheduleOptions { Enabled = true, Workspaces = ["workspace-1"], PolicyVersion = "policy/1", BatchSize = 1 };
        var initialSessions = await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM ellai_sessions").SingleAsync();
        using var client = new FollowUpNoInferenceClient();
        var agent = EllaiFrameworkAgentFactory.Instance.Create(client, new ChatClientAgentOptions { Name = "test", UseProvidedChatClientAsIs = true });
        var sessions = new List<AgentSession>();
        async Task<AgentSession> Open(CancellationToken ct)
        {
            var session = await agent.CreateSessionAsync(cancellationToken: ct);
            sessions.Add(session);
            return session;
        }
        Assert.Equal("disabled", await sweep.RunAsync("workspace-1", new EllaiFollowUpScheduleOptions(), Open, new EllaiFollowUpSweepProgress(), default));
        Assert.Equal("disabled", await sweep.RunAsync("other", options, Open, new EllaiFollowUpSweepProgress(), default));
        var deniedSettings = Options.Create(new EllaiFollowUpOptions { Enabled = true, Organizations = ["other-org"] });
        var deniedSweep = new EllaiFollowUpSweep(db, new EllaiFollowUpWork(db, deniedSettings), deniedSettings);
        Assert.Equal("scope-unavailable", await deniedSweep.RunAsync("workspace-1", options, Open, new EllaiFollowUpSweepProgress(), default));
        var schedules = new PostgresEllaiOperationalScheduleStore(db);
        Assert.True(await schedules.CreateAsync("workspace-1", "ops.followup", 4, "policy/1", DateTimeOffset.UtcNow.AddMinutes(-1), default));
        var mismatched = new EllaiFollowUpScheduleOptions { Enabled = true, Workspaces = ["workspace-1"], PolicyVersion = "policy/2" };
        Assert.Equal("policy-mismatch", await sweep.RunAsync("workspace-1", mismatched, Open, new EllaiFollowUpSweepProgress(), default));
        Assert.Empty(sessions);
        var first = new EllaiFollowUpSweepProgress();
        Assert.Equal("batch-limit", await sweep.RunAsync("workspace-1", options, Open, first, default));
        Assert.Equal(1, first.ChangesCommitted);
        Assert.Equal(1, await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM ellai_follow_up_recommendations WHERE run_id = {first.RunId}").SingleAsync());
        Assert.Equal("batch-limit", await sweep.RunAsync("workspace-1", options, Open, new EllaiFollowUpSweepProgress(), default));
        var empty = new EllaiFollowUpSweepProgress();
        Assert.Equal("complete", await sweep.RunAsync("workspace-1", options, Open, empty, default));
        Assert.Equal(0, empty.ChangesCommitted);
        Assert.Equal(0, await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM ellai_follow_up_recommendations WHERE run_id = {empty.RunId}").SingleAsync());
        Assert.Equal("not-due", await sweep.RunAsync("workspace-1", options, Open, new EllaiFollowUpSweepProgress(), default));
        Assert.Equal(3, sessions.Count);
        Assert.Equal(3, sessions.Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.Equal(0, client.Calls);

        // Exercise the actual hosted worker against another committed staff schedule.
        var third = await ProcessCommandAsync(db, actor, caseId,
            new ScheduleFollowUpCommand(Guid.NewGuid(), 4, Guid.NewGuid(), "synthetic", "officer-1",
                FirstDueAtUtc, "staff scheduled", null, DateTimeOffset.UtcNow), keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, third.Status);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ellai_operational_schedules SET next_due_utc = clock_timestamp() - interval '1 minute' WHERE workspace_id = {"workspace-1"} AND role_id = 'ops.followup'");
        var connection = db.Database.GetConnectionString();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptions<EllaiFollowUpScheduleOptions>>(Options.Create(options));
        services.AddSingleton<IOptions<EllaiFollowUpOptions>>(settings);
        services.AddSingleton<IEllaiFrameworkAgentFactory>(EllaiFrameworkAgentFactory.Instance);
        services.AddKeyedSingleton<IChatClient>(EllaiReasoningRegistration.FrameworkClientKey, client);
        services.AddScoped(_ => new SwiftAidDbContext(new DbContextOptionsBuilder<SwiftAidDbContext>()
            .UseNpgsql(connection).UseOpenIddict().Options));
        services.AddScoped<EllaiFollowUpWork>();
        services.AddScoped<EllaiFollowUpSweep>();
        services.AddSingleton<EllaiFollowUpScheduleWorker>();
        await using var providerServices = services.BuildServiceProvider();
        var hosted = providerServices.GetRequiredService<EllaiFollowUpScheduleWorker>();
        await hosted.StartAsync(default);
        try
        {
            var count = 0;
            for (var attempt = 0; attempt < 100 && count < 3; attempt++)
            {
                count = await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM ellai_follow_up_recommendations WHERE case_id = {caseId}").SingleAsync();
                if (count < 3) await Task.Delay(100);
            }
            Assert.Equal(3, count);
            Assert.Equal(initialSessions, await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM ellai_sessions").SingleAsync());
            Assert.Equal(0, client.Calls);
        }
        finally { await hosted.StopAsync(default); }
    }

    private sealed class OfficerAuthenticationStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, "officer-1"),
                new Claim(ActorContext.AccessEpochClaimType, "7")], "fixture"))));
    }

    private sealed class FollowUpNoInferenceClient : IChatClient
    {
        public int Calls { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
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

    [IntakePostgresFact]
    public async Task FollowUpRecommendationIsFencedDeduplicatedAndVisibleInAuthorizedHistory()
    {
        // The immutable recommendation is retained; this test requires a disposable database.
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var followUpGrant = await SeedMonitoringScopeAsync(db, [CaseOperation.ReadCase, CaseOperation.ManageFollowUp]);
        using var keyRing = SyntheticMonitoringKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        var caseId = await SeedMonitoredCaseAsync(db, keyRing.CreateOptions(), provider);
        var actor = CreateOfficer("officer-1");
        var followUpId = Guid.NewGuid();
        var scheduled = await ProcessCommandAsync(db, actor, caseId,
            new ScheduleFollowUpCommand(Guid.NewGuid(), 2, followUpId, "synthetic", "officer-1",
                FirstDueAtUtc, "staff scheduled", null, DateTimeOffset.UtcNow), keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, scheduled.Status);
        var schedules = new PostgresEllaiOperationalScheduleStore(db);
        Assert.True(await schedules.CreateAsync("workspace-1", "ops.followup", 4, "policy/1", DateTimeOffset.UtcNow.AddMinutes(-1), default));
        var lease = await schedules.TryClaimAsync("workspace-1", "ops.followup", TimeSpan.FromMinutes(1), default);
        Assert.NotNull(lease);
        var unit = new PostgresCaseCommandUnitOfWork(db);
        var policy = new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(db, unit), new PostgresCaseAccessDescriptorStore(db, unit));
        var timelineQuery = new CaseTimelineQuery(new PostgresCaseTimelineStore(db), policy, unit);
        var broker = new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService(), timeline: timelineQuery);
        var context = new EllaiToolContext("followup-test", "epoch", "public.product-help", null, 4, true,
            DataClassification: EllaiProcessingClassification.UserProvidedNonSensitive, Actor: actor,
            AllowedTools: new HashSet<string> { EllaiToolNames.CaseTimeline }, ConsentPurpose: EllaiConsentService.PublicProductHelpPurpose);
        var request = new EllaiToolRequest(EllaiToolNames.CaseTimeline, new Dictionary<string, string> { ["case_id"] = caseId.ToString("D") });
        var before = await broker.InvokeAsync(context, request, default);
        Assert.True(before.Allowed);
        var settings = Options.Create(new EllaiFollowUpOptions { Enabled = true, Organizations = ["org-zw-1"] });
        var work = new EllaiFollowUpWork(db, settings);
        var denied = new EllaiFollowUpWork(db, Options.Create(new EllaiFollowUpOptions { Enabled = true, Organizations = ["other-org"] }));
        Assert.Equal(EllaiFencedWorkOutcome.NoChange, await denied.RunOneAsync(lease, Guid.NewGuid(), Guid.NewGuid(), default));
        Assert.Equal(EllaiFencedWorkOutcome.StaleLease, await work.RunOneAsync(lease with { FencingToken = lease.FencingToken + 1 }, Guid.NewGuid(), Guid.NewGuid(), default));
        var run = Guid.NewGuid();
        await db.WorkspaceOrganizations.Where(link => link.WorkspaceId == "workspace-1" && link.OrganizationId == "org-zw-1")
            .ExecuteUpdateAsync(update => update.SetProperty(link => link.Status, PersistentResourceStatus.Revoked));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => work.RunOneAsync(lease, run, Guid.NewGuid(), default));
        await db.WorkspaceOrganizations.Where(link => link.WorkspaceId == "workspace-1" && link.OrganizationId == "org-zw-1")
            .ExecuteUpdateAsync(update => update.SetProperty(link => link.Status, PersistentResourceStatus.Active));
        await using var replicaDb = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var replica = new EllaiFollowUpWork(replicaDb, settings);
        var outcomes = await Task.WhenAll(work.RunOneAsync(lease, run, Guid.NewGuid(), default),
            replica.RunOneAsync(lease, run, Guid.NewGuid(), default));
        Assert.Single(outcomes, outcome => outcome == EllaiFencedWorkOutcome.Committed);
        Assert.Single(outcomes, outcome => outcome == EllaiFencedWorkOutcome.NoChange);
        Assert.Equal(EllaiFencedWorkOutcome.NoChange, await work.RunOneAsync(lease, run, Guid.NewGuid(), default));
        var timeline = await timelineQuery.GetAsync(actor, caseId, 20, default);
        var activity = Assert.Single(timeline.Timeline!.Entries.SelectMany(entry => entry.AgentActivities));
        Assert.Equal("ops.followup", activity.RoleId);
        Assert.Equal("Overdue follow-up review recommended", activity.Label);
        Assert.Equal(followUpId, activity.FollowUpId);
        Assert.True(activity.FollowUpIsCurrent);
        Assert.Equal(FirstDueAtUtc, activity.FollowUpDueAtUtc);
        var after = await broker.InvokeAsync(context, request, default);
        Assert.True(after.Allowed);
        Assert.Contains("Overdue follow-up review recommended", after.ResultSummary, StringComparison.Ordinal);
        Assert.Contains("recommendation is current", after.ResultSummary, StringComparison.Ordinal);
        Assert.NotEqual(before.CaseTimelineReplayScope, after.CaseTimelineReplayScope);
        await db.AccessGrantOperations.Where(item => item.GrantGroupId == followUpGrant && item.Operation == CaseOperation.ManageFollowUp)
            .ExecuteDeleteAsync();
        var readOnlyTimeline = await timelineQuery.GetAsync(actor, caseId, 20, default);
        Assert.Equal(CaseTimelineAccess.Found, readOnlyTimeline.Access);
        Assert.Empty(readOnlyTimeline.Timeline!.Entries.SelectMany(entry => entry.AgentActivities));
        var readOnlyEllai = await broker.InvokeAsync(context, request, default);
        Assert.True(readOnlyEllai.Allowed);
        Assert.DoesNotContain("Overdue follow-up review recommended", readOnlyEllai.ResultSummary, StringComparison.Ordinal);
        db.AccessGrantOperations.Add(new AccessGrantOperationEntity { GrantGroupId = followUpGrant, Operation = CaseOperation.ManageFollowUp });
        await db.SaveChangesAsync();
        var reviewer = new PostgresFollowUpRecommendationReviewService(db, unit, policy);
        var commandId = Guid.NewGuid();
        Assert.Equal(FollowUpRecommendationReviewOutcome.NotFoundOrNotAuthorized,
            await reviewer.ReviewAsync(CreateOfficer("unknown-" + Guid.NewGuid()), caseId, activity.ActivityId, commandId, default));
        await using var reviewerDb = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var reviewerUnit = new PostgresCaseCommandUnitOfWork(reviewerDb);
        var reviewerPolicy = new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(reviewerDb, reviewerUnit),
            new PostgresCaseAccessDescriptorStore(reviewerDb, reviewerUnit));
        var secondReviewer = new PostgresFollowUpRecommendationReviewService(reviewerDb, reviewerUnit, reviewerPolicy);
        using var reviewServices = new ServiceCollection().AddSingleton<IFollowUpRecommendationReviewService>(secondReviewer).BuildServiceProvider();
        var reviewClient = new FollowUpReviewGatewayClient(new OfficerAuthenticationStateProvider(), reviewServices);
        var directReview = reviewer.ReviewAsync(actor, caseId, activity.ActivityId, commandId, default);
        var gatewayReview = reviewClient.ReviewAsync(caseId, activity.ActivityId, commandId, default);
        await Task.WhenAll(directReview, gatewayReview);
        Assert.True((directReview.Result == FollowUpRecommendationReviewOutcome.Recorded && gatewayReview.Result.Status == FollowUpReviewStatus.Replayed) ||
            (directReview.Result == FollowUpRecommendationReviewOutcome.Replayed && gatewayReview.Result.Status == FollowUpReviewStatus.Recorded));
        Assert.Equal(FollowUpRecommendationReviewOutcome.Replayed, await reviewer.ReviewAsync(actor, caseId, activity.ActivityId, commandId, default));
        Assert.Equal(FollowUpRecommendationReviewOutcome.Conflict, await reviewer.ReviewAsync(actor, caseId, Guid.NewGuid(), commandId, default));
        Assert.Equal(FollowUpRecommendationReviewOutcome.AlreadyReviewed, await reviewer.ReviewAsync(actor, caseId, activity.ActivityId, Guid.NewGuid(), default));
        var reviewed = await broker.InvokeAsync(context, request, default);
        Assert.Contains("Staff review recorded", reviewed.ResultSummary, StringComparison.Ordinal);
        Assert.NotEqual(after.CaseTimelineReplayScope, reviewed.CaseTimelineReplayScope);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE ellai_follow_up_reviews SET actor_id = 'changed' WHERE recommendation_id = {activity.ActivityId}"));
        var inaccessible = await broker.InvokeAsync(context with { Actor = CreateOfficer("unknown-" + Guid.NewGuid()) }, request, default);
        Assert.False(inaccessible.Allowed);
        Assert.Empty(inaccessible.Evidence);
        Assert.Equal(FollowUpStatus.Scheduled, (await db.ManagedCaseFollowUps.AsNoTracking().SingleAsync(f => f.FollowUpId == followUpId)).Status);
        var rescheduled = await ProcessCommandAsync(db, actor, caseId,
            new RecordFollowUpOutcomeCommand(Guid.NewGuid(), 3, followUpId, FollowUpOutcome.Rescheduled,
                "staff changed schedule", DateTimeOffset.UtcNow.AddDays(30), "rescheduled", null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, rescheduled.Status);
        Assert.Equal(EllaiFencedWorkOutcome.NoChange, await work.RunOneAsync(lease, run, Guid.NewGuid(), default));
        timeline = await new CaseTimelineQuery(new PostgresCaseTimelineStore(db), policy, unit).GetAsync(actor, caseId, 20, default);
        var historical = Assert.Single(timeline.Timeline!.Entries.SelectMany(entry => entry.AgentActivities));
        Assert.False(historical.FollowUpIsCurrent);
        var afterReschedule = await broker.InvokeAsync(context, request, default);
        Assert.Contains("recommendation is historical", afterReschedule.ResultSummary, StringComparison.Ordinal);
        Assert.NotEqual(reviewed.CaseTimelineReplayScope, afterReschedule.CaseTimelineReplayScope);
        var staleFollowUpId = Guid.NewGuid();
        var scheduledAgain = await ProcessCommandAsync(db, actor, caseId,
            new ScheduleFollowUpCommand(Guid.NewGuid(), 4, staleFollowUpId, "synthetic", "officer-1",
                FirstDueAtUtc, "staff scheduled", null, DateTimeOffset.UtcNow), keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, scheduledAgain.Status);
        Assert.Equal(EllaiFencedWorkOutcome.Committed, await work.RunOneAsync(lease, run, Guid.NewGuid(), default));
        var staleTimeline = await timelineQuery.GetAsync(actor, caseId, 20, default);
        var staleActivity = Assert.Single(staleTimeline.Timeline!.Entries.SelectMany(entry => entry.AgentActivities),
            candidate => candidate.FollowUpId == staleFollowUpId);
        var completed = await ProcessCommandAsync(db, actor, caseId,
            new RecordFollowUpOutcomeCommand(Guid.NewGuid(), 5, staleFollowUpId, FollowUpOutcome.Completed,
                "staff recorded outcome", null, "completed", null, DateTimeOffset.UtcNow), keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, completed.Status);
        Assert.Equal(FollowUpRecommendationReviewOutcome.Stale,
            await reviewer.ReviewAsync(actor, caseId, staleActivity.ActivityId, Guid.NewGuid(), default));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE ellai_follow_up_recommendations SET policy_version = 'changed' WHERE follow_up_id = {followUpId}"));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.GetService<IMigrator>().MigrateAsync("20261104110000_EllaiQualityActivity"));
        Assert.True(await schedules.CompleteAsync(lease, default));
    }

    [IntakePostgresFact]
    public async Task TimelineWorkSummaryReflectsAuthorizedEscalationAndFollowUpState()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var grantGroupId = await SeedMonitoringScopeAsync(db,
            [CaseOperation.ReadCase, CaseOperation.ManageFollowUp, CaseOperation.ManageEscalation]);
        using var keyRing = SyntheticMonitoringKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        var caseId = await SeedMonitoredCaseAsync(db, keyRing.CreateOptions(), provider);
        await using var cleanup = new CaseMonitoringCleanup(caseId, grantGroupId);
        var actor = CreateOfficer("officer-1");
        var scheduled = await ProcessCommandAsync(db, actor, caseId,
            new ScheduleFollowUpCommand(Guid.NewGuid(), 2, Guid.NewGuid(), "synthetic", "officer-1",
                FirstDueAtUtc, "staff scheduled", null, DateTimeOffset.UtcNow), keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, scheduled.Status);
        var escalationId = Guid.NewGuid();
        var raised = await ProcessCommandAsync(db, actor, caseId,
            new RaiseEscalationCommand(Guid.NewGuid(), 3, escalationId, "synthetic", [], "duty-officer", null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, raised.Status);
        var unit = new PostgresCaseCommandUnitOfWork(db);
        var policy = new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(db, unit), new PostgresCaseAccessDescriptorStore(db, unit));
        var query = new CaseTimelineQuery(new PostgresCaseTimelineStore(db), policy, unit);
        var timeline = await query.GetAsync(actor, caseId, 1, default);
        Assert.Equal(new CaseTimelineWorkSummary(1, 1, FirstDueAtUtc), timeline.Timeline!.WorkSummary);
        var closed = await ProcessCommandAsync(db, actor, caseId,
            new AcknowledgeEscalationCommand(Guid.NewGuid(), 4, escalationId, null, "acknowledged", null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, closed.Status);
        closed = await ProcessCommandAsync(db, actor, caseId,
            new RecordEscalationActionCommand(Guid.NewGuid(), 5, escalationId, "action", "recorded", null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, closed.Status);
        closed = await ProcessCommandAsync(db, actor, caseId,
            new CloseEscalationCommand(Guid.NewGuid(), 6, escalationId, "closed", "resolved", null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, closed.Status);
        timeline = await query.GetAsync(actor, caseId, 1, default);
        Assert.Equal(new CaseTimelineWorkSummary(0, 1, FirstDueAtUtc), timeline.Timeline!.WorkSummary);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            query.GetAsync(CreateOfficer("unauthorized-" + Guid.NewGuid()), caseId, 1, default));
    }

    [IntakePostgresFact]
    public async Task AssignedWorkerAcknowledgement_PersistsAndRemainsDueInAuthorizedQueue()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var grantGroupId = await SeedMonitoringScopeAsync(db,
            [CaseOperation.ReadCase, CaseOperation.ManageFollowUp]);
        using var keyRing = SyntheticMonitoringKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        var caseId = await SeedMonitoredCaseAsync(db, keyRing.CreateOptions(), provider);
        await using var cleanup = new CaseMonitoringCleanup(caseId, grantGroupId);
        var actor = CreateOfficer("officer-1");
        var followUpId = Guid.NewGuid();

        var scheduled = await ProcessCommandAsync(db, actor, caseId,
            new ScheduleFollowUpCommand(Guid.NewGuid(), 2, followUpId, "record next observation", "officer-1",
                FirstDueAtUtc, "staff scheduled", null, DateTimeOffset.UtcNow), keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, scheduled.Status);
        var acknowledged = await ProcessCommandAsync(db, actor, caseId,
            new AcknowledgeFollowUpCommand(Guid.NewGuid(), 3, followUpId, null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, acknowledged.Status);
        Assert.Equal(4, acknowledged.AggregateVersion);

        await using var verify = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var projection = await verify.ManagedCaseFollowUps.AsNoTracking().SingleAsync(item => item.FollowUpId == followUpId);
        Assert.Equal(FollowUpStatus.Acknowledged, projection.Status);
        var eventRow = await verify.CaseEvents.AsNoTracking().SingleAsync(item => item.AggregateVersion == 4 && item.CaseId == caseId);
        Assert.Equal(nameof(CaseEventType.CaseFollowUpAcknowledged), eventRow.EventType);
        Assert.Equal("officer-1", eventRow.ActorId);

        var queue = await QueryQueueAsync(actor, FirstDueAtUtc.AddDays(2), keyRing.CreateOptions(), provider);
        var item = Assert.Single(queue.Items);
        Assert.Equal("Acknowledged", item.Status);
        Assert.True(item.IsOverdue);
    }

    [IntakePostgresFact]
    public async Task ScheduleFollowUp_then_CompletedOutcome_closes_follow_up()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var grantGroupId = await SeedMonitoringScopeAsync(db,
            [CaseOperation.ReadCase, CaseOperation.ManageFollowUp]);
        using var keyRing = SyntheticMonitoringKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        var caseId = await SeedMonitoredCaseAsync(db, keyRing.CreateOptions(), provider);
        await using var cleanup = new CaseMonitoringCleanup(caseId, grantGroupId);
        var actor = CreateOfficer("officer-1");

        var followUpId = Guid.NewGuid();
        var schedule = new ScheduleFollowUpCommand(
            Guid.NewGuid(), 2, followUpId, "revisit holding", "officer-1",
            FirstDueAtUtc, "routine check", null, DateTimeOffset.UtcNow);
        var scheduled = await ProcessCommandAsync(db, actor, caseId, schedule, keyRing.CreateOptions(), provider);

        Assert.Equal(CaseCommandProcessingStatus.Accepted, scheduled.Status);
        Assert.Equal(3, scheduled.AggregateVersion);

        var completed = new RecordFollowUpOutcomeCommand(
            Guid.NewGuid(), 3, followUpId, FollowUpOutcome.Completed,
            "holding calm, no concerns", null, "visit complete", null, DateTimeOffset.UtcNow);
        var outcome = await ProcessCommandAsync(db, actor, caseId, completed, keyRing.CreateOptions(), provider);

        Assert.Equal(CaseCommandProcessingStatus.Accepted, outcome.Status);
        Assert.Equal(4, outcome.AggregateVersion);

        await using var verify = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var row = await verify.ManagedCaseFollowUps.AsNoTracking()
            .SingleAsync(item => item.FollowUpId == followUpId);
        Assert.Equal(caseId, row.CaseId);
        Assert.Equal("revisit holding", row.Purpose);
        Assert.Equal("officer-1", row.AssigneeActorId);
        Assert.Equal(FirstDueAtUtc, row.DueAtUtc);
        Assert.Equal(FollowUpStatus.Completed, row.Status);
        Assert.Equal(3, row.ScheduledVersion);
        Assert.Equal(4, row.OutcomeVersion);

        var summary = await QuerySummaryAsync(db, actor, caseId, keyRing.CreateOptions(), provider);
        Assert.Equal(CaseMonitoringSummaryAccess.Found, summary.Access);
        Assert.NotNull(summary.Summary);
        Assert.Empty(summary.Summary!.OpenFollowUps);
        Assert.Empty(summary.Summary.Escalations);
        Assert.Null(summary.Summary.LatestObservation);
    }

    [IntakePostgresFact]
    public async Task MissedOutcome_then_Rescheduled_preserves_prior_due()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var grantGroupId = await SeedMonitoringScopeAsync(db,
            [CaseOperation.ReadCase, CaseOperation.ManageFollowUp]);
        using var keyRing = SyntheticMonitoringKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        var caseId = await SeedMonitoredCaseAsync(db, keyRing.CreateOptions(), provider);
        await using var cleanup = new CaseMonitoringCleanup(caseId, grantGroupId);
        var actor = CreateOfficer("officer-1");

        var followUpId = Guid.NewGuid();
        var scheduled = await ProcessCommandAsync(db, actor, caseId,
            new ScheduleFollowUpCommand(Guid.NewGuid(), 2, followUpId, "revisit holding", "officer-1",
                FirstDueAtUtc, "routine check", null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, scheduled.Status);
        var scheduleEventId = Assert.Single(scheduled.EventIds);

        var missed = await ProcessCommandAsync(db, actor, caseId,
            new RecordFollowUpOutcomeCommand(Guid.NewGuid(), 3, followUpId, FollowUpOutcome.Missed,
                "nobody present at holding", null, "attempt logged", null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, missed.Status);
        Assert.Equal(4, missed.AggregateVersion);

        await using (var verify = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync())
        {
            var row = await verify.ManagedCaseFollowUps.AsNoTracking()
                .SingleAsync(item => item.FollowUpId == followUpId);
            Assert.Equal(FollowUpStatus.Missed, row.Status);
            Assert.Equal(FirstDueAtUtc, row.DueAtUtc);
            Assert.Equal(4, row.OutcomeVersion);
            Assert.Equal(3, row.CurrentScheduleVersion);
            Assert.Equal(scheduleEventId, row.CurrentScheduleEventId);
        }

        var openAfterMiss = await QuerySummaryAsync(db, actor, caseId, keyRing.CreateOptions(), provider);
        var missedItem = Assert.Single(openAfterMiss.Summary!.OpenFollowUps);
        Assert.Equal(followUpId, missedItem.FollowUpId);
        Assert.Equal("Missed", missedItem.Status);
        Assert.Equal(FirstDueAtUtc, missedItem.DueAtUtc);

        var rescheduled = await ProcessCommandAsync(db, actor, caseId,
            new RecordFollowUpOutcomeCommand(Guid.NewGuid(), 4, followUpId, FollowUpOutcome.Rescheduled,
                "retry after market day", SecondDueAtUtc, "new attempt", null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, rescheduled.Status);
        Assert.Equal(5, rescheduled.AggregateVersion);
        var rescheduledEventId = Assert.Single(rescheduled.EventIds);

        await using (var verify = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync())
        {
            var row = await verify.ManagedCaseFollowUps.AsNoTracking()
                .SingleAsync(item => item.FollowUpId == followUpId);
            Assert.Equal(FollowUpStatus.Scheduled, row.Status);
            Assert.Equal(SecondDueAtUtc, row.DueAtUtc);
            Assert.Equal(3, row.ScheduledVersion);
            Assert.Equal(5, row.OutcomeVersion);
            Assert.Equal(5, row.CurrentScheduleVersion);
            Assert.Equal(rescheduledEventId, row.CurrentScheduleEventId);

            var outcomeEvents = await verify.CaseEvents.AsNoTracking()
                .Where(item => item.CaseId == caseId && item.EventType == "CaseFollowUpOutcomeRecorded")
                .OrderBy(item => item.AggregateVersion)
                .Select(item => item.AggregateVersion)
                .ToArrayAsync();
            Assert.Equal([4L, 5L], outcomeEvents);
        }

        var openAfterReschedule = await QuerySummaryAsync(db, actor, caseId, keyRing.CreateOptions(), provider);
        var rescheduledItem = Assert.Single(openAfterReschedule.Summary!.OpenFollowUps);
        Assert.Equal(followUpId, rescheduledItem.FollowUpId);
        Assert.Equal("Scheduled", rescheduledItem.Status);
        Assert.Equal(SecondDueAtUtc, rescheduledItem.DueAtUtc);
    }

    [IntakePostgresFact]
    public async Task UnreachableOutcome_does_not_complete_follow_up()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var grantGroupId = await SeedMonitoringScopeAsync(db,
            [CaseOperation.ReadCase, CaseOperation.ManageFollowUp]);
        using var keyRing = SyntheticMonitoringKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        var caseId = await SeedMonitoredCaseAsync(db, keyRing.CreateOptions(), provider);
        await using var cleanup = new CaseMonitoringCleanup(caseId, grantGroupId);
        var actor = CreateOfficer("officer-1");

        var followUpId = Guid.NewGuid();
        var scheduled = await ProcessCommandAsync(db, actor, caseId,
            new ScheduleFollowUpCommand(Guid.NewGuid(), 2, followUpId, "contact reporter", "officer-1",
                FirstDueAtUtc, "callback", null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, scheduled.Status);

        var unreachable = await ProcessCommandAsync(db, actor, caseId,
            new RecordFollowUpOutcomeCommand(Guid.NewGuid(), 3, followUpId, FollowUpOutcome.Unreachable,
                "phone off, holding gate locked", null, "attempt logged", null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, unreachable.Status);

        await using var verify = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var row = await verify.ManagedCaseFollowUps.AsNoTracking()
            .SingleAsync(item => item.FollowUpId == followUpId);
        Assert.Equal(FollowUpStatus.Unreachable, row.Status);
        Assert.Equal(FirstDueAtUtc, row.DueAtUtc);

        var summary = await QuerySummaryAsync(db, actor, caseId, keyRing.CreateOptions(), provider);
        var item = Assert.Single(summary.Summary!.OpenFollowUps);
        Assert.Equal("Unreachable", item.Status);
    }

    [IntakePostgresFact]
    public async Task Escalation_raise_acknowledge_close_keeps_owner()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var grantGroupId = await SeedMonitoringScopeAsync(db,
            [CaseOperation.ReadCase, CaseOperation.RecordObservation, CaseOperation.ManageEscalation]);
        using var keyRing = SyntheticMonitoringKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        var caseId = await SeedMonitoredCaseAsync(db, keyRing.CreateOptions(), provider);
        await using var cleanup = new CaseMonitoringCleanup(caseId, grantGroupId);
        var actor = CreateOfficer("officer-1");

        var observationId = Guid.NewGuid();
        var observed = await ProcessCommandAsync(db, actor, caseId,
            new RecordObservationCommand(Guid.NewGuid(), 2, observationId,
                "holding gate locked two visits running", "home-visit", "visit note", null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, observed.Status);
        var evidenceId = Assert.Single(observed.EventIds).ToString("D");

        var escalationId = Guid.NewGuid();
        var raised = await ProcessCommandAsync(db, actor, caseId,
            new RaiseEscalationCommand(Guid.NewGuid(), 3, escalationId,
                "holding unreachable twice", [evidenceId], "duty-officer", null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, raised.Status);
        Assert.Equal(4, raised.AggregateVersion);

        var acknowledged = await ProcessCommandAsync(db, actor, caseId,
            new AcknowledgeEscalationCommand(Guid.NewGuid(), 4, escalationId,
                null, "acknowledged", null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, acknowledged.Status);

        var actionRecorded = await ProcessCommandAsync(db, actor, caseId,
            new RecordEscalationActionCommand(Guid.NewGuid(), 5, escalationId,
                "Supervisor paged", "Duty officer coordinated a visit.", null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, actionRecorded.Status);

        var closed = await ProcessCommandAsync(db, actor, caseId,
            new CloseEscalationCommand(Guid.NewGuid(), 6, escalationId,
                "supervisor visited holding", "resolved", null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, closed.Status);
        Assert.Equal(7, closed.AggregateVersion);

        await using var verify = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var row = await verify.ManagedCaseEscalations.AsNoTracking()
            .SingleAsync(item => item.EscalationId == escalationId);
        Assert.Equal(caseId, row.CaseId);
        Assert.Equal(EscalationStatus.Closed, row.Status);
        Assert.Equal(4, row.RaisedVersion);
        Assert.Equal(5, row.AcknowledgedVersion);
        Assert.Equal(6, row.ActionRecordedVersion);
        Assert.Equal("Supervisor paged", row.ActionSummary);
        Assert.Equal(7, row.ClosedVersion);

        var projection = await verify.ManagedCases.AsNoTracking()
            .SingleAsync(item => item.CaseId == caseId);
        Assert.Equal("officer-1", projection.AccountableOwnerActorId);
        var epochs = await verify.ActorAccessStates.AsNoTracking()
            .Where(item => item.ActorId == "officer-1")
            .Select(item => item.AccessEpoch)
            .ToArrayAsync();
        Assert.Equal([7L], epochs);

        var summary = await QuerySummaryAsync(db, actor, caseId, keyRing.CreateOptions(), provider);
        var item = Assert.Single(summary.Summary!.Escalations);
        Assert.Equal(escalationId, item.EscalationId);
        Assert.Equal("Closed", item.Status);
    }

    [IntakePostgresFact]
    public async Task RecordObservation_stored_and_summarized_as_latest()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var grantGroupId = await SeedMonitoringScopeAsync(db,
            [CaseOperation.ReadCase, CaseOperation.RecordObservation]);
        using var keyRing = SyntheticMonitoringKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        var caseId = await SeedMonitoredCaseAsync(db, keyRing.CreateOptions(), provider);
        await using var cleanup = new CaseMonitoringCleanup(caseId, grantGroupId);
        var actor = CreateOfficer("officer-1");

        var firstId = Guid.NewGuid();
        var first = await ProcessCommandAsync(db, actor, caseId,
            new RecordObservationCommand(Guid.NewGuid(), 2, firstId,
                "subject resting, breathing normal", "home-visit", "visit note", null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, first.Status);

        var secondId = Guid.NewGuid();
        var second = await ProcessCommandAsync(db, actor, caseId,
            new RecordObservationCommand(Guid.NewGuid(), 3, secondId,
                "water point functional", "phone-check", "callback note", null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, second.Status);

        await using (var verify = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync())
        {
            Assert.Equal(2, await verify.ManagedCaseObservations.AsNoTracking()
                .CountAsync(item => item.CaseId == caseId));
            var firstRow = await verify.ManagedCaseObservations.AsNoTracking()
                .SingleAsync(item => item.ObservationId == firstId);
            Assert.Equal("subject resting, breathing normal", firstRow.Narrative);
            Assert.Equal("home-visit", firstRow.Source);
            Assert.Equal(3, firstRow.AggregateVersion);
        }

        var summary = await QuerySummaryAsync(db, actor, caseId, keyRing.CreateOptions(), provider);
        Assert.NotNull(summary.Summary!.LatestObservation);
        Assert.Equal(secondId, summary.Summary.LatestObservation!.ObservationId);
        Assert.Equal("water point functional", summary.Summary.LatestObservation.Narrative);
        Assert.Equal("phone-check", summary.Summary.LatestObservation.Source);
        Assert.Equal(4, summary.Summary.LatestObservation.AggregateVersion);
    }

    [IntakePostgresFact]
    public async Task MonitoringCommand_replay_is_idempotent()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var grantGroupId = await SeedMonitoringScopeAsync(db,
            [CaseOperation.ReadCase, CaseOperation.RecordObservation]);
        using var keyRing = SyntheticMonitoringKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        var caseId = await SeedMonitoredCaseAsync(db, keyRing.CreateOptions(), provider);
        await using var cleanup = new CaseMonitoringCleanup(caseId, grantGroupId);
        var actor = CreateOfficer("officer-1");

        var command = new RecordObservationCommand(Guid.NewGuid(), 2, Guid.NewGuid(),
            "subject resting, breathing normal", "home-visit", "visit note", null, DateTimeOffset.UtcNow);
        var first = await ProcessCommandAsync(db, actor, caseId, command, keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, first.Status);

        var replay = await ProcessCommandAsync(db, actor, caseId, command, keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Replayed, replay.Status);
        Assert.True(first.EventIds.SequenceEqual(replay.EventIds));
        Assert.Equal(first.AggregateVersion, replay.AggregateVersion);

        await using var verify = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        Assert.Equal(1, await verify.ManagedCaseObservations.AsNoTracking()
            .CountAsync(item => item.CaseId == caseId));
        Assert.Equal(1, await verify.CaseEvents.AsNoTracking()
            .CountAsync(item => item.CaseId == caseId && item.CommandId == command.CommandId));
    }

    [IntakePostgresFact]
    public async Task StaleExpectedVersion_conflicts()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var grantGroupId = await SeedMonitoringScopeAsync(db,
            [CaseOperation.ReadCase, CaseOperation.RecordObservation, CaseOperation.ManageFollowUp]);
        using var keyRing = SyntheticMonitoringKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        var caseId = await SeedMonitoredCaseAsync(db, keyRing.CreateOptions(), provider);
        await using var cleanup = new CaseMonitoringCleanup(caseId, grantGroupId);
        var actor = CreateOfficer("officer-1");

        var observed = await ProcessCommandAsync(db, actor, caseId,
            new RecordObservationCommand(Guid.NewGuid(), 2, Guid.NewGuid(),
                "subject resting, breathing normal", "home-visit", "visit note", null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, observed.Status);
        Assert.Equal(3, observed.AggregateVersion);

        var stale = await ProcessCommandAsync(db, actor, caseId,
            new ScheduleFollowUpCommand(Guid.NewGuid(), 2, Guid.NewGuid(), "revisit holding", "officer-1",
                FirstDueAtUtc, "routine check", null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Conflict, stale.Status);

        await using var verify = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        Assert.Equal(0, await verify.ManagedCaseFollowUps.AsNoTracking()
            .CountAsync(item => item.CaseId == caseId));
    }

    [IntakePostgresFact]
    public async Task MissingMonitoringOperation_denies_command()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var grantGroupId = await SeedMonitoringScopeAsync(db, [CaseOperation.ReadCase]);
        using var keyRing = SyntheticMonitoringKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        var caseId = await SeedMonitoredCaseAsync(db, keyRing.CreateOptions(), provider);
        await using var cleanup = new CaseMonitoringCleanup(caseId, grantGroupId);
        var actor = CreateOfficer("officer-1");

        var denied = await ProcessCommandAsync(db, actor, caseId,
            new RecordObservationCommand(Guid.NewGuid(), 2, Guid.NewGuid(),
                "subject resting, breathing normal", "home-visit", "visit note", null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Denied, denied.Status);

        await using var verify = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        Assert.Equal(0, await verify.ManagedCaseObservations.AsNoTracking()
            .CountAsync(item => item.CaseId == caseId));
        Assert.Equal(0, await verify.CaseEvents.AsNoTracking()
            .CountAsync(item => item.CaseId == caseId && item.AggregateVersion > 2));
    }

    [IntakePostgresFact]
    public async Task FollowUp_schedule_denies_assignee_without_active_case_scope()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var grantGroupId = await SeedMonitoringScopeAsync(db,
            [CaseOperation.ReadCase, CaseOperation.ManageFollowUp]);
        using var keyRing = SyntheticMonitoringKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        var caseId = await SeedMonitoredCaseAsync(db, keyRing.CreateOptions(), provider);
        await using var cleanup = new CaseMonitoringCleanup(caseId, grantGroupId);
        var targetActorId = $"follow-up-unscoped-{Guid.NewGuid():N}";
        await using (var targetSetup = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync())
        {
            var now = DateTimeOffset.UtcNow;
            targetSetup.Actors.Add(new ActorEntity { ActorId = targetActorId, Status = PersistentActorStatus.Active, CreatedAtUtc = now, UpdatedAtUtc = now });
            targetSetup.ActorAccessStates.Add(new ActorAccessStateEntity { ActorId = targetActorId, AccessEpoch = 1, Version = 1, UpdatedAtUtc = now });
            await targetSetup.SaveChangesAsync();
        }

        try
        {
            var denied = await ProcessCommandAsync(db, CreateOfficer("officer-1"), caseId,
                new ScheduleFollowUpCommand(Guid.NewGuid(), 2, Guid.NewGuid(), "synthetic callback", targetActorId,
                    FirstDueAtUtc, "synthetic scheduling reason", null, DateTimeOffset.UtcNow),
                keyRing.CreateOptions(), provider);

            Assert.Equal(CaseCommandProcessingStatus.Denied, denied.Status);
            await using var verify = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
            Assert.Empty(await verify.ManagedCaseFollowUps.AsNoTracking().Where(item => item.CaseId == caseId).ToArrayAsync());
            Assert.Empty(await verify.CaseEvents.AsNoTracking().Where(item => item.CaseId == caseId && item.AggregateVersion > 2).ToArrayAsync());
        }
        finally
        {
            await using var targetCleanup = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
            await targetCleanup.ActorAccessStates.Where(item => item.ActorId == targetActorId).ExecuteDeleteAsync();
            await targetCleanup.Actors.Where(item => item.ActorId == targetActorId).ExecuteDeleteAsync();
        }
    }

    [IntakePostgresFact]
    public async Task Summary_without_read_is_not_found_or_not_authorized()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var grantGroupId = await SeedMonitoringScopeAsync(db, [CaseOperation.RecordObservation]);
        using var keyRing = SyntheticMonitoringKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        var caseId = await SeedMonitoredCaseAsync(db, keyRing.CreateOptions(), provider);
        await using var cleanup = new CaseMonitoringCleanup(caseId, grantGroupId);
        var actor = CreateOfficer("officer-1");

        var denied = await QuerySummaryAsync(db, actor, caseId, keyRing.CreateOptions(), provider);
        Assert.Equal(CaseMonitoringSummaryAccess.NotFoundOrNotAuthorized, denied.Access);
        Assert.Null(denied.Summary);

        var unknown = await QuerySummaryAsync(db, actor, Guid.NewGuid(), keyRing.CreateOptions(), provider);
        Assert.Equal(CaseMonitoringSummaryAccess.NotFoundOrNotAuthorized, unknown.Access);
        Assert.Null(unknown.Summary);
    }

    private static ActorContext CreateOfficer(string actorId) => ActorContext.FromValidatedPrincipal(
        new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, actorId),
            new Claim(ActorContext.AccessEpochClaimType, "7")
        ], "synthetic-test")));

    private const string MonitoringRegionId = "region-zw-harare";

    private static async Task<string> SeedMonitoringScopeAsync(
        SwiftAidDbContext db, IReadOnlyList<CaseOperation> operations)
    {
        await CaseRegistrationBackendTests.SeedRegistrationIdentitiesAsync(db);
        var membershipId = await IntakeDecisionStoreTests.ResetOfficerScopeAsync(db, "officer-1", 7);

        var grantGroupId = $"grant-group-monitoring-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        db.AccessGrantGroups.Add(new AccessGrantGroupEntity
        {
            GrantGroupId = grantGroupId,
            MembershipId = membershipId,
            ActorId = "officer-1",
            OrganizationId = "org-zw-1",
            WorkspaceId = "workspace-1",
            CountryCode = "ZW",
            Sector = Sector.HumanHealth,
            SensitivityCode = "standard",
            CanAccessJurisdictionalCases = false,
            Status = PersistentGrantStatus.Active,
            ValidFromUtc = now.AddHours(-1),
            ExpiresAtUtc = now.AddHours(1),
            GrantedByActorId = "officer-1",
            Version = 1
        });
        foreach (var operation in operations.Distinct())
        {
            db.AccessGrantOperations.Add(new AccessGrantOperationEntity
            {
                GrantGroupId = grantGroupId,
                Operation = operation
            });
        }

        // The shared case resource policy requires every case region to be covered by
        // the grant regions, including for the accountable owner, so the grant carries
        // the same region seeded onto the monitored case below.
        db.AccessGrantRegions.Add(new AccessGrantRegionEntity
        {
            GrantGroupId = grantGroupId,
            RegionId = MonitoringRegionId
        });

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return grantGroupId;
    }

    private static async Task<Guid> SeedMonitoredCaseAsync(
        SwiftAidDbContext db,
        ProtectedPayloadOptions options,
        ProtectedPayloadDataProtectionProvider provider)
    {
        var caseId = await IntakeDecisionStoreTests.SeedManagedCaseAsync(db);
        await db.ManagedCases
            .Where(item => item.CaseId == caseId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.WorkflowVersion, 1));
        db.ManagedCaseRegions.Add(new ManagedCaseRegionEntity
        {
            CaseId = caseId,
            RegionId = MonitoringRegionId
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await SeedInitialJournalAsync(db, caseId, options, provider);
        return caseId;
    }

    private static async Task SeedInitialJournalAsync(
        SwiftAidDbContext db,
        Guid caseId,
        ProtectedPayloadOptions options,
        ProtectedPayloadDataProtectionProvider provider)
    {
        var actor = CreateOfficer("officer-1");
        var unitOfWork = new PostgresCaseCommandUnitOfWork(db);
        var store = new PostgresProtectedCaseEventPayloadStore(db, unitOfWork, options, provider);
        var serializer = new CaseEventSerializer();
        await using var lease = await unitOfWork.BeginCaseAsync(
            actor, caseId, [], CaseLeaseMode.Write, CancellationToken.None);
        var commandId = Guid.NewGuid();
        var recordedAt = DateTimeOffset.UtcNow;
        var report = new ReportFacts(
            CaseKind.Human,
            Sector.HumanHealth,
            "synthetic protected report text",
            Array.AsReadOnly<ReportedObservation>([new ReportedObservation("synthetic observation")]));
        var initialEvents = CaseAggregate.Create(
            new AcceptCaseCommand(
                commandId, caseId, report, "officer-1", "workspace-1", null, recordedAt),
            new ActorCapabilities("officer-1", CaseCapability.AcceptReport | CaseCapability.AssignCase));

        db.CommandReceipts.Add(new CommandReceiptEntity
        {
            ReceiptId = Guid.NewGuid(),
            ActorId = "officer-1",
            CommandId = commandId,
            CaseId = caseId,
            CommandType = "AcceptCase",
            SchemaVersion = 1,
            CanonicalPayloadHash = SHA256.HashData([]),
            FirstAggregateVersion = 1,
            LastAggregateVersion = 2,
            ReportRevision = 1,
            AcceptedAtUtc = recordedAt
        });

        foreach (var @event in initialEvents)
        {
            var eventType = @event.GetType().Name;
            var serialized = serializer.SerializeToUtf8Bytes(@event);
            try
            {
                var payloadId = Guid.NewGuid();
                var reference = await store.StageProtectedAsync(
                    caseId, payloadId, eventType, 1, serialized, CancellationToken.None);
                db.CaseEvents.Add(new CaseEventEntity
                {
                    EventId = @event.EventId,
                    CaseId = @event.CaseId,
                    AggregateVersion = @event.AggregateVersion,
                    ReportRevision = @event.ReportRevision,
                    CommandId = @event.CommandId,
                    ActorId = @event.ActorId,
                    EventType = eventType,
                    SchemaVersion = 1,
                    OccurredAtUtc = @event.OccurredAtUtc,
                    RecordedAtUtc = @event.RecordedAtUtc,
                    PayloadId = reference.PayloadId
                });
            }
            finally
            {
                CryptographicOperations.ZeroMemory(serialized);
            }
        }

        await lease.CommitAsync(CancellationToken.None);
        db.ChangeTracker.Clear();
    }

    private static async Task<CaseCommandProcessingResult> ProcessCommandAsync(
        SwiftAidDbContext db,
        ActorContext actor,
        Guid caseId,
        CaseCommand command,
        ProtectedPayloadOptions options,
        ProtectedPayloadDataProtectionProvider provider)
    {
        await using var context = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var unitOfWork = new PostgresCaseCommandUnitOfWork(context);
        var payloadStore = new PostgresProtectedCaseEventPayloadStore(context, unitOfWork, options, provider);
        var persistence = new PostgresCaseCommandPersistence(
            context, unitOfWork, payloadStore, new CaseEventSerializer());
        var policy = new CaseAccessPolicy(
            new PostgresCurrentAccessGrantStore(context, unitOfWork),
            new PostgresCaseAccessDescriptorStore(context, unitOfWork));
        var processor = new CaseCommandProcessor(unitOfWork, policy, persistence);
        return await processor.ProcessAsync(actor, caseId, 1, command, CancellationToken.None);
    }

    private static async Task<CaseMonitoringSummaryResult> QuerySummaryAsync(
        SwiftAidDbContext db, ActorContext actor, Guid caseId,
        ProtectedPayloadOptions options, ProtectedPayloadDataProtectionProvider provider)
    {
        await using var context = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var unitOfWork = new PostgresCaseCommandUnitOfWork(context);
        var policy = new CaseAccessPolicy(
            new PostgresCurrentAccessGrantStore(context, unitOfWork),
            new PostgresCaseAccessDescriptorStore(context, unitOfWork));
        var payloadStore = new PostgresProtectedCaseEventPayloadStore(context, unitOfWork, options, provider);
        var query = new CaseMonitoringSummaryQuery(
            new PostgresCaseMonitoringSummaryStore(context, payloadStore), policy, unitOfWork);
        return await query.GetAsync(actor, caseId, CancellationToken.None);
    }

    private static async Task<MonitoringQueueResult> QueryQueueAsync(
        ActorContext actor, DateTimeOffset utcNow, ProtectedPayloadOptions options, ProtectedPayloadDataProtectionProvider provider)
    {
        await using var context = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var unitOfWork = new PostgresCaseCommandUnitOfWork(context);
        var policy = new CaseAccessPolicy(
            new PostgresCurrentAccessGrantStore(context, unitOfWork),
            new PostgresCaseAccessDescriptorStore(context, unitOfWork));
        var payloadStore = new PostgresProtectedCaseEventPayloadStore(context, unitOfWork, options, provider);
        var query = new CaseMonitoringQueueQuery(new PostgresCaseMonitoringQueueStore(context, payloadStore), policy, unitOfWork);
        return await query.GetAssignedAsync(actor, null, utcNow, CancellationToken.None);
    }

    private sealed class CaseMonitoringCleanup(Guid caseId, string grantGroupId) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await using var context = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
            var payloadIds = await context.CaseEvents.AsNoTracking()
                .Where(item => item.CaseId == caseId)
                .Select(item => item.PayloadId)
                .ToArrayAsync();
            var eventIds = await context.CaseEvents.AsNoTracking()
                .Where(item => item.CaseId == caseId)
                .Select(item => item.EventId)
                .ToArrayAsync();
            await context.DomainOutbox.Where(item => eventIds.Contains(item.EventId))
                .ExecuteDeleteAsync();
            await context.ManagedCaseObservations.Where(item => item.CaseId == caseId)
                .ExecuteDeleteAsync();
            await context.ManagedCaseFollowUps.Where(item => item.CaseId == caseId)
                .ExecuteDeleteAsync();
            await context.ManagedCaseEscalations.Where(item => item.CaseId == caseId)
                .ExecuteDeleteAsync();
            await context.CaseEvents.Where(item => item.CaseId == caseId)
                .ExecuteDeleteAsync();
            await context.CommandReceipts.Where(item => item.CaseId == caseId)
                .ExecuteDeleteAsync();
            await context.ProtectedEventPayloads.Where(item => payloadIds.Contains(item.PayloadId))
                .ExecuteDeleteAsync();
            await context.SecurityAuditEntries.Where(item => item.ResourceId == caseId)
                .ExecuteDeleteAsync();
            await context.NotificationItems.Where(item => item.ResourceId == caseId)
                .ExecuteDeleteAsync();
            await context.ManagedCaseRegions.Where(item => item.CaseId == caseId)
                .ExecuteDeleteAsync();
            await context.ManagedCases.Where(item => item.CaseId == caseId)
                .ExecuteDeleteAsync();
            await context.AccessGrantOperations.Where(item => item.GrantGroupId == grantGroupId)
                .ExecuteDeleteAsync();
            await context.AccessGrantRegions.Where(item => item.GrantGroupId == grantGroupId)
                .ExecuteDeleteAsync();
            await context.AccessGrantGroups.Where(item => item.GrantGroupId == grantGroupId)
                .ExecuteDeleteAsync();
        }
    }

    private sealed class SyntheticMonitoringKeyRing : IDisposable
    {
        private readonly string _rootPath;
        private readonly string _certificatePath;
        private readonly string _certificatePassword;

        private SyntheticMonitoringKeyRing(string rootPath, string keysPath, string certificatePath, string certificatePassword)
        {
            _rootPath = rootPath;
            KeysPath = keysPath;
            _certificatePath = certificatePath;
            _certificatePassword = certificatePassword;
        }

        public string KeysPath { get; }

        public static SyntheticMonitoringKeyRing Create()
        {
            var root = PrivateKeyRingTestDirectory.CreateRoot("swiftaid-monitoring");
            var keys = PrivateKeyRingTestDirectory.CreateKeyDirectory(root);
            var certificatePath = Path.Combine(root, "protection.pfx");
            try
            {
                var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                using var rsa = RSA.Create(2048);
                var request = new CertificateRequest("CN=SwiftAid synthetic monitoring", rsa,
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
                var bytes = certificate.Export(X509ContentType.Pfx, password);
                try
                {
                    File.WriteAllBytes(certificatePath, bytes);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }

                return new SyntheticMonitoringKeyRing(root, keys, certificatePath, password);
            }
            catch
            {
                PrivateKeyRingTestDirectory.DeleteRoot(root);
                throw;
            }
        }

        public ProtectedPayloadOptions CreateOptions() => new()
        {
            Enabled = true,
            ApplicationName = "SwiftAid.SyntheticMonitoringTests",
            KeysPath = KeysPath,
            CertificatePath = _certificatePath,
            CertificatePassword = _certificatePassword,
            MaximumPayloadBytes = 1024 * 1024
        };

        public void Dispose()
        {
            if (Directory.Exists(_rootPath))
            {
                Directory.Delete(_rootPath, recursive: true);
            }
        }
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "SwiftAid.Integration.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
