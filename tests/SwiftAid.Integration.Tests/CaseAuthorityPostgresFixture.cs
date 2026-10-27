using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using SwiftAid.Contracts.Commands;
using SwiftAid.Application.Authorization;
using SwiftAid.Domain.Cases;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Integration.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class CaseAuthorityPostgresFactAttribute : FactAttribute
{
    public CaseAuthorityPostgresFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SWIFTAID_RUN_SQLAUTH_FIXTURES"), "1", StringComparison.Ordinal))
        {
            Skip = "Set SWIFTAID_RUN_SQLAUTH_FIXTURES=1 to run the disposable PostgreSQL authorization fixtures.";
        }
    }
}

internal sealed class CaseAuthorityPostgresFixture : IAsyncDisposable
{
    private CaseAuthorityPostgresFixture(string connectionString)
    {
        ConnectionString = connectionString;
        var suffix = Guid.NewGuid().ToString("N");
        ActorId = $"test-actor-{suffix}";
        OwnerActorId = $"test-owner-{suffix}";
        NewOwnerActorId = $"test-new-owner-{suffix}";
        OrganizationId = $"test-org-{suffix}";
        WorkspaceId = $"test-ws-{suffix}";
        AlternateWorkspaceId = $"test-alt-ws-{suffix}";
        GrantGroupId = $"test-grant-{suffix}";
        CaseId = Guid.NewGuid();
        SiblingCaseId = Guid.NewGuid();
        MembershipId = Guid.NewGuid();
        CaseRegionId = $"test-case-region-{suffix}";
        SiblingCaseRegionId = $"test-sibling-region-{suffix}";
        GrantRegionId = $"test-grant-region-{suffix}";
        AccessEpoch = 17;
    }

    public string ConnectionString { get; }

    public string ActorId { get; }

    public string OwnerActorId { get; }

    public string NewOwnerActorId { get; }

    public string OrganizationId { get; }

    public string WorkspaceId { get; }

    public string AlternateWorkspaceId { get; }

    public string GrantGroupId { get; }

    public Guid CaseId { get; }

    public Guid SiblingCaseId { get; }

    public Guid MembershipId { get; }

    public string CaseRegionId { get; }

    public string SiblingCaseRegionId { get; }

    public string GrantRegionId { get; }

    public long AccessEpoch { get; }

    public DateTimeOffset? ShareExpiresAtUtc { get; private set; }

    public DateTimeOffset? ActorExpiresAtUtc { get; private set; }

    public DateTimeOffset MembershipExpiresAtUtc { get; private set; }

    public DateTimeOffset GrantExpiresAtUtc { get; private set; }

    public static async Task<CaseAuthorityPostgresFixture> CreateAsync(
        bool makeCurrentActorOwner = true,
        PersistentActorStatus actorStatus = PersistentActorStatus.Active,
        PersistentMembershipStatus membershipStatus = PersistentMembershipStatus.Active,
        PersistentResourceStatus organizationStatus = PersistentResourceStatus.Active,
        PersistentResourceStatus workspaceStatus = PersistentResourceStatus.Active,
        DateTimeOffset? grantExpiresAtUtc = null,
        bool createActiveShare = false,
        string? grantWorkspaceId = null,
        DateTimeOffset? actorExpiresAtUtc = null,
        DateTimeOffset? membershipExpiresAtUtc = null,
        DateTimeOffset? shareExpiresAtUtc = null)
    {
        var fixture = new CaseAuthorityPostgresFixture(GetValidatedConnectionString());
        await fixture.SeedAsync(
                makeCurrentActorOwner,
                actorStatus,
                membershipStatus,
                organizationStatus,
                workspaceStatus,
                grantExpiresAtUtc,
                createActiveShare,
                grantWorkspaceId,
                actorExpiresAtUtc,
                membershipExpiresAtUtc,
                shareExpiresAtUtc)
            .ConfigureAwait(false);
        return fixture;
    }

    public ActorContext CreateActor(long? accessEpoch = null) => ActorContext.FromValidatedPrincipal(
        new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
        [
            new System.Security.Claims.Claim("sub", ActorId),
            new System.Security.Claims.Claim(ActorContext.AccessEpochClaimType,
                (accessEpoch ?? AccessEpoch).ToString(System.Globalization.CultureInfo.InvariantCulture))
        ], authenticationType: "fixture-authenticated")));

    public SwiftAidDbContext CreateDbContext(DbCommandInterceptor? interceptor = null)
    {
        var optionsBuilder = new DbContextOptionsBuilder<SwiftAidDbContext>().UseNpgsql(ConnectionString);
        if (interceptor is not null)
        {
            optionsBuilder.AddInterceptors(interceptor);
        }

        var options = optionsBuilder.Options;
        return new SwiftAidDbContext(options);
    }

    public SwiftAidDbContext CreateRuntimeContext(
        CaseAuthorityRuntimeProfile profile,
        DbCommandInterceptor? interceptor = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.CreateRuntimeContext(this, interceptor);
    }

    public async Task SetCaseOwnerAsync(string ownerActorId)
    {
        await using var dbContext = CreateDbContext();
        await dbContext.ManagedCases
            .Where(entity => entity.CaseId == CaseId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(entity => entity.AccountableOwnerActorId, ownerActorId))
            .ConfigureAwait(false);
    }

    public async Task SetGrantWorkspaceAsync(string workspaceId)
    {
        await using var dbContext = CreateDbContext();
        await dbContext.AccessGrantGroups
            .Where(entity => entity.GrantGroupId == GrantGroupId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(entity => entity.WorkspaceId, workspaceId))
            .ConfigureAwait(false);
    }

    public async Task SetGrantExpiryAsync(DateTimeOffset expiresAtUtc)
    {
        await using var dbContext = CreateDbContext();
        await dbContext.AccessGrantGroups
            .Where(entity => entity.GrantGroupId == GrantGroupId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(entity => entity.ExpiresAtUtc, expiresAtUtc))
            .ConfigureAwait(false);
    }

    public async Task SetMembershipExpiryAsync(DateTimeOffset expiresAtUtc)
    {
        await using var dbContext = CreateDbContext();
        await dbContext.OrganizationMemberships
            .Where(entity => entity.MembershipId == MembershipId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(entity => entity.ExpiresAtUtc, expiresAtUtc))
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await using var dbContext = CreateDbContext();
        await using var transaction = await dbContext.Database.BeginTransactionAsync().ConfigureAwait(false);

        await dbContext.AccessGrantOperations.Where(entity => entity.GrantGroupId == GrantGroupId)
            .ExecuteDeleteAsync().ConfigureAwait(false);
        await dbContext.AccessGrantRegions.Where(entity => entity.GrantGroupId == GrantGroupId)
            .ExecuteDeleteAsync().ConfigureAwait(false);
        await dbContext.AccessGrantGroups.Where(entity => entity.GrantGroupId == GrantGroupId)
            .ExecuteDeleteAsync().ConfigureAwait(false);
        var caseIds = new[] { CaseId, SiblingCaseId };
        await dbContext.CaseShares.Where(entity => caseIds.Contains(entity.CaseId))
            .ExecuteDeleteAsync().ConfigureAwait(false);
        await dbContext.ManagedCaseRegions.Where(entity => caseIds.Contains(entity.CaseId))
            .ExecuteDeleteAsync().ConfigureAwait(false);
        await dbContext.ManagedCases.Where(entity => caseIds.Contains(entity.CaseId))
            .ExecuteDeleteAsync().ConfigureAwait(false);
        await dbContext.OrganizationMemberships.Where(entity => entity.MembershipId == MembershipId)
            .ExecuteDeleteAsync().ConfigureAwait(false);
        await dbContext.ActorAccessStates
            .Where(entity => entity.ActorId == ActorId || entity.ActorId == OwnerActorId || entity.ActorId == NewOwnerActorId)
            .ExecuteDeleteAsync().ConfigureAwait(false);
        await dbContext.Workspaces
            .Where(entity => entity.WorkspaceId == WorkspaceId || entity.WorkspaceId == AlternateWorkspaceId)
            .ExecuteDeleteAsync().ConfigureAwait(false);
        await dbContext.Actors
            .Where(entity => entity.ActorId == ActorId || entity.ActorId == OwnerActorId || entity.ActorId == NewOwnerActorId)
            .ExecuteDeleteAsync().ConfigureAwait(false);
        await dbContext.Organizations.Where(entity => entity.OrganizationId == OrganizationId)
            .ExecuteDeleteAsync().ConfigureAwait(false);

        await transaction.CommitAsync().ConfigureAwait(false);
    }

    private async Task SeedAsync(
        bool makeCurrentActorOwner,
        PersistentActorStatus actorStatus,
        PersistentMembershipStatus membershipStatus,
        PersistentResourceStatus organizationStatus,
        PersistentResourceStatus workspaceStatus,
        DateTimeOffset? grantExpiresAtUtc,
        bool createActiveShare,
        string? grantWorkspaceId,
        DateTimeOffset? actorExpiresAtUtc,
        DateTimeOffset? membershipExpiresAtUtc,
        DateTimeOffset? shareExpiresAtUtc)
    {
        await using var dbContext = CreateDbContext();
        if (!await dbContext.Database.CanConnectAsync().ConfigureAwait(false))
        {
            throw new InvalidOperationException("The configured disposable PostgreSQL database is unavailable.");
        }

        var now = DateTimeOffset.UtcNow;
        var currentOwnerActorId = makeCurrentActorOwner ? ActorId : OwnerActorId;
        ActorExpiresAtUtc = actorExpiresAtUtc;
        MembershipExpiresAtUtc = membershipExpiresAtUtc ?? now.AddDays(2);
        GrantExpiresAtUtc = grantExpiresAtUtc ?? now.AddDays(2);
        dbContext.Actors.AddRange(
            new ActorEntity
            {
                ActorId = ActorId,
                Status = actorStatus,
                ExpiresAtUtc = actorExpiresAtUtc,
                CreatedAtUtc = now.AddDays(-1),
                UpdatedAtUtc = now
            },
            new ActorEntity
            {
                ActorId = OwnerActorId,
                Status = PersistentActorStatus.Active,
                CreatedAtUtc = now.AddDays(-1),
                UpdatedAtUtc = now
            },
            new ActorEntity
            {
                ActorId = NewOwnerActorId,
                Status = PersistentActorStatus.Active,
                CreatedAtUtc = now.AddDays(-1),
                UpdatedAtUtc = now
            });
        dbContext.ActorAccessStates.AddRange(
            ActorState(ActorId, now),
            ActorState(OwnerActorId, now),
            ActorState(NewOwnerActorId, now));
        dbContext.Organizations.Add(new OrganizationEntity
        {
            OrganizationId = OrganizationId,
            Status = organizationStatus,
            CreatedAtUtc = now.AddDays(-1),
            UpdatedAtUtc = now
        });
        dbContext.Workspaces.AddRange(
            new WorkspaceEntity { WorkspaceId = WorkspaceId, OrganizationId = OrganizationId, Status = workspaceStatus },
            new WorkspaceEntity { WorkspaceId = AlternateWorkspaceId, OrganizationId = OrganizationId, Status = PersistentResourceStatus.Active });
        dbContext.OrganizationMemberships.Add(new OrganizationMembershipEntity
        {
            MembershipId = MembershipId,
            ActorId = ActorId,
            OrganizationId = OrganizationId,
            Status = membershipStatus,
            ValidFromUtc = now.AddDays(-1),
            ExpiresAtUtc = MembershipExpiresAtUtc,
            GrantedByActorId = ActorId,
            Version = 1
        });
        dbContext.AccessGrantGroups.Add(new AccessGrantGroupEntity
        {
            GrantGroupId = GrantGroupId,
            MembershipId = MembershipId,
            ActorId = ActorId,
            OrganizationId = OrganizationId,
            WorkspaceId = grantWorkspaceId ?? WorkspaceId,
            CountryCode = "SZ",
            Sector = Sector.HumanHealth,
            SensitivityCode = "sens-A",
            CanAccessJurisdictionalCases = false,
            Status = PersistentGrantStatus.Active,
            ValidFromUtc = now.AddDays(-1),
            ExpiresAtUtc = GrantExpiresAtUtc,
            GrantedByActorId = ActorId,
            Version = 1
        });
        dbContext.AccessGrantRegions.Add(new AccessGrantRegionEntity { GrantGroupId = GrantGroupId, RegionId = GrantRegionId });
        dbContext.AccessGrantOperations.Add(new AccessGrantOperationEntity
        {
            GrantGroupId = GrantGroupId,
            Operation = CaseOperation.ReadCase
        });
        dbContext.ManagedCases.AddRange(
            CreateManagedCase(CaseId, currentOwnerActorId, now),
            CreateManagedCase(SiblingCaseId, currentOwnerActorId, now));
        dbContext.ManagedCaseRegions.AddRange(
            new ManagedCaseRegionEntity { CaseId = CaseId, RegionId = CaseRegionId },
            new ManagedCaseRegionEntity { CaseId = SiblingCaseId, RegionId = SiblingCaseRegionId });
        if (createActiveShare)
        {
            ShareExpiresAtUtc = shareExpiresAtUtc ?? now.AddHours(6);
            dbContext.CaseShares.Add(new CaseShareEntity
            {
                CaseId = CaseId,
                ActorId = ActorId,
                Version = 1,
                IsActive = true,
                GrantedByActorId = ActorId,
                GrantedAtUtc = now.AddMinutes(-10),
                ExpiresAtUtc = ShareExpiresAtUtc
            });
        }

        await dbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    private static ActorAccessStateEntity ActorState(string actorId, DateTimeOffset now) => new()
    {
        ActorId = actorId,
        AccessEpoch = 17,
        Version = 1,
        UpdatedAtUtc = now
    };

    private ManagedCaseEntity CreateManagedCase(Guid caseId, string ownerActorId, DateTimeOffset now) => new()
    {
        CaseId = caseId,
        OrganizationId = OrganizationId,
        WorkspaceId = WorkspaceId,
        CountryCode = "SZ",
        Kind = CaseKind.Human,
        Sector = Sector.HumanHealth,
        SensitivityCode = "sens-A",
        AccountableOwnerActorId = ownerActorId,
        ResourceVersion = 1,
        AggregateVersion = 2,
        ReportRevision = 1,
        Workflow = CaseWorkflow.Reported,
        WorkflowVersion = 0,
        Classification = ClinicalClassification.Unclassified,
        ClassificationVersion = 0,
        Laboratory = LaboratoryState.NotRecorded,
        LaboratoryVersion = 0,
        OutcomeState = ReportedValueState.Unknown,
        OutcomeValue = null,
        OutcomeVersion = 0,
        CreatedAtUtc = now.AddDays(-1),
        UpdatedAtUtc = now
    };

    private static string GetValidatedConnectionString()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SWIFTAID_RUN_SQLAUTH_FIXTURES"), "1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Set SWIFTAID_RUN_SQLAUTH_FIXTURES=1 to run PostgreSQL authority integration tests.");
        }

        var value = Environment.GetEnvironmentVariable("SWIFTAID_TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("The disposable PostgreSQL test connection is required when SQLAUTH fixtures are enabled.");
        }

        var builder = new NpgsqlConnectionStringBuilder(value);
        if (builder.Host is not ("127.0.0.1" or "localhost" or "::1") ||
            builder.Port != 32703 ||
            !string.Equals(builder.Database, "swiftaid_p21a", StringComparison.Ordinal) ||
            !string.Equals(builder.Username, "p21_migration_owner", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(builder.Password))
        {
            throw new InvalidOperationException("The PostgreSQL fixture target is not the approved disposable loopback database.");
        }

        builder.Timeout = Math.Clamp(builder.Timeout, 1, 5);
        builder.CommandTimeout = Math.Clamp(builder.CommandTimeout, 1, 10);
        return builder.ConnectionString;
    }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class CaseAuthorityPostgresTheoryAttribute : TheoryAttribute
{
    public CaseAuthorityPostgresTheoryAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SWIFTAID_RUN_SQLAUTH_FIXTURES"), "1", StringComparison.Ordinal))
        {
            Skip = "Set SWIFTAID_RUN_SQLAUTH_FIXTURES=1 to run the disposable PostgreSQL authorization fixtures.";
        }
    }
}
