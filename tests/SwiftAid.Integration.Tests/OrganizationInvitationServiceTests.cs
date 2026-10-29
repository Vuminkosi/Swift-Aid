using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Infrastructure.Identity;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Integration.Tests;

/// <summary>Exercises invitation-code redemption: valid codes fast-track a join
/// request for staff review, while used, expired, revoked or unknown codes fail
/// without creating memberships or requests.</summary>
public sealed class OrganizationInvitationServiceTests : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly SwiftAidDbContext _dbContext;
    private readonly ManualInvitationTimeProvider _timeProvider = new(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero));

    public OrganizationInvitationServiceTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(_timeProvider);
        services.AddDbContext<SwiftAidDbContext>(options => options.UseSqlite("DataSource=:memory:"));
        services.AddScoped<IOrganizationMembershipService, OrganizationMembershipService>();
        services.AddScoped<IOrganizationInvitationService, OrganizationInvitationService>();
        _provider = services.BuildServiceProvider();
        _dbContext = _provider.GetRequiredService<SwiftAidDbContext>();
        _dbContext.Database.OpenConnection();
        CreateTables();
        Seed();
    }

    [Fact]
    public async Task Valid_code_creates_pending_request_and_marks_invitation_used()
    {
        var service = _provider.GetRequiredService<IOrganizationInvitationService>();

        var result = await service.RedeemAsync("actor-2", "join-org2-x1", CancellationToken.None);
        Assert.Equal(InvitationRedeemStatus.Redeemed, result.Status);
        Assert.Equal("org-2", result.OrganizationId);

        var requests = _dbContext.MembershipRequests
            .Where(entry => entry.ActorId == "actor-2" && entry.OrganizationId == "org-2")
            .ToList();
        var request = Assert.Single(requests);
        Assert.Equal(MembershipRequestStatus.Pending, request.Status);
        Assert.Equal(SeedInvitationId, request.InvitationId);

        var invitation = _dbContext.OrganizationInvitations.Single(entry => entry.InvitationId == SeedInvitationId);
        Assert.Equal(1, invitation.UseCount);
        Assert.Equal("Exhausted", invitation.Status);

        Assert.Empty(_dbContext.OrganizationMemberships
            .Where(entry => entry.ActorId == "actor-2" && entry.OrganizationId == "org-2"));
    }

    [Fact]
    public async Task Reused_single_use_code_is_exhausted()
    {
        var service = _provider.GetRequiredService<IOrganizationInvitationService>();

        Assert.Equal(
            InvitationRedeemStatus.Redeemed,
            (await service.RedeemAsync("actor-2", "JOIN-ORG2-X1", CancellationToken.None)).Status);
        Assert.Equal(
            InvitationRedeemStatus.Exhausted,
            (await service.RedeemAsync("actor-3", "join-org2-x1", CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Member_redeeming_is_already_member_without_side_effects()
    {
        var service = _provider.GetRequiredService<IOrganizationInvitationService>();

        Assert.Equal(
            InvitationRedeemStatus.AlreadyMember,
            (await service.RedeemAsync("actor-1", "join-org1-x1", CancellationToken.None)).Status);
        Assert.Empty(_dbContext.MembershipRequests
            .Where(entry => entry.ActorId == "actor-1" && entry.OrganizationId == "org-1"));
    }

    [Fact]
    public async Task Second_code_while_pending_is_already_requested_and_linked()
    {
        var service = _provider.GetRequiredService<IOrganizationInvitationService>();

        Assert.Equal(
            InvitationRedeemStatus.Redeemed,
            (await service.RedeemAsync("actor-2", "join-org2-x1", CancellationToken.None)).Status);
        Assert.Equal(
            InvitationRedeemStatus.AlreadyRequested,
            (await service.RedeemAsync("actor-2", "join-org2-x2", CancellationToken.None)).Status);

        var linked = _dbContext.MembershipRequests
            .Where(entry => entry.ActorId == "actor-2" && entry.OrganizationId == "org-2")
            .ToList();
        var request = Assert.Single(linked);
        Assert.Equal(SecondInvitationId, request.InvitationId);
    }

    [Fact]
    public async Task Unknown_malformed_revoked_and_expired_codes_fail()
    {
        var service = _provider.GetRequiredService<IOrganizationInvitationService>();

        Assert.Equal(
            InvitationRedeemStatus.InvalidCode,
            (await service.RedeemAsync("actor-2", "no-such-code-1", CancellationToken.None)).Status);
        Assert.Equal(
            InvitationRedeemStatus.InvalidCode,
            (await service.RedeemAsync("actor-2", "!!!", CancellationToken.None)).Status);
        Assert.Equal(
            InvitationRedeemStatus.InvalidCode,
            (await service.RedeemAsync("actor-2", "join-revoked-1", CancellationToken.None)).Status);
        var expired = await service.RedeemAsync("actor-2", "join-expired-1", CancellationToken.None);
        Assert.Equal(InvitationRedeemStatus.Expired, expired.Status);
        Assert.Equal("org-2", expired.OrganizationId);
        Assert.Empty(_dbContext.MembershipRequests
            .Where(entry => entry.ActorId == "actor-2"));
    }

    [Fact]
    public async Task Suspended_organization_is_unavailable()
    {
        var service = _provider.GetRequiredService<IOrganizationInvitationService>();

        var result = await service.RedeemAsync("actor-2", "join-suspended-1", CancellationToken.None);
        Assert.Equal(InvitationRedeemStatus.OrganizationUnavailable, result.Status);
        Assert.Empty(_dbContext.MembershipRequests
            .Where(entry => entry.ActorId == "actor-2"));
    }

    public async ValueTask DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _provider.DisposeAsync();
    }

    private static readonly Guid SeedInvitationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SecondInvitationId = Guid.Parse("22222222-2222-2222-2222-222222222222");

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
                invitation_id TEXT,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            );
            CREATE TABLE organization_invitations (
                invitation_id TEXT NOT NULL PRIMARY KEY,
                organization_id TEXT NOT NULL REFERENCES organizations (organization_id),
                code_hash TEXT NOT NULL,
                status TEXT NOT NULL,
                expires_at_utc TEXT,
                max_uses INTEGER NOT NULL,
                use_count INTEGER NOT NULL,
                created_by_actor_id TEXT,
                created_at_utc TEXT NOT NULL
            );
            CREATE UNIQUE INDEX ux_organization_invitations_code_hash ON organization_invitations (code_hash);
            PRAGMA foreign_keys = ON;
            """);
    }

    private void Seed()
    {
        var now = _timeProvider.GetUtcNow();
        _dbContext.Actors.AddRange(
            new ActorEntity { ActorId = "actor-1", Status = PersistentActorStatus.Active, CreatedAtUtc = now, UpdatedAtUtc = now },
            new ActorEntity { ActorId = "actor-2", Status = PersistentActorStatus.Active, CreatedAtUtc = now, UpdatedAtUtc = now },
            new ActorEntity { ActorId = "actor-3", Status = PersistentActorStatus.Active, CreatedAtUtc = now, UpdatedAtUtc = now });
        _dbContext.Organizations.AddRange(
            new OrganizationEntity { OrganizationId = "org-1", Status = PersistentResourceStatus.Active, CreatedAtUtc = now, UpdatedAtUtc = now },
            new OrganizationEntity { OrganizationId = "org-2", Status = PersistentResourceStatus.Active, CreatedAtUtc = now, UpdatedAtUtc = now },
            new OrganizationEntity { OrganizationId = "org-suspended", Status = PersistentResourceStatus.Suspended, CreatedAtUtc = now, UpdatedAtUtc = now });
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

        AddInvitation(SeedInvitationId, "org-2", "JOIN-ORG2-X1", "Active", null, 1);
        AddInvitation(SecondInvitationId, "org-2", "JOIN-ORG2-X2", "Active", null, 5);
        AddInvitation(Guid.Parse("33333333-3333-3333-3333-333333333333"), "org-1", "JOIN-ORG1-X1", "Active", null, 5);
        AddInvitation(Guid.Parse("44444444-4444-4444-4444-444444444444"), "org-2", "JOIN-REVOKED-1", "Revoked", null, 5);
        AddInvitation(Guid.Parse("55555555-5555-5555-5555-555555555555"), "org-2", "JOIN-EXPIRED-1", "Active", now.AddDays(-1), 5);
        AddInvitation(Guid.Parse("66666666-6666-6666-6666-666666666666"), "org-suspended", "JOIN-SUSPENDED-1", "Active", null, 5);
        _dbContext.SaveChanges();
    }

    private void AddInvitation(Guid id, string organizationId, string code, string status, DateTimeOffset? expires, int maxUses)
    {
        _dbContext.OrganizationInvitations.Add(new OrganizationInvitationEntity
        {
            InvitationId = id,
            OrganizationId = organizationId,
            CodeHash = OrganizationInvitationService.HashCode(
                OrganizationInvitationService.NormalizeCode(code)!),
            Status = status,
            ExpiresAtUtc = expires,
            MaxUses = maxUses,
            UseCount = 0,
            CreatedAtUtc = _timeProvider.GetUtcNow()
        });
    }

    [Fact]
    public async Task Staff_member_can_issue_a_code_that_redeems()
    {
        SeedStaffWorkspace();
        var service = _provider.GetRequiredService<IOrganizationInvitationService>();

        var issued = await service.CreateAsync("actor-1", "org-1", 5, _timeProvider.GetUtcNow().AddDays(30), CancellationToken.None);

        Assert.Equal(InvitationIssueStatus.Issued, issued.Status);
        Assert.Matches(@"^[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$", issued.Code);
        Assert.NotNull(issued.InvitationId);

        var stored = _dbContext.OrganizationInvitations.Single(entry => entry.InvitationId == issued.InvitationId);
        Assert.Equal(OrganizationInvitationService.HashCode(OrganizationInvitationService.NormalizeCode(issued.Code)!), stored.CodeHash);
        Assert.NotEqual(issued.Code, stored.CodeHash);
        Assert.Equal("actor-1", stored.CreatedByActorId);

        var redeemed = await service.RedeemAsync("actor-2", issued.Code!, CancellationToken.None);
        Assert.Equal(InvitationRedeemStatus.Redeemed, redeemed.Status);
        Assert.Equal("org-1", redeemed.OrganizationId);
    }

    [Fact]
    public async Task Non_staff_member_cannot_issue()
    {
        SeedStaffWorkspace();
        var service = _provider.GetRequiredService<IOrganizationInvitationService>();

        var result = await service.CreateAsync("actor-2", "org-1", 5, null, CancellationToken.None);

        Assert.Equal(InvitationIssueStatus.NotStaff, result.Status);
        Assert.Null(result.Code);
    }

    [Fact]
    public async Task Issue_rejects_unknown_organization_and_bad_input()
    {
        var service = _provider.GetRequiredService<IOrganizationInvitationService>();

        Assert.Equal(InvitationIssueStatus.OrganizationUnavailable,
            (await service.CreateAsync("actor-1", "org-nope", 5, null, CancellationToken.None)).Status);
        Assert.Equal(InvitationIssueStatus.Invalid,
            (await service.CreateAsync("actor-1", "org-1", 0, null, CancellationToken.None)).Status);
        Assert.Equal(InvitationIssueStatus.Invalid,
            (await service.CreateAsync("actor-1", "org-1", 5, _timeProvider.GetUtcNow().AddDays(-1), CancellationToken.None)).Status);
        Assert.Equal(InvitationIssueStatus.Invalid,
            (await service.CreateAsync(string.Empty, "org-1", 5, null, CancellationToken.None)).Status);
    }

    private void SeedStaffWorkspace()
    {
        _dbContext.Workspaces.Add(new WorkspaceEntity
        {
            WorkspaceId = "ws-1",
            OrganizationId = "org-1",
            Status = PersistentResourceStatus.Active
        });
        _dbContext.SaveChanges();
    }

    private sealed class ManualInvitationTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
