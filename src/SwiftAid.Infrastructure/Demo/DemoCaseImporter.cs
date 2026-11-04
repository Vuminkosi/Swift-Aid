using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Demo;
using SwiftAid.Contracts.Commands;
using SwiftAid.Domain.Cases;
using SwiftAid.Infrastructure.Configuration;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Infrastructure.Demo;

/// <summary>
/// Imports the development demo dataset (embedded <c>demodata.csv</c>) as managed
/// cases owned by the dedicated <c>demo-officer-1</c> actor. Demo rows are visible
/// only through that actor's authorized scope and never grant real privileges.
/// The import is idempotent: once demo cases exist, further runs change nothing.
/// </summary>
public sealed class DemoCaseImporter(
    SwiftAidDbContext dbContext,
    DemoDataOptions options,
    TimeProvider timeProvider,
    ILogger<DemoCaseImporter> logger) : IDemoCaseImporter
{
    public const string ResourceName = "SwiftAid.Infrastructure.Resources.Demodata.demodata.csv";
    public const string DemoOrganizationId = "org-demo-1";
    public const string DemoWorkspaceId = "workspace-demo-1";
    public const string DemoActorId = "demo-officer-1";
    private const long DemoAccessEpoch = 7;

    private static readonly Assembly ResourceAssembly = typeof(DemoCaseImporter).Assembly;

    private readonly SwiftAidDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly DemoDataOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger<DemoCaseImporter> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<DemoCaseImportReport> ImportAsync(CancellationToken cancellationToken)
    {
        var csv = await LoadCsvAsync(cancellationToken).ConfigureAwait(false);
        var (rows, errors) = DemoDataCsvParser.Parse(csv);
        foreach (var error in errors)
        {
            _logger.LogWarning("Skipping demo dataset entry: {Error}", error);
        }

        if (rows.Count == 0)
        {
            return new DemoCaseImportReport(0, 0, errors.Count, false);
        }

        if (await _dbContext.ManagedCases
                .AsNoTracking()
                .AnyAsync(item => item.OrganizationId == DemoOrganizationId, cancellationToken)
                .ConfigureAwait(false))
        {
            return new DemoCaseImportReport(rows.Count, 0, errors.Count, true);
        }

        var now = _timeProvider.GetUtcNow();
        await SeedIdentityAsync(now, cancellationToken).ConfigureAwait(false);

        var inserted = 0;
        foreach (var group in rows
                     .Take(_options.MaxCases)
                     .GroupBy(row => new { row.CountryCode, row.Sector })
                     .OrderBy(group => group.Key.CountryCode, StringComparer.Ordinal)
                     .ThenBy(group => group.Key.Sector))
        {
            await SeedGrantGroupAsync(group.Key.CountryCode, group.Key.Sector, now, cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var row in rows.Take(_options.MaxCases))
        {
            var createdAt = now.AddDays(-row.CreatedDaysAgo);
            var caseId = Guid.NewGuid();
            _dbContext.ManagedCases.Add(new ManagedCaseEntity
            {
                CaseId = caseId,
                OrganizationId = DemoOrganizationId,
                WorkspaceId = DemoWorkspaceId,
                CountryCode = row.CountryCode,
                Kind = row.Kind,
                Sector = row.Sector,
                SensitivityCode = row.SensitivityCode,
                AccountableOwnerActorId = DemoActorId,
                ResourceVersion = 1,
                AggregateVersion = 2,
                ReportRevision = 1,
                Workflow = row.Workflow,
                WorkflowVersion = 0,
                Classification = ClinicalClassification.Unclassified,
                ClassificationVersion = 0,
                Laboratory = LaboratoryState.NotRecorded,
                LaboratoryVersion = 0,
                OutcomeState = ReportedValueState.Unknown,
                OutcomeVersion = 0,
                CreatedAtUtc = createdAt,
                UpdatedAtUtc = createdAt
            });

            if (row.RegionId is not null)
            {
                _dbContext.ManagedCaseRegions.Add(new ManagedCaseRegionEntity
                {
                    CaseId = caseId,
                    RegionId = row.RegionId
                });
            }

            inserted++;
        }

        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new DemoCaseImportReport(rows.Count, inserted, errors.Count, false);
    }

    private async Task SeedIdentityAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        _dbContext.Actors.Add(new ActorEntity
        {
            ActorId = DemoActorId,
            Status = PersistentActorStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        _dbContext.ActorAccessStates.Add(new ActorAccessStateEntity
        {
            ActorId = DemoActorId,
            AccessEpoch = DemoAccessEpoch,
            Version = 1,
            UpdatedAtUtc = now
        });
        _dbContext.Organizations.Add(new OrganizationEntity
        {
            OrganizationId = DemoOrganizationId,
            Status = PersistentResourceStatus.Active,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        _dbContext.Workspaces.Add(new WorkspaceEntity
        {
            WorkspaceId = DemoWorkspaceId,
            OrganizationId = DemoOrganizationId,
            Status = PersistentResourceStatus.Active
        });
        var membershipId = Guid.NewGuid();
        _dbContext.OrganizationMemberships.Add(new OrganizationMembershipEntity
        {
            MembershipId = membershipId,
            ActorId = DemoActorId,
            OrganizationId = DemoOrganizationId,
            Status = PersistentMembershipStatus.Active,
            ValidFromUtc = now.AddHours(-1),
            ExpiresAtUtc = null,
            GrantedByActorId = DemoActorId,
            Version = 1
        });
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _dbContext.ChangeTracker.Clear();
    }

    private async Task SeedGrantGroupAsync(
        string countryCode, Sector sector, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var grantGroupId = $"grant-group-demo-{countryCode}-{sector}".ToLowerInvariant();
        if (await _dbContext.AccessGrantGroups
                .AsNoTracking()
                .AnyAsync(item => item.GrantGroupId == grantGroupId, cancellationToken)
                .ConfigureAwait(false))
        {
            return;
        }

        var membershipId = await _dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(item => item.ActorId == DemoActorId && item.OrganizationId == DemoOrganizationId)
            .Select(item => item.MembershipId)
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);
        _dbContext.AccessGrantGroups.Add(new AccessGrantGroupEntity
        {
            GrantGroupId = grantGroupId,
            MembershipId = membershipId,
            ActorId = DemoActorId,
            OrganizationId = DemoOrganizationId,
            WorkspaceId = DemoWorkspaceId,
            CountryCode = countryCode,
            Sector = sector,
            SensitivityCode = "standard",
            CanAccessJurisdictionalCases = false,
            Status = PersistentGrantStatus.Active,
            ValidFromUtc = now.AddHours(-1),
            ExpiresAtUtc = now.AddYears(1),
            GrantedByActorId = DemoActorId,
            Version = 1
        });
        _dbContext.AccessGrantOperations.Add(new AccessGrantOperationEntity
        {
            GrantGroupId = grantGroupId,
            Operation = CaseOperation.ReadCase
        });
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _dbContext.ChangeTracker.Clear();
    }

    private static async Task<string> LoadCsvAsync(CancellationToken cancellationToken)
    {
        await using var resource = ResourceAssembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("The demo dataset resource is missing.");
        using var reader = new StreamReader(resource);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }
}
