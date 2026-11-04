using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SwiftAid.Application.Authorization;
using SwiftAid.Infrastructure.Authorization;
using SwiftAid.Infrastructure.Configuration;
using SwiftAid.Infrastructure.Demo;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Integration.Tests;

public sealed class SessionScopePostgresTests
{
    [CaseAuthorityPostgresFact]
    public async Task Gateway_concurrent_scope_reads_use_separate_contexts()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync(frameworkFixture: true);
        await using (var db = fixture.CreateDbContext()) await LinkAsync(db, fixture);
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped(_ => fixture.CreateDbContext());
        services.AddScoped<PostgresSessionScopeService>();
        await using var provider = services.BuildServiceProvider();
        var reader = new SwiftAid.Gateway.Ellai.GatewaySessionScopeService(provider.GetRequiredService<IServiceScopeFactory>());
        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(async _ => await reader.GetAsync(fixture.CreateActor(), default)));
        Assert.All(results, result => Assert.Contains(fixture.CaseId, Assert.Single(result.Grants).CaseIds));
    }

    [CaseAuthorityPostgresFact]
    public async Task Expired_shares_and_different_workspace_do_not_enter_scope()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync(
            makeCurrentActorOwner: false, createActiveShare: true, frameworkFixture: true);
        await using var db = fixture.CreateDbContext();
        await LinkAsync(db, fixture);
        var service = new PostgresSessionScopeService(db, TimeProvider.System);
        await db.ManagedCases.Where(row => row.CaseId == fixture.CaseId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.WorkspaceId, fixture.AlternateWorkspaceId));
        Assert.DoesNotContain((await service.GetAsync(fixture.CreateActor(), default)).Grants,
            row => row.CaseIds.Contains(fixture.CaseId));
        await db.ManagedCases.Where(row => row.CaseId == fixture.CaseId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.WorkspaceId, fixture.WorkspaceId));
        await db.CaseShares.Where(row => row.ActorId == fixture.ActorId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.GrantedAtUtc, DateTimeOffset.UtcNow.AddDays(-2))
                .SetProperty(row => row.ExpiresAtUtc, DateTimeOffset.UtcNow.AddDays(-1)));
        Assert.DoesNotContain((await service.GetAsync(fixture.CreateActor(), default)).Grants,
            row => row.CaseIds.Contains(fixture.CaseId));
    }

    [CaseAuthorityPostgresFact]
    public async Task Disabled_demo_access_creates_no_grants()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync(frameworkFixture: true);
        await using var db = fixture.CreateDbContext();
        await new DemoAccessProvisioner(db, new ConfigurationBuilder().Build(), new TestEnvironment(), TimeProvider.System)
            .EnsureAsync(fixture.CreateActor(), default);
        Assert.Equal(1, await db.AccessGrantGroups.CountAsync(row => row.ActorId == fixture.ActorId));
    }

    [CaseAuthorityPostgresFact]
    public async Task Assigned_scope_works_without_command_lease_and_rejects_stale_epoch()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync(frameworkFixture: true);
        await using var db = fixture.CreateDbContext();
        await LinkAsync(db, fixture);
        var service = new PostgresSessionScopeService(db, TimeProvider.System);
        var result = await service.GetAsync(fixture.CreateActor(), default);
        Assert.Contains(fixture.CaseId, Assert.Single(result.Grants).CaseIds);
        var error = await Assert.ThrowsAsync<ScopeUnavailableException>(async () =>
            await service.GetAsync(fixture.CreateActor(fixture.AccessEpoch + 1), default));
        Assert.Equal(ScopeFailureCode.AccessEpochMismatch, error.Code);
    }

    [CaseAuthorityPostgresFact]
    public async Task Shared_scope_preserves_operation_and_expiry_boundaries_and_revocation()
    {
        var expiry = DateTimeOffset.UtcNow.AddMinutes(20);
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync(
            makeCurrentActorOwner: false, createActiveShare: true, shareExpiresAtUtc: expiry, frameworkFixture: true);
        await using var db = fixture.CreateDbContext();
        await LinkAsync(db, fixture);
        await db.CaseShares.Where(row => row.ActorId == fixture.ActorId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.AllowedOperations, new[] { "ReadCase" }));
        db.AccessGrantOperations.Add(new AccessGrantOperationEntity { GrantGroupId = fixture.GrantGroupId, Operation = CaseOperation.Export });
        await db.SaveChangesAsync();
        var service = new PostgresSessionScopeService(db, TimeProvider.System);
        var result = await service.GetAsync(fixture.CreateActor(), default);
        var share = Assert.Single(result.Grants, row => row.CaseIds.Contains(fixture.CaseId));
        Assert.Equal(new[] { "ReadCase" }, share.Operations);
        Assert.False(share.CanAccessJurisdictionalCases);
        Assert.True((share.ExpiresAtUtc - expiry).Duration() < TimeSpan.FromMilliseconds(1));
        await db.CaseShares.Where(row => row.ActorId == fixture.ActorId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.IsActive, false));
        result = await service.GetAsync(fixture.CreateActor(), default);
        Assert.DoesNotContain(result.Grants, row => row.CaseIds.Contains(fixture.CaseId));
        await db.OrganizationMemberships.Where(row => row.MembershipId == fixture.MembershipId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, PersistentMembershipStatus.Suspended));
        await Assert.ThrowsAsync<ScopeUnavailableException>(async () => await service.GetAsync(fixture.CreateActor(), default));
    }

    [CaseAuthorityPostgresFact]
    public async Task Demo_grants_are_explicit_idempotent_and_limited_to_demo_workspace()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync(frameworkFixture: true);
        await using var db = fixture.CreateDbContext();
        var importer = new DemoCaseImporter(db, new DemoDataOptions { Enabled = true, MaxCases = 10 },
            TimeProvider.System, NullLogger<DemoCaseImporter>.Instance);
        await importer.ImportAsync(default);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["DemoAccess:Enabled"] = "true" }).Build();
        var provisioner = new DemoAccessProvisioner(db, config, new TestEnvironment(), TimeProvider.System);
        await provisioner.EnsureAsync(fixture.CreateActor(), default);
        var count = await db.AccessGrantGroups.CountAsync(row => row.ActorId == fixture.ActorId);
        await provisioner.EnsureAsync(fixture.CreateActor(), default);
        Assert.Equal(count, await db.AccessGrantGroups.CountAsync(row => row.ActorId == fixture.ActorId));
        var scope = await new PostgresSessionScopeService(db, TimeProvider.System).GetAsync(fixture.CreateActor(), default);
        var grants = scope.Grants.Where(row => row.GroupId.StartsWith("demo-access:", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(grants);
        Assert.All(grants, grant =>
        {
            Assert.Equal(new[] { DemoCaseImporter.DemoWorkspaceId }, grant.WorkspaceIds);
            Assert.Equal(Enum.GetNames<CaseOperation>().Order(), grant.Operations.Order());
        });
        var production = new DemoAccessProvisioner(db, config,
            new TestEnvironment { EnvironmentName = Environments.Production }, TimeProvider.System);
        await Assert.ThrowsAsync<InvalidOperationException>(() => production.EnsureAsync(fixture.CreateActor(), default));
        var demoIds = db.AccessGrantGroups.Where(row => row.ActorId == fixture.ActorId
            && row.OrganizationId == DemoCaseImporter.DemoOrganizationId).Select(row => row.GrantGroupId);
        await db.AccessGrantOperations.Where(row => demoIds.Contains(row.GrantGroupId)).ExecuteDeleteAsync();
        await db.AccessGrantRegions.Where(row => demoIds.Contains(row.GrantGroupId)).ExecuteDeleteAsync();
        await db.AccessGrantGroups.Where(row => demoIds.Contains(row.GrantGroupId)).ExecuteDeleteAsync();
        await db.CaseShares.Where(row => row.ActorId == fixture.ActorId).ExecuteDeleteAsync();
        await db.OrganizationMemberships.Where(row => row.ActorId == fixture.ActorId
            && row.OrganizationId == DemoCaseImporter.DemoOrganizationId).ExecuteDeleteAsync();
    }

    private static async Task LinkAsync(SwiftAidDbContext db, CaseAuthorityPostgresFixture fixture)
    {
        if (await db.WorkspaceOrganizations.AnyAsync(row => row.WorkspaceId == fixture.WorkspaceId
            && row.OrganizationId == fixture.OrganizationId)) return;
        db.WorkspaceOrganizations.Add(new WorkspaceOrganizationEntity
        {
            OrganizationId = fixture.OrganizationId, WorkspaceId = fixture.WorkspaceId,
            Status = PersistentResourceStatus.Active, CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ScopeTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
