using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Ellai;
using SwiftAid.Infrastructure.Authorization;
using SwiftAid.Infrastructure.Classification;
using SwiftAid.Infrastructure.Persistence.Classification;
using SwiftAid.Infrastructure.Persistence.Ellai;
using SwiftAid.Infrastructure.Persistence.Repositories.Cases;
using SwiftAid.Infrastructure.Persistence.Transactions;

namespace SwiftAid.Integration.Tests;

[Collection(IntakePostgresCollection.Name)]
public sealed class EllaiQualityWorkPostgresTests
{
    [IntakePostgresFact]
    public async Task QualityTurnCommitsOneAssessmentAndLinkedAuthorizedTimelineActivity()
    {
        await using var fixture = await CaseImportPostgresTests.Fixture.CreateAsync();
        var scope = await fixture.CreateIsolatedWorkspaceAsync();
        var imported = await fixture.Service.ProcessAsync(CaseImportPostgresTests.Actor, Guid.NewGuid(), scope,
            1, true, new StringReader(CaseImportPostgresTests.Csv(Guid.NewGuid())), default);
        var caseId = Assert.Single(imported.Rows).CaseId!.Value;
        fixture.Db.ChangeTracker.Clear();
        var schedules = new PostgresEllaiOperationalScheduleStore(fixture.Db);
        const string role = "ops.quality";
        Assert.True(await schedules.CreateAsync(scope.WorkspaceId, role, 4, "policy/1", DateTimeOffset.UtcNow.AddMinutes(-1), default));
        var lease = await schedules.TryClaimAsync(scope.WorkspaceId, role, TimeSpan.FromMinutes(1), default);
        Assert.NotNull(lease);
        var settings = new ClassificationOptions { Enabled = true, WorkerTokenSha256 = new string('a', 64), Organizations = ["org-zw-1"] };
        var work = new EllaiQualityWork(fixture.Db, fixture.ClassificationPayloadStore(fixture.Db), Options.Create(settings));
        var unit = new PostgresCaseCommandUnitOfWork(fixture.Db);
        var policy = new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(fixture.Db, unit),
            new PostgresCaseAccessDescriptorStore(fixture.Db, unit));
        var query = new CaseTimelineQuery(new PostgresCaseTimelineStore(fixture.Db), policy, unit);
        var broker = new EllaiToolBroker(new EllaiPolicyService(), new EllaiConsentService(), timeline: query);
        var toolContext = new EllaiToolContext("quality-session", "epoch", "public.product-help", null, 4, true,
            DataClassification: EllaiProcessingClassification.UserProvidedNonSensitive, Actor: CaseImportPostgresTests.Actor,
            AllowedTools: new HashSet<string> { EllaiToolNames.CaseTimeline }, ConsentPurpose: EllaiConsentService.PublicProductHelpPurpose);
        var request = new EllaiToolRequest(EllaiToolNames.CaseTimeline, new Dictionary<string, string> { ["case_id"] = caseId.ToString("D") });
        var before = await broker.InvokeAsync(toolContext, request, default);
        Assert.True(before.Allowed);
        var trace = EllaiToolTrace.SerializeFlattened([EllaiObservedToolResult.From(request, before)], false, []);
        Assert.NotNull(trace);
        var history = new[]
        {
            new EllaiConversationTurn("quality-session", "prior", 1, "history", "prior answer", "Completed", 1, DateTimeOffset.UtcNow) { ToolTraceJson = trace },
            new EllaiConversationTurn("quality-session", "current", 2, "follow-up", null, "Processing", 1, null)
        };
        var beforeReplay = await EllaiAuthorizedConversationContext.BuildAsync(history, "current", CaseImportPostgresTests.Actor,
            null, default, toolContext: toolContext, invokeTool: broker.InvokeAsync);
        Assert.Contains(beforeReplay.Messages, message => message.Content == "prior answer");
        var outOfScope = new ClassificationOptions { Enabled = true, WorkerTokenSha256 = new string('a', 64), Organizations = ["other-org"] };
        var otherWork = new EllaiQualityWork(fixture.Db, fixture.ClassificationPayloadStore(fixture.Db), Options.Create(outOfScope));
        Assert.Equal(EllaiFencedWorkOutcome.NoChange, await otherWork.RunOneAsync(lease, Guid.NewGuid(), Guid.NewGuid(), default));
        Assert.Equal(EllaiFencedWorkOutcome.StaleLease, await work.RunOneAsync(lease with { FencingToken = lease.FencingToken + 1 }, Guid.NewGuid(), Guid.NewGuid(), default));
        Assert.Equal(0, await fixture.Db.Set<ReportQualityEntity>().AsNoTracking().CountAsync(item => item.CaseId == caseId));
        await using var replicaDb = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var replica = new EllaiQualityWork(replicaDb, fixture.ClassificationPayloadStore(replicaDb), Options.Create(settings));
        var outcomes = await Task.WhenAll(work.RunOneAsync(lease, Guid.NewGuid(), Guid.NewGuid(), default),
            replica.RunOneAsync(lease, Guid.NewGuid(), Guid.NewGuid(), default));
        Assert.Single(outcomes, outcome => outcome == EllaiFencedWorkOutcome.Committed);
        Assert.Single(outcomes, outcome => outcome == EllaiFencedWorkOutcome.NoChange);
        Assert.Equal(EllaiFencedWorkOutcome.NoChange, await work.RunOneAsync(lease, Guid.NewGuid(), Guid.NewGuid(), default));
        Assert.Equal(1, await fixture.Db.Set<ReportQualityEntity>().AsNoTracking().CountAsync(item => item.CaseId == caseId));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => fixture.Db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE ellai_case_quality_activity SET policy_version = 'changed' WHERE case_id = {caseId}"));
        var timeline = await query.GetAsync(CaseImportPostgresTests.Actor, caseId, 20, default);
        var activity = Assert.Single(timeline.Timeline!.Entries.SelectMany(entry => entry.AgentActivities));
        Assert.Equal("ops.quality", activity.RoleId);
        var after = await broker.InvokeAsync(toolContext, request, default);
        Assert.True(after.Allowed);
        Assert.Contains("Ellai Data Quality", after.ResultSummary, StringComparison.Ordinal);
        Assert.NotEqual(before.CaseTimelineReplayScope, after.CaseTimelineReplayScope);
        var afterReplay = await EllaiAuthorizedConversationContext.BuildAsync(history, "current", CaseImportPostgresTests.Actor,
            null, default, toolContext: toolContext, invokeTool: broker.InvokeAsync);
        Assert.DoesNotContain(afterReplay.Messages, message => message.Content == "prior answer");
        var foreign = CaseRegistrationBackendTests.CreateOfficer("unregistered-" + Guid.NewGuid().ToString("N"));
        var denied = await broker.InvokeAsync(toolContext with { Actor = foreign }, request, default);
        Assert.False(denied.Allowed);
        Assert.Empty(denied.Evidence);
        Assert.True(await schedules.CompleteAsync(lease, default));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => fixture.Db.GetService<IMigrator>()
            .MigrateAsync("20261104100000_EllaiOperationalSchedules"));
        Assert.Equal(1, await fixture.Db.Set<ReportQualityEntity>().AsNoTracking().CountAsync(item => item.CaseId == caseId));
    }
}
