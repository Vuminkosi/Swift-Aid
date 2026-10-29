using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Infrastructure.Identity;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Integration.Tests;

/// <summary>Exercises membership reads and join-request rules with seeded rows.</summary>
public sealed class OrganizationMembershipServiceTests : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly SwiftAidDbContext _dbContext;
    private readonly ManualMembershipTimeProvider _timeProvider = new(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero));

    public OrganizationMembershipServiceTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(_timeProvider);
        services.AddDbContext<SwiftAidDbContext>(options => options.UseSqlite("DataSource=:memory:"));
        services.AddScoped<IOrganizationMembershipService, OrganizationMembershipService>();
        _provider = services.BuildServiceProvider();
        _dbContext = _provider.GetRequiredService<SwiftAidDbContext>();
        _dbContext.Database.OpenConnection();
        CreateTables();
        Seed();
    }

    [Fact]
    public async Task Mine_lists_memberships_with_workspaces()
    {
        var service = _provider.GetRequiredService<IOrganizationMembershipService>();
        var mine = await service.GetMineAsync("actor-1", CancellationToken.None);

        var membership = Assert.Single(mine);
        Assert.Equal("org-1", membership.OrganizationId);
        Assert.Equal("Active", membership.MembershipStatus);
        Assert.Contains("ws-1", membership.WorkspaceIds);
    }

    [Fact]
    public async Task Mine_is_empty_for_unknown_actor()
    {
        var service = _provider.GetRequiredService<IOrganizationMembershipService>();
        Assert.Empty(await service.GetMineAsync("nobody", CancellationToken.None));
    }

    [Fact]
    public async Task Directory_lists_only_active_organizations()
    {
        var service = _provider.GetRequiredService<IOrganizationMembershipService>();
        var entries = await service.SearchAsync(null, CancellationToken.None);

        Assert.Contains(entries, entry => entry.OrganizationId == "org-1");
        Assert.Contains(entries, entry => entry.OrganizationId == "org-2");
        Assert.DoesNotContain(entries, entry => entry.OrganizationId == "org-suspended");

        var filtered = await service.SearchAsync("org-2", CancellationToken.None);
        Assert.Equal("org-2", Assert.Single(filtered).OrganizationId);
    }

    [Fact]
    public async Task Request_creates_pending_then_rejects_duplicates()
    {
        var service = _provider.GetRequiredService<IOrganizationMembershipService>();

        var first = await service.RequestAsync("actor-3", "org-2", "Field work", CancellationToken.None);
        Assert.Equal(MembershipRequestSubmitStatus.Submitted, first.Status);
        Assert.NotNull(first.Request);

        var duplicate = await service.RequestAsync("actor-3", "org-2", null, CancellationToken.None);
        Assert.Equal(MembershipRequestSubmitStatus.DuplicatePending, duplicate.Status);

        var requests = await service.GetMyRequestsAsync("actor-3", CancellationToken.None);
        var pending = Assert.Single(requests);
        Assert.Equal("Pending", pending.Status);
        Assert.Equal("org-2", pending.OrganizationId);
    }

    [Fact]
    public async Task Request_rejects_members_and_unknown_organizations()
    {
        var service = _provider.GetRequiredService<IOrganizationMembershipService>();

        Assert.Equal(
            MembershipRequestSubmitStatus.AlreadyMember,
            (await service.RequestAsync("actor-1", "org-1", null, CancellationToken.None)).Status);
        Assert.Equal(
            MembershipRequestSubmitStatus.OrganizationUnavailable,
            (await service.RequestAsync("actor-1", "org-missing", null, CancellationToken.None)).Status);
        Assert.Equal(
            MembershipRequestSubmitStatus.Invalid,
            (await service.RequestAsync("actor-1", "  ", null, CancellationToken.None)).Status);
    }

    public async ValueTask DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _provider.DisposeAsync();
    }

    private void CreateTables()
    {
        _dbContext.Database.ExecuteSqlRaw("""
            CREATE TABLE actors (
                actor_id TEXT NOT NULL PRIMARY KEY,
                status TEXT NOT NULL,
                expires_at_utc TEXT,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                lock_token SMALLINT NOT NULL DEFAULT 0
            );
            CREATE TABLE organizations (
                organization_id TEXT NOT NULL PRIMARY KEY,
                status TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                lock_token SMALLINT NOT NULL DEFAULT 0
            );
            CREATE TABLE workspaces (
                workspace_id TEXT NOT NULL PRIMARY KEY,
                organization_id TEXT NOT NULL REFERENCES organizations (organization_id),
                status TEXT NOT NULL,
                lock_token SMALLINT NOT NULL DEFAULT 0
            );
            CREATE TABLE organization_memberships (
                membership_id TEXT NOT NULL PRIMARY KEY,
                actor_id TEXT NOT NULL REFERENCES actors (actor_id),
                organization_id TEXT NOT NULL REFERENCES organizations (organization_id),
                status TEXT NOT NULL,
                valid_from_utc TEXT NOT NULL,
                expires_at_utc TEXT,
                granted_by_actor_id TEXT NOT NULL,
                revoked_by_actor_id TEXT,
                change_reason TEXT,
                version INTEGER NOT NULL,
                lock_token SMALLINT NOT NULL DEFAULT 0
            );
            CREATE TABLE membership_requests (
                request_id TEXT NOT NULL PRIMARY KEY,
                actor_id TEXT NOT NULL REFERENCES actors (actor_id),
                organization_id TEXT NOT NULL REFERENCES organizations (organization_id),
                status TEXT NOT NULL,
                reason TEXT,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            );
            PRAGMA foreign_keys = ON;
            """);
    }

    private void Seed()
    {
        var now = _timeProvider.GetUtcNow();
        _dbContext.Actors.AddRange(
            new ActorEntity { ActorId = "actor-1", Status = PersistentActorStatus.Active, CreatedAtUtc = now, UpdatedAtUtc = now },
            new ActorEntity { ActorId = "actor-3", Status = PersistentActorStatus.Active, CreatedAtUtc = now, UpdatedAtUtc = now });
        _dbContext.Organizations.AddRange(
            new OrganizationEntity { OrganizationId = "org-1", Status = PersistentResourceStatus.Active, CreatedAtUtc = now, UpdatedAtUtc = now },
            new OrganizationEntity { OrganizationId = "org-2", Status = PersistentResourceStatus.Active, CreatedAtUtc = now, UpdatedAtUtc = now },
            new OrganizationEntity { OrganizationId = "org-suspended", Status = PersistentResourceStatus.Suspended, CreatedAtUtc = now, UpdatedAtUtc = now });
        _dbContext.Workspaces.Add(
            new WorkspaceEntity { WorkspaceId = "ws-1", OrganizationId = "org-1", Status = PersistentResourceStatus.Active });
        _dbContext.OrganizationMemberships.Add(
            new OrganizationMembershipEntity
            {
                MembershipId = Guid.NewGuid(),
                ActorId = "actor-1",
                OrganizationId = "org-1",
                Status = PersistentMembershipStatus.Active,
                ValidFromUtc = now,
                GrantedByActorId = "actor-1",
                Version = 1
            });
        _dbContext.SaveChanges();
    }

    private sealed class ManualMembershipTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
