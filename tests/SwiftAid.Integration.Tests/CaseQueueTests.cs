using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Validation.AspNetCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Contracts.Cases;
using SwiftAid.Domain.Cases;
using SwiftAid.Gateway.Endpoints;
using SwiftAid.Infrastructure.Authorization;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Entities.Intake;
using SwiftAid.Infrastructure.Persistence.Repositories.Cases;
using SwiftAid.Infrastructure.Persistence.Transactions;

namespace SwiftAid.Integration.Tests;

/// <summary>
/// Opt-in PostgreSQL tests plus in-memory endpoint tests for the officer "my cases" queue.
/// Uses the same disposable loopback database gate as <see cref="IntakePostgresFactAttribute"/>.
/// </summary>
[Trait("Category", "CaseQueue")]
[Collection(IntakePostgresCollection.Name)]
public sealed class CaseQueueTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [IntakePostgresFact]
    public async Task MapCandidateCountryAndWorkspaceFiltersApplyBeforeLimit()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var target = await IntakeDecisionStoreTests.SeedManagedCaseAsync(db, "org-sz-1");
        var newerOtherCountry = await IntakeDecisionStoreTests.SeedManagedCaseAsync(db);
        var now = DateTimeOffset.UtcNow;
        await SeedEventAsync(db, target, 1, "CaseReported", now.AddDays(2));
        await SeedEventAsync(db, newerOtherCountry, 1, "CaseReported", now.AddDays(3));

        var rows = await new PostgresCaseQueueStore(db).GetAsync("officer-1", "workspace-sz-1", "SZ",
            now.AddMinutes(-1), 1, CancellationToken.None);

        Assert.Equal(target, Assert.Single(rows).CaseId);
        Assert.Empty(await new PostgresCaseQueueStore(db).GetAsync("officer-1", "workspace-1", "SZ",
            now.AddMinutes(-1), 1, CancellationToken.None));
    }

    [IntakePostgresFact]
    public async Task FilteredOwnedCasesApplyCountryAndWorkflowBeforeLimit()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var target = await IntakeDecisionStoreTests.SeedManagedCaseAsync(db, "org-sz-1");
        var otherCountry = await IntakeDecisionStoreTests.SeedManagedCaseAsync(db);
        var otherWorkflow = await IntakeDecisionStoreTests.SeedManagedCaseAsync(db, "org-sz-1");
        await db.ManagedCases.Where(x => x.CaseId == otherWorkflow)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Workflow, CaseWorkflow.Active));
        var now = DateTimeOffset.UtcNow;
        await SeedEventAsync(db, target, 1, "CaseReported", now);
        await SeedEventAsync(db, otherCountry, 1, "CaseReported", now.AddDays(1));
        await SeedEventAsync(db, otherWorkflow, 1, "CaseReported", now.AddDays(2));

        var rows = await new PostgresCaseQueueStore(db).GetOwnedCasesAsync("officer-1", 1,
            new CaseQueueFilter("SZ", "Reported", true), CancellationToken.None);
        Assert.Equal(target, Assert.Single(rows).CaseId);
        Assert.Empty(await new PostgresCaseQueueStore(db).GetOwnedCasesAsync("another-owner", 1,
            new CaseQueueFilter("SZ", "Reported", true), CancellationToken.None));
    }

    [IntakePostgresFact]
    public async Task OwnedCases_ListedNewestFirst()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        await CaseRegistrationBackendTests.SeedRegistrationGrantAsync(db);
        var olderCaseId = await IntakeDecisionStoreTests.SeedManagedCaseAsync(db);
        var newerCaseId = await IntakeDecisionStoreTests.SeedManagedCaseAsync(db);
        await SeedRegionScopeAsync(db, olderCaseId);
        await SeedRegionScopeAsync(db, newerCaseId);
        var recordedAt = DateTimeOffset.UtcNow;
        await SeedEventAsync(db, olderCaseId, 1, "CaseReported", recordedAt);
        await SeedEventAsync(db, newerCaseId, 1, "CaseReported", recordedAt.AddMinutes(5));

        var result = await CreateQuery(db).GetMineAsync(
            CreateOfficer("officer-1"), null, CancellationToken.None);

        Assert.Equal(CaseQueueAccess.Allowed, result.Access);
        // The shared disposable database accumulates officer-owned cases across
        // runs, so assert relative order and containment instead of an exact count.
        Assert.True(result.Items.Count >= 2);
        var newerIndex = result.Items.ToList().FindIndex(item => item.CaseId == newerCaseId);
        var olderIndex = result.Items.ToList().FindIndex(item => item.CaseId == olderCaseId);
        Assert.True(newerIndex >= 0 && olderIndex >= 0);
        Assert.True(newerIndex < olderIndex);
        var first = result.Items[0];
        Assert.Equal("Reported", first.Workflow);
        Assert.Equal("ZW", first.CountryCode);
        Assert.Equal("officer-1", first.AccountableOwnerActorId);
        Assert.Equal(2, first.AggregateVersion);
        Assert.NotEqual(default, first.UpdatedAtUtc);
    }

    [IntakePostgresFact]
    public async Task ForeignOwnedCase_Excluded()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        await CaseRegistrationBackendTests.SeedRegistrationGrantAsync(db);
        var ownCaseId = await IntakeDecisionStoreTests.SeedManagedCaseAsync(db);
        var foreignCaseId = await IntakeDecisionStoreTests.SeedManagedCaseAsync(db);
        await SeedRegionScopeAsync(db, ownCaseId);
        await SeedRegionScopeAsync(db, foreignCaseId);
        await ReassignOwnerAsync(db, foreignCaseId, "officer-2");
        var recordedAt = DateTimeOffset.UtcNow;
        await SeedEventAsync(db, ownCaseId, 1, "CaseReported", recordedAt);
        await SeedEventAsync(db, foreignCaseId, 1, "CaseReported", recordedAt.AddMinutes(5));

        var result = await CreateQuery(db).GetMineAsync(
            CreateOfficer("officer-1"), null, CancellationToken.None);

        Assert.Equal(CaseQueueAccess.Allowed, result.Access);
        Assert.Contains(result.Items, item => item.CaseId == ownCaseId);
        Assert.DoesNotContain(result.Items, item => item.CaseId == foreignCaseId);
    }

    [IntakePostgresFact]
    public async Task DeniedCase_SkippedNeverListed()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        await CaseRegistrationBackendTests.SeedRegistrationGrantAsync(db);
        // Owned by the officer but outside the grant's organization: ReadCase denies.
        var foreignCaseId = await IntakeDecisionStoreTests.SeedManagedCaseAsync(db, "org-sz-1");

        var result = await CreateQuery(db).GetMineAsync(
            CreateOfficer("officer-1"), null, CancellationToken.None);

        Assert.Equal(CaseQueueAccess.Allowed, result.Access);
        Assert.DoesNotContain(result.Items, item => item.CaseId == foreignCaseId);
    }

    [IntakePostgresFact]
    public async Task UnknownActor_EmptyQueue()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        await CaseRegistrationBackendTests.SeedRegistrationGrantAsync(db);
        var caseId = await IntakeDecisionStoreTests.SeedManagedCaseAsync(db);
        await SeedRegionScopeAsync(db, caseId);

        var result = await CreateQuery(db).GetMineAsync(
            CreateOfficer("officer-unknown"), null, CancellationToken.None);

        Assert.Equal(CaseQueueAccess.Allowed, result.Access);
        Assert.Empty(result.Items);
    }

    [Fact]
    public void MappedQueueRoute_RequiresStaffAuthentication()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<ICaseQueueQuery>(new TestQueueQuery(TestQueueQuery.SomeCaseId));
        builder.Services.AddAuthorization();
        using var app = builder.Build();
        app.MapCaseQueueEndpoints();

        var route = Assert.Single(
            ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>(),
            candidate => candidate.RoutePattern.RawText == "/api/v1/cases/mine");

        var authorization = Assert.Single(route.Metadata.GetOrderedMetadata<IAuthorizeData>());
        Assert.Equal(
            string.Join(",", IdentityConstants.ApplicationScheme, OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme),
            authorization.AuthenticationSchemes);
    }

    [Fact]
    public async Task AnonymousQueueRequest_IsUnauthorized()
    {
        await using var host = await TestQueueHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/cases/mine");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, host.Service.Calls);
    }

    [Fact]
    public async Task EmptyQueue_ReturnsOkEmptyList()
    {
        await using var host = await TestQueueHost.StartAsync(items: []);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/cases/mine");
        TestAuthenticationHandler.AddCookieAuthentication(request);

        using var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = JsonSerializer.Deserialize<CaseQueueResult>(body, JsonOptions);
        Assert.NotNull(result);
        Assert.Equal(CaseQueueAccess.Allowed, result!.Access);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task DeniedQueue_ReturnsOkEmptyWithDeniedFlag()
    {
        await using var host = await TestQueueHost.StartAsync(access: CaseQueueAccess.Denied);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/cases/mine");
        TestAuthenticationHandler.AddCookieAuthentication(request);

        using var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = JsonSerializer.Deserialize<CaseQueueResult>(body, JsonOptions);
        Assert.NotNull(result);
        Assert.Equal(CaseQueueAccess.Denied, result!.Access);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task UnavailableQueue_ReturnsServiceUnavailable()
    {
        await using var host = await TestQueueHost.StartAsync(access: CaseQueueAccess.Unavailable);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/cases/mine");
        TestAuthenticationHandler.AddCookieAuthentication(request);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-4")]
    [InlineData("not-a-number")]
    public async Task InvalidLimit_IsRejected(string limit)
    {
        await using var host = await TestQueueHost.StartAsync();
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/v1/cases/mine?limit={limit}");
        TestAuthenticationHandler.AddCookieAuthentication(request);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Service.Calls);
    }

    [Fact]
    public async Task OversizedLimit_IsClampedToServiceMaximum()
    {
        await using var host = await TestQueueHost.StartAsync();
        using var request = new HttpRequestMessage(
            HttpMethod.Get, "/api/v1/cases/mine?limit=9999");
        TestAuthenticationHandler.AddCookieAuthentication(request);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(CaseQueueQuery.MaxLimit, host.Service.LastLimit);
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
        // No ICaseQueueQuery: the case command pipeline is disabled here.
        using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapCaseQueueEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/cases/mine");
        TestAuthenticationHandler.AddCookieAuthentication(request);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await app.StopAsync();
    }

    [Fact]
    public void MapResult_CoversEveryAccess()
    {
        var allowed = CaseQueueEndpoints.MapResult(
            new CaseQueueResult(CaseQueueAccess.Allowed, []));
        Assert.Equal(200, ((Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<CaseQueueResult>)allowed).StatusCode);

        var denied = CaseQueueEndpoints.MapResult(
            new CaseQueueResult(CaseQueueAccess.Denied, []));
        Assert.Equal(200, ((Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<CaseQueueResult>)denied).StatusCode);

        var unavailable = CaseQueueEndpoints.MapResult(
            new CaseQueueResult(CaseQueueAccess.Unavailable, []));
        Assert.Equal(503, ((Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult)unavailable).StatusCode);
    }

    /// <summary>
    /// The shared case policy requires region coverage: every case region must sit
    /// inside the grant's regions. This helper scopes the officer's current grants
    /// and the case to one shared test region without touching the shared seeds.
    /// </summary>
    private static async Task SeedRegionScopeAsync(SwiftAidDbContext db, Guid caseId)
    {
        const string regionId = "region-harare-1";
        var groupIds = await db.AccessGrantGroups.AsNoTracking()
            .Where(item => item.ActorId == "officer-1")
            .Select(item => item.GrantGroupId)
            .ToArrayAsync();
        var existingGroups = await db.AccessGrantRegions.AsNoTracking()
            .Where(item => item.RegionId == regionId && groupIds.Contains(item.GrantGroupId))
            .Select(item => item.GrantGroupId)
            .ToArrayAsync();
        foreach (var groupId in groupIds.Except(existingGroups))
        {
            db.AccessGrantRegions.Add(new AccessGrantRegionEntity
            {
                GrantGroupId = groupId,
                RegionId = regionId
            });
        }

        var caseScoped = await db.ManagedCaseRegions.AsNoTracking()
            .AnyAsync(item => item.CaseId == caseId && item.RegionId == regionId);
        if (!caseScoped)
        {
            db.ManagedCaseRegions.Add(new ManagedCaseRegionEntity
            {
                CaseId = caseId,
                RegionId = regionId
            });
        }

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task ReassignOwnerAsync(SwiftAidDbContext db, Guid caseId, string ownerActorId)
    {
        // The owner FK chain requires an actor row plus an access row; the other
        // officer never logs in here, so both are seeded idempotently.
        if (!await db.Actors.AsNoTracking().AnyAsync(item => item.ActorId == ownerActorId))
        {
            var now = DateTimeOffset.UtcNow;
            db.Actors.Add(new ActorEntity
            {
                ActorId = ownerActorId,
                Status = PersistentActorStatus.Active,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            db.ActorAccessStates.Add(new ActorAccessStateEntity
            {
                ActorId = ownerActorId,
                AccessEpoch = 7,
                Version = 1,
                UpdatedAtUtc = now
            });
        }

        var entity = await db.ManagedCases.SingleAsync(item => item.CaseId == caseId);
        entity.AccountableOwnerActorId = ownerActorId;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static CaseQueueQuery CreateQuery(SwiftAidDbContext db)
    {
        // Policy stores resolve the ambient case lease through the accessor, so all
        // three collaborators must share one unit of work.
        var unitOfWork = new PostgresCaseCommandUnitOfWork(db);
        return new CaseQueueQuery(
            new PostgresCaseQueueStore(db),
            new CaseAccessPolicy(
                new PostgresCurrentAccessGrantStore(db, unitOfWork),
                new PostgresCaseAccessDescriptorStore(db, unitOfWork)),
            unitOfWork);
    }

    private static ActorContext CreateOfficer(string actorId) => ActorContext.FromValidatedPrincipal(
        new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, actorId),
            new Claim(ActorContext.AccessEpochClaimType, "7")
        ], "synthetic-test")));

    private static async Task SeedEventAsync(
        SwiftAidDbContext db, Guid caseId, long version, string eventType, DateTimeOffset recordedAt)
    {
        var payloadId = Guid.NewGuid();
        db.ProtectedEventPayloads.Add(new ProtectedEventPayloadEntity
        {
            PayloadId = payloadId,
            ProtectedBytes = [1],
            ProtectionPurposeVersion = "queue-test.v1",
            CreatedAtUtc = recordedAt
        });
        var commandId = Guid.NewGuid();
        db.CommandReceipts.Add(new CommandReceiptEntity
        {
            ReceiptId = Guid.NewGuid(),
            ActorId = "officer-1",
            CommandId = commandId,
            CaseId = caseId,
            CommandType = "QueueSeed",
            SchemaVersion = 1,
            CanonicalPayloadHash = new byte[32],
            FirstAggregateVersion = version,
            LastAggregateVersion = version,
            ReportRevision = 1,
            AcceptedAtUtc = recordedAt
        });
        db.CaseEvents.Add(new CaseEventEntity
        {
            EventId = Guid.NewGuid(),
            CaseId = caseId,
            AggregateVersion = version,
            ReportRevision = 1,
            CommandId = commandId,
            ActorId = "officer-1",
            EventType = eventType,
            SchemaVersion = 1,
            OccurredAtUtc = recordedAt,
            RecordedAtUtc = recordedAt,
            PayloadId = payloadId
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }
}

internal sealed class TestQueueHost : IAsyncDisposable
{
    private readonly WebApplication _application;

    public HttpClient Client { get; }

    public TestQueueQuery Service { get; }

    private TestQueueHost(WebApplication application, HttpClient client, TestQueueQuery service)
    {
        _application = application;
        Client = client;
        Service = service;
    }

    public static async Task<TestQueueHost> StartAsync(
        CaseQueueAccess access = CaseQueueAccess.Allowed,
        IReadOnlyList<CaseQueueItem>? items = null)
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

        var service = new TestQueueQuery(TestQueueQuery.SomeCaseId, access, items);
        builder.Services.AddSingleton<ICaseQueueQuery>(service);

        var application = builder.Build();
        application.UseAuthentication();
        application.UseAuthorization();
        application.MapCaseQueueEndpoints();
        await application.StartAsync().ConfigureAwait(false);

        return new TestQueueHost(application, application.GetTestClient(), service);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _application.DisposeAsync().ConfigureAwait(false);
    }
}

internal sealed class TestQueueQuery(
    Guid fixedCaseId,
    CaseQueueAccess access = CaseQueueAccess.Allowed,
    IReadOnlyList<CaseQueueItem>? items = null) : ICaseQueueQuery
{
    public static readonly Guid SomeCaseId = Guid.Parse("b22ce000-2222-4d33-9ee9-23456789abcd");

    public int Calls { get; private set; }

    public int? LastLimit { get; private set; }

    public Task<CaseQueueResult> GetMineAsync(
        ActorContext actor,
        int? limit,
        CancellationToken ct)
    {
        Calls++;
        LastLimit = limit;
        var entries = items ?? new List<CaseQueueItem>
        {
            new(fixedCaseId, "Reported", "ZW", "officer-1", 2, DateTimeOffset.UtcNow)
        };
        return Task.FromResult(access == CaseQueueAccess.Allowed
            ? new CaseQueueResult(access, entries)
            : new CaseQueueResult(access, []));
    }
}
