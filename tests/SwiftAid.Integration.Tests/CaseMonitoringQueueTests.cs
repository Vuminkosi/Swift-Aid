using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Application.Serialization;
using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Commands;
using SwiftAid.Domain.Cases;
using SwiftAid.Gateway.Endpoints;
using SwiftAid.Infrastructure.Authorization;
using SwiftAid.Infrastructure.Configuration;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Entities.Cases;
using SwiftAid.Infrastructure.Persistence.Entities.Intake;
using SwiftAid.Infrastructure.Persistence.Processing;
using SwiftAid.Infrastructure.Persistence.Protection;
using SwiftAid.Infrastructure.Persistence.Repositories.Cases;
using SwiftAid.Infrastructure.Persistence.Transactions;

namespace SwiftAid.Integration.Tests;

/// <summary>
/// Opt-in PostgreSQL tests plus in-memory endpoint tests for the staff monitoring
/// queue (SA-FUP-06, SA-MON-04). Uses the same disposable loopback database gate
/// as <see cref="IntakePostgresFactAttribute"/>.
/// </summary>
[Trait("Category", "IntakePostgres")]
[Collection(IntakePostgresCollection.Name)]
public sealed class CaseMonitoringQueueTests
{
    private static readonly DateTimeOffset PastDueAtUtc = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FutureDueAtUtc = new(2026, 11, 10, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset QueryNowUtc = new(2026, 11, 5, 8, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [IntakePostgresFact]
    public async Task CaseScopedPagesCrossKindsAndRejectStaleContinuation()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var grantGroupId = await SeedQueueScopeAsync(db,
            [CaseOperation.ReadCase, CaseOperation.ManageFollowUp, CaseOperation.ManageEscalation, CaseOperation.ManageAssistance]);
        using var keyRing = SyntheticQueueKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        var caseId = await SeedQueueCaseAsync(db, keyRing.CreateOptions(), provider);
        await using var cleanup = new QueueCleanup(caseId, grantGroupId);
        var actor = CreateOfficer("officer-1");
        for (var version = 2; version <= 4; version++)
            Assert.Equal(CaseCommandProcessingStatus.Accepted, (await ProcessCommandAsync(db, actor, caseId,
                new ScheduleFollowUpCommand(Guid.NewGuid(), version, Guid.NewGuid(), $"Visit {version}", "officer-1",
                    FutureDueAtUtc, "routine check", null, DateTimeOffset.UtcNow), keyRing.CreateOptions(), provider)).Status);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, (await ProcessCommandAsync(db, actor, caseId,
            new RaiseEscalationCommand(Guid.NewGuid(), 5, Guid.NewGuid(), "Supervisor needed", [], "duty-officer", null,
                DateTimeOffset.UtcNow), keyRing.CreateOptions(), provider)).Status);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, (await ProcessCommandAsync(db, actor, caseId,
            new RequestAssistanceCommand(Guid.NewGuid(), 6, Guid.NewGuid(), "Transport", "officer-1", FutureDueAtUtc,
                null, DateTimeOffset.UtcNow), keyRing.CreateOptions(), provider)).Status);

        var unit = new PostgresCaseCommandUnitOfWork(db);
        var payload = new PostgresProtectedCaseEventPayloadStore(db, unit, keyRing.CreateOptions(), provider);
        var queue = new PostgresCaseMonitoringQueueStore(db, payload);
        var policy = new CaseAccessPolicy(new PostgresCurrentAccessGrantStore(db, unit), new PostgresCaseAccessDescriptorStore(db, unit));
        var query = new CaseMonitoringPageQuery(new PostgresCaseMonitoringPageStore(db, queue),
            new PostgresCaseTimelineStore(db), policy, unit, TimeProvider.System);
        var first = await query.GetAsync(actor, caseId, 0, 2, 7, default);
        var second = await query.GetAsync(actor, caseId, 2, 2, 7, default);
        var third = await query.GetAsync(actor, caseId, 4, 2, 7, default);
        Assert.Equal(CaseMonitoringPageAccess.Found, first.Access);
        Assert.Equal(2, first.Page!.NextOffset);
        Assert.Equal(4, second.Page!.NextOffset);
        Assert.Null(third.Page!.NextOffset);
        var rows = first.Page.Items.Concat(second.Page.Items).Concat(third.Page.Items).ToArray();
        Assert.Equal(5, rows.Length);
        Assert.Equal(5, rows.Select(row => row.Work.WorkItemId).Distinct().Count());
        Assert.Equal(3, rows.Count(row => row.Work.Kind == MonitoringQueueKind.FollowUp));
        Assert.Single(rows, row => row.Work.Kind == MonitoringQueueKind.Escalation);
        Assert.Single(rows, row => row.Work.Kind == MonitoringQueueKind.Assistance);
        Assert.Equal(CaseMonitoringPageAccess.Stale, (await query.GetAsync(actor, caseId, 2, 2, 6, default)).Access);
        var discovery = new PostgresCaseWorkDiscoveryStore(db);
        Assert.Contains(caseId, await discovery.GetCasesAfterAsync(CaseWorkDiscoveryKind.FollowUp, "officer-1", null, 200, default));
        Assert.DoesNotContain(caseId, await discovery.GetCasesAfterAsync(CaseWorkDiscoveryKind.FollowUp, "other-actor", null, 200, default));
        Assert.DoesNotContain(caseId, await discovery.GetCasesAfterAsync(CaseWorkDiscoveryKind.FollowUp, "officer-1", caseId, 200, default));
        Assert.Contains(caseId, await discovery.GetCasesAfterAsync(CaseWorkDiscoveryKind.Escalation, null, null, 200, default));
        Assert.Contains(caseId, await discovery.GetCasesAfterAsync(CaseWorkDiscoveryKind.Assistance, null, null, 200, default));
        var secondCaseId = await SeedQueueCaseAsync(db, keyRing.CreateOptions(), provider);
        await using var secondCleanup = new QueueCleanup(secondCaseId, grantGroupId);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, (await ProcessCommandAsync(db, actor, secondCaseId,
            new ScheduleFollowUpCommand(Guid.NewGuid(), 2, Guid.NewGuid(), "Second case visit", "officer-1", FutureDueAtUtc,
                "routine check", null, DateTimeOffset.UtcNow), keyRing.CreateOptions(), provider)).Status);
        var firstDiscovery = await discovery.GetCasesAfterAsync(CaseWorkDiscoveryKind.FollowUp, "officer-1", null, 1, default);
        var secondDiscovery = await discovery.GetCasesAfterAsync(CaseWorkDiscoveryKind.FollowUp, "officer-1", Assert.Single(firstDiscovery), 1, default);
        Assert.Equal(new[] { caseId, secondCaseId }.Order(), firstDiscovery.Concat(secondDiscovery).Order());
    }

    [IntakePostgresFact]
    public async Task AssignedFollowUps_ListedDueFirst_WithOverdueFlag()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var grantGroupId = await SeedQueueScopeAsync(db,
            [CaseOperation.ReadCase, CaseOperation.ManageFollowUp]);
        using var keyRing = SyntheticQueueKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        var caseId = await SeedQueueCaseAsync(db, keyRing.CreateOptions(), provider);
        await using var cleanup = new QueueCleanup(caseId, grantGroupId);
        var actor = CreateOfficer("officer-1");

        await ProcessCommandAsync(db, actor, caseId,
            new ScheduleFollowUpCommand(Guid.NewGuid(), 2, Guid.NewGuid(), "later visit", "officer-1",
                FutureDueAtUtc, "routine check", null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);
        await ProcessCommandAsync(db, actor, caseId,
            new ScheduleFollowUpCommand(Guid.NewGuid(), 3, Guid.NewGuid(), "overdue visit", "officer-1",
                PastDueAtUtc, "missed window", null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);

        var result = await CreateQuery(db, keyRing.CreateOptions(), provider).GetAssignedAsync(actor, null, QueryNowUtc, CancellationToken.None);

        Assert.Equal(CaseQueueAccess.Allowed, result.Access);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal("overdue visit", result.Items[0].Summary);
        Assert.True(result.Items[0].IsOverdue);
        Assert.Equal(MonitoringQueueKind.FollowUp, result.Items[0].Kind);
        Assert.Equal("later visit", result.Items[1].Summary);
        Assert.False(result.Items[1].IsOverdue);
    }

    [IntakePostgresFact]
    public async Task OpenScope_ListsEachOpenEscalationAndResolvesLegacyActionSummary()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var grantGroupId = await SeedQueueScopeAsync(db,
            [CaseOperation.ReadCase, CaseOperation.ManageEscalation]);
        using var keyRing = SyntheticQueueKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        var caseId = await SeedQueueCaseAsync(db, keyRing.CreateOptions(), provider);
        await using var cleanup = new QueueCleanup(caseId, grantGroupId);
        var actor = CreateOfficer("officer-1");

        var raisedId = Guid.NewGuid();
        await ProcessCommandAsync(db, actor, caseId,
            new RaiseEscalationCommand(Guid.NewGuid(), 2, raisedId, "holding unreachable twice",
                ["event-ref"], "duty-officer", null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);
        var ackedId = Guid.NewGuid();
        await ProcessCommandAsync(db, actor, caseId,
            new RaiseEscalationCommand(Guid.NewGuid(), 3, ackedId, "supervisor aware",
                [], "duty-officer", null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);
        await ProcessCommandAsync(db, actor, caseId,
            new AcknowledgeEscalationCommand(Guid.NewGuid(), 4, ackedId, null, "acknowledged", null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);

        var legacyId = Guid.NewGuid();
        await ProcessCommandAsync(db, actor, caseId,
            new RaiseEscalationCommand(Guid.NewGuid(), 5, legacyId, "supervisor paged",
                [], "duty-officer", null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);
        await ProcessCommandAsync(db, actor, caseId,
            new AcknowledgeEscalationCommand(Guid.NewGuid(), 6, legacyId, "supervisor paged",
                "action completed", null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);
        await db.ManagedCaseEscalations.Where(item => item.EscalationId == legacyId)
            .ExecuteUpdateAsync(update => update
                .SetProperty(item => item.Status, EscalationStatus.Acknowledged)
                .SetProperty(item => item.ActionRecordedVersion, (long?)null)
                .SetProperty(item => item.ActionSummary, (string?)null));

        var result = await CreateQuery(db, keyRing.CreateOptions(), provider)
            .GetOpenAsync(actor, null, QueryNowUtc, CancellationToken.None);

        Assert.Equal(CaseQueueAccess.Allowed, result.Access);
        var statuses = result.Items.ToDictionary(item => item.WorkItemId, item => item.Status);
        Assert.Equal(3, statuses.Count);
        Assert.Equal("Acknowledged", statuses[ackedId]);
        Assert.Equal("ActionRecorded", statuses[legacyId]);
        Assert.Equal("Raised", statuses[raisedId]);
        Assert.All(result.Items, item => Assert.False(item.IsOverdue));

        var summary = await CreateSummaryQuery(db, keyRing.CreateOptions(), provider)
            .GetAsync(actor, caseId, CancellationToken.None);
        Assert.Equal(CaseMonitoringSummaryAccess.Found, summary.Access);
        Assert.Contains(summary.Summary!.Escalations,
            item => item.EscalationId == legacyId && item.Status == "ActionRecorded");
    }

    [IntakePostgresFact]
    public async Task AssistanceLifecycle_PersistsProjection_ListsAssignedOpenWork_AndClosesOnReceipt()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var grantGroupId = await SeedQueueScopeAsync(db,
            [CaseOperation.ReadCase, CaseOperation.ManageAssistance]);
        using var keyRing = SyntheticQueueKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        var caseId = await SeedQueueCaseAsync(db, keyRing.CreateOptions(), provider);
        await using var cleanup = new QueueCleanup(caseId, grantGroupId);
        var actor = CreateOfficer("officer-1");
        var assistanceId = Guid.NewGuid();

        var requested = await ProcessCommandAsync(db, actor, caseId,
            new RequestAssistanceCommand(Guid.NewGuid(), 2, assistanceId, "Transport", "officer-1",
                FutureDueAtUtc, null, DateTimeOffset.UtcNow), keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, requested.Status);
        var accepted = await ProcessCommandAsync(db, actor, caseId,
            new UpdateAssistanceCommand(Guid.NewGuid(), 3, assistanceId, AssistanceStatus.Accepted,
                "Request accepted", null, null, null, DateTimeOffset.UtcNow), keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, accepted.Status);
        var progressing = await ProcessCommandAsync(db, actor, caseId,
            new UpdateAssistanceCommand(Guid.NewGuid(), 4, assistanceId, AssistanceStatus.InProgress,
                "Coordination underway", null, null, null, DateTimeOffset.UtcNow), keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, progressing.Status);
        var provided = await ProcessCommandAsync(db, actor, caseId,
            new UpdateAssistanceCommand(Guid.NewGuid(), 5, assistanceId, AssistanceStatus.Provided,
                "Transport arranged", null, null, null, DateTimeOffset.UtcNow), keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, provided.Status);

        var projection = await db.ManagedCaseAssistance.AsNoTracking().SingleAsync(item => item.AssistanceId == assistanceId);
        Assert.Equal(AssistanceStatus.Provided, projection.Status);
        Assert.Equal("officer-1", projection.ResponsibleActorId);
        Assert.Equal("officer-1", projection.ProvidedByActorId);
        Assert.Null(projection.ReceiptRecordedByActorId);
        Assert.Null(projection.ReceiptMethod);
        Assert.Null(projection.ReceiptContext);

        var open = await CreateQuery(db, keyRing.CreateOptions(), provider).GetAssignedAsync(actor, null, QueryNowUtc, CancellationToken.None);
        var item = Assert.Single(open.Items);
        Assert.Equal(MonitoringQueueKind.Assistance, item.Kind);
        Assert.Equal(assistanceId, item.WorkItemId);
        Assert.Equal("Provided", item.Status);
        Assert.False(item.IsOverdue);

        var receipt = await ProcessCommandAsync(db, actor, caseId,
            new UpdateAssistanceCommand(Guid.NewGuid(), 6, assistanceId, AssistanceStatus.ReceiptConfirmed,
                "Recipient reports receiving transport", "In person", "At service point", null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, receipt.Status);
        db.ChangeTracker.Clear();
        var confirmed = await db.ManagedCaseAssistance.AsNoTracking().SingleAsync(item => item.AssistanceId == assistanceId);
        Assert.Equal(AssistanceStatus.ReceiptConfirmed, confirmed.Status);
        Assert.Equal("officer-1", confirmed.ReceiptRecordedByActorId);
        Assert.Equal("In person", confirmed.ReceiptMethod);
        Assert.Equal("At service point", confirmed.ReceiptContext);
        var closed = await CreateQuery(db, keyRing.CreateOptions(), provider).GetAssignedAsync(actor, null, QueryNowUtc, CancellationToken.None);
        Assert.Empty(closed.Items);
    }

    [IntakePostgresFact]
    public async Task UnfulfilledAssistance_RemainsVisibleInAssignedAndOpenQueuesWithReason()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var grantGroupId = await SeedQueueScopeAsync(db,
            [CaseOperation.ReadCase, CaseOperation.ManageAssistance]);
        using var keyRing = SyntheticQueueKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        var caseId = await SeedQueueCaseAsync(db, keyRing.CreateOptions(), provider);
        await using var cleanup = new QueueCleanup(caseId, grantGroupId);
        var actor = CreateOfficer("officer-1");
        var assistanceId = Guid.NewGuid();

        Assert.Equal(CaseCommandProcessingStatus.Accepted, (await ProcessCommandAsync(db, actor, caseId,
            new RequestAssistanceCommand(Guid.NewGuid(), 2, assistanceId, "Transport support", "officer-1",
                null, null, DateTimeOffset.UtcNow), keyRing.CreateOptions(), provider)).Status);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, (await ProcessCommandAsync(db, actor, caseId,
            new UpdateAssistanceCommand(Guid.NewGuid(), 3, assistanceId, AssistanceStatus.Accepted,
                "Request accepted", null, null, null, DateTimeOffset.UtcNow), keyRing.CreateOptions(), provider)).Status);
        Assert.Equal(CaseCommandProcessingStatus.Accepted, (await ProcessCommandAsync(db, actor, caseId,
            new UpdateAssistanceCommand(Guid.NewGuid(), 4, assistanceId, AssistanceStatus.Unfulfilled,
                "No eligible transport provider was available", null, null, null, DateTimeOffset.UtcNow),
            keyRing.CreateOptions(), provider)).Status);

        var projection = await db.ManagedCaseAssistance.AsNoTracking().SingleAsync(item => item.AssistanceId == assistanceId);
        Assert.Equal(AssistanceStatus.Unfulfilled, projection.Status);
        Assert.Equal("No eligible transport provider was available", projection.LastNote);

        var assigned = await CreateQuery(db, keyRing.CreateOptions(), provider).GetAssignedAsync(actor, null, QueryNowUtc, CancellationToken.None);
        var assignedItem = Assert.Single(assigned.Items);
        Assert.Equal("Unfulfilled", assignedItem.Status);
        Assert.Contains("Transport support", assignedItem.Summary, StringComparison.Ordinal);
        Assert.Contains("No eligible transport provider was available", assignedItem.Summary, StringComparison.Ordinal);

        var open = await CreateQuery(db, keyRing.CreateOptions(), provider).GetOpenAsync(actor, null, QueryNowUtc, CancellationToken.None);
        var openItem = Assert.Single(open.Items);
        Assert.Equal(assistanceId, openItem.WorkItemId);
        Assert.Equal("Unfulfilled", openItem.Status);
        Assert.Contains("No eligible transport provider was available", openItem.Summary, StringComparison.Ordinal);
    }

    [IntakePostgresFact]
    public async Task UnreadableCase_SkippedNeverListed()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var grantGroupId = await SeedQueueScopeAsync(db,
            [CaseOperation.ReadCase, CaseOperation.ManageFollowUp]);
        using var keyRing = SyntheticQueueKeyRing.Create();
        using var provider = ProtectedPayloadDataProtectionProviderFactory.Create(
            keyRing.CreateOptions(), new TestHostEnvironment(Environments.Production));
        // Owned outside the grant's organization: ReadCase denies, so the queued
        // row must be skipped instead of listed.
        var unusedScopeSeedCaseId = await IntakeDecisionStoreTests.SeedManagedCaseAsync(db, "org-sz-1");
        var foreignCaseId = await SeedQueueCaseAsync(db, keyRing.CreateOptions(), provider);
        var currentScheduleEventId = await db.CaseEvents.AsNoTracking()
            .Where(item => item.CaseId == foreignCaseId && item.AggregateVersion == 2)
            .Select(item => item.EventId)
            .SingleAsync();
        await db.ManagedCases.Where(item => item.CaseId == foreignCaseId)
            .ExecuteUpdateAsync(update => update
                .SetProperty(item => item.OrganizationId, "org-sz-1")
                .SetProperty(item => item.WorkspaceId, "workspace-sz-1")
                .SetProperty(item => item.CountryCode, "SZ"));
        await db.ManagedCases.Where(item => item.CaseId == unusedScopeSeedCaseId).ExecuteDeleteAsync();
        db.ManagedCaseFollowUps.Add(new ManagedCaseFollowUpEntity
        {
            FollowUpId = Guid.NewGuid(),
            CaseId = foreignCaseId,
            Purpose = "foreign visit",
            AssigneeActorId = "officer-1",
            DueAtUtc = FutureDueAtUtc,
            Status = FollowUpStatus.Scheduled,
            ScheduledVersion = 2,
            CurrentScheduleVersion = 2,
            CurrentScheduleEventId = currentScheduleEventId
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await using var cleanup = new QueueCleanup(foreignCaseId, grantGroupId);

        var actor = CreateOfficer("officer-1");
        var assigned = await CreateQuery(db, keyRing.CreateOptions(), provider).GetAssignedAsync(actor, null, QueryNowUtc, CancellationToken.None);
        var open = await CreateQuery(db, keyRing.CreateOptions(), provider).GetOpenAsync(actor, null, QueryNowUtc, CancellationToken.None);

        Assert.Equal(CaseQueueAccess.Allowed, assigned.Access);
        Assert.DoesNotContain(assigned.Items, item => item.CaseId == foreignCaseId);
        Assert.Equal(CaseQueueAccess.Allowed, open.Access);
        Assert.DoesNotContain(open.Items, item => item.CaseId == foreignCaseId);
    }

    [Fact]
    public void MappedQueueRoute_RequiresStaffAuthentication()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<ICaseMonitoringQueueQuery>(new TestMonitoringQuery());
        builder.Services.AddAuthorization();
        using var app = builder.Build();
        app.MapMonitoringQueueEndpoints();

        var route = Assert.Single(
            ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>(),
            candidate => candidate.RoutePattern.RawText == "/api/v1/cases/monitoring/queue");

        var authorization = Assert.Single(route.Metadata.GetOrderedMetadata<IAuthorizeData>());
        Assert.Equal(
            string.Join(",", IdentityConstants.ApplicationScheme, OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme),
            authorization.AuthenticationSchemes);
    }

    [Fact]
    public async Task AnonymousQueueRequest_IsUnauthorized()
    {
        await using var host = await TestMonitoringHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/cases/monitoring/queue?scope=assigned");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, host.Service.Calls);
    }

    [Theory]
    [InlineData("/api/v1/cases/monitoring/queue")]
    [InlineData("/api/v1/cases/monitoring/queue?scope=bogus")]
    public async Task InvalidScope_IsRejected(string address)
    {
        await using var host = await TestMonitoringHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        TestAuthenticationHandler.AddCookieAuthentication(request);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Service.Calls);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-4")]
    [InlineData("not-a-number")]
    public async Task InvalidLimit_IsRejected(string limit)
    {
        await using var host = await TestMonitoringHost.StartAsync();
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/v1/cases/monitoring/queue?scope=open&limit={limit}");
        TestAuthenticationHandler.AddCookieAuthentication(request);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Service.Calls);
    }

    [Fact]
    public async Task AssignedScope_ReturnsOkEmptyList()
    {
        await using var host = await TestMonitoringHost.StartAsync(items: []);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/cases/monitoring/queue?scope=assigned");
        TestAuthenticationHandler.AddCookieAuthentication(request);

        using var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = JsonSerializer.Deserialize<MonitoringQueueResult>(body, JsonOptions);
        Assert.NotNull(result);
        Assert.Equal(CaseQueueAccess.Allowed, result!.Access);
        Assert.Empty(result.Items);
        Assert.Equal(MonitoringQueueScope.Assigned, host.Service.LastScope);
    }

    [Fact]
    public async Task OpenScope_ReturnsOkEmptyList()
    {
        await using var host = await TestMonitoringHost.StartAsync(items: []);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/cases/monitoring/queue?scope=open");
        TestAuthenticationHandler.AddCookieAuthentication(request);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(MonitoringQueueScope.Open, host.Service.LastScope);
    }

    [Fact]
    public void MapResult_CoversEveryAccess()
    {
        var allowed = MonitoringQueueEndpoints.MapResult(
            new MonitoringQueueResult(CaseQueueAccess.Allowed, []));
        Assert.Equal(200, ((Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<MonitoringQueueResult>)allowed).StatusCode);

        var denied = MonitoringQueueEndpoints.MapResult(
            new MonitoringQueueResult(CaseQueueAccess.Denied, []));
        Assert.Equal(200, ((Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<MonitoringQueueResult>)denied).StatusCode);

        var unavailable = MonitoringQueueEndpoints.MapResult(
            new MonitoringQueueResult(CaseQueueAccess.Unavailable, []));
        Assert.Equal(503, ((Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult)unavailable).StatusCode);
    }

    [Fact]
    public async Task DisabledPipeline_GetsNotFound()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
                options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
            })
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                IdentityConstants.ApplicationScheme, _ => { })
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, _ => { });
        builder.Services.AddAuthorization();
        // No ICaseMonitoringQueueQuery: the case command pipeline is disabled here.
        using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapMonitoringQueueEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/cases/monitoring/queue?scope=open");
        TestAuthenticationHandler.AddCookieAuthentication(request);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await app.StopAsync();
    }

    private const string QueueRegionId = "region-zw-harare";

    private static ActorContext CreateOfficer(string actorId) => ActorContext.FromValidatedPrincipal(
        new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, actorId),
            new Claim(ActorContext.AccessEpochClaimType, "7")
        ], "synthetic-test")));

    private static CaseMonitoringQueueQuery CreateQuery(
        SwiftAidDbContext db, ProtectedPayloadOptions options, ProtectedPayloadDataProtectionProvider provider)
    {
        var unitOfWork = new PostgresCaseCommandUnitOfWork(db);
        var payloadStore = new PostgresProtectedCaseEventPayloadStore(db, unitOfWork, options, provider);
        return new CaseMonitoringQueueQuery(
            new PostgresCaseMonitoringQueueStore(db, payloadStore),
            new CaseAccessPolicy(
                new PostgresCurrentAccessGrantStore(db, unitOfWork),
                new PostgresCaseAccessDescriptorStore(db, unitOfWork)),
            unitOfWork);
    }

    private static CaseMonitoringSummaryQuery CreateSummaryQuery(
        SwiftAidDbContext db, ProtectedPayloadOptions options, ProtectedPayloadDataProtectionProvider provider)
    {
        var unitOfWork = new PostgresCaseCommandUnitOfWork(db);
        var payloadStore = new PostgresProtectedCaseEventPayloadStore(db, unitOfWork, options, provider);
        return new CaseMonitoringSummaryQuery(
            new PostgresCaseMonitoringSummaryStore(db, payloadStore),
            new CaseAccessPolicy(
                new PostgresCurrentAccessGrantStore(db, unitOfWork),
                new PostgresCaseAccessDescriptorStore(db, unitOfWork)),
            unitOfWork);
    }

    private static async Task<string> SeedQueueScopeAsync(
        SwiftAidDbContext db, IReadOnlyList<CaseOperation> operations)
    {
        await CaseRegistrationBackendTests.SeedRegistrationIdentitiesAsync(db);
        var membershipId = await IntakeDecisionStoreTests.ResetOfficerScopeAsync(db, "officer-1", 7);

        var grantGroupId = $"grant-group-queue-{Guid.NewGuid():N}";
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

        db.AccessGrantRegions.Add(new AccessGrantRegionEntity
        {
            GrantGroupId = grantGroupId,
            RegionId = QueueRegionId
        });

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return grantGroupId;
    }

    private static async Task<Guid> SeedQueueCaseAsync(
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
            RegionId = QueueRegionId
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

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
        return caseId;
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

    private sealed class QueueCleanup(Guid caseId, string grantGroupId) : IAsyncDisposable
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
            await context.ManagedCaseAssistance.Where(item => item.CaseId == caseId)
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

    private sealed class SyntheticQueueKeyRing : IDisposable
    {
        private readonly string _rootPath;
        private readonly string _certificatePath;
        private readonly string _certificatePassword;

        private SyntheticQueueKeyRing(string rootPath, string keysPath, string certificatePath, string certificatePassword)
        {
            _rootPath = rootPath;
            KeysPath = keysPath;
            _certificatePath = certificatePath;
            _certificatePassword = certificatePassword;
        }

        public string KeysPath { get; }

        public static SyntheticQueueKeyRing Create()
        {
            var root = PrivateKeyRingTestDirectory.CreateRoot("swiftaid-queue");
            var keys = PrivateKeyRingTestDirectory.CreateKeyDirectory(root);
            var certificatePath = Path.Combine(root, "protection.pfx");
            try
            {
                var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                using var rsa = RSA.Create(2048);
                var request = new CertificateRequest("CN=SwiftAid synthetic queue", rsa,
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

                return new SyntheticQueueKeyRing(root, keys, certificatePath, password);
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
            ApplicationName = "SwiftAid.SyntheticQueueTests",
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

        public string? WebRootPath { get; set; }

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class TestMonitoringQuery(
        MonitoringQueueResult? result = null) : ICaseMonitoringQueueQuery
    {
        public int Calls { get; private set; }

        public MonitoringQueueScope LastScope { get; private set; }

        public int? LastLimit { get; private set; }

        private readonly MonitoringQueueResult _result = result ?? new MonitoringQueueResult(CaseQueueAccess.Allowed, []);

        public Task<MonitoringQueueResult> GetAssignedAsync(
            ActorContext actor, int? limit, DateTimeOffset utcNow, CancellationToken ct)
        {
            Calls++;
            LastScope = MonitoringQueueScope.Assigned;
            LastLimit = limit;
            return Task.FromResult(_result);
        }

        public Task<MonitoringQueueResult> GetOpenAsync(
            ActorContext actor, int? limit, DateTimeOffset utcNow, CancellationToken ct)
        {
            Calls++;
            LastScope = MonitoringQueueScope.Open;
            LastLimit = limit;
            return Task.FromResult(_result);
        }
    }

    private sealed class TestMonitoringHost : IAsyncDisposable
    {
        private readonly WebApplication _application;

        public HttpClient Client { get; }

        public TestMonitoringQuery Service { get; }

        private TestMonitoringHost(WebApplication application, HttpClient client, TestMonitoringQuery service)
        {
            _application = application;
            Client = client;
            Service = service;
        }

        public static async Task<TestMonitoringHost> StartAsync(
            MonitoringQueueResult? result = null,
            IReadOnlyList<MonitoringQueueItem>? items = null,
            CaseQueueAccess access = CaseQueueAccess.Allowed)
        {
            var service = new TestMonitoringQuery(result ?? new MonitoringQueueResult(access, items ?? []));
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
                    options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                    IdentityConstants.ApplicationScheme, _ => { })
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                    OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, _ => { });
            builder.Services.AddAuthorization();
            builder.Services.AddSingleton<ICaseMonitoringQueueQuery>(service);
            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapMonitoringQueueEndpoints();
            await app.StartAsync();
            return new TestMonitoringHost(app, app.GetTestClient(), service);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _application.StopAsync();
            await _application.DisposeAsync();
        }
    }
}
