using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Application.Intake;
using SwiftAid.Contracts.Intake;
using SwiftAid.Contracts.Surveillance;
using SwiftAid.Infrastructure.Authorization;
using SwiftAid.Infrastructure.Intake;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Entities.Intake;
using SwiftAid.Infrastructure.Persistence.Repositories.Cases;
using SwiftAid.Infrastructure.Persistence.Transactions;

namespace SwiftAid.Integration.Tests;

/// <summary>
/// Opt-in PostgreSQL tests for the officer-scope surveillance aggregate.
/// Uses the same disposable loopback database gate as the other intake fixtures.
/// </summary>
[Trait("Category", "MapAggregate")]
[Collection(IntakePostgresCollection.Name)]
public sealed class MapAggregatePostgresTests
{
    [IntakePostgresFact]
    public async Task OwnedCases_AggregatedByCountryAndRegion_OldRecordsExcluded()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        await CaseRegistrationBackendTests.SeedRegistrationGrantAsync(db);
        var recentIds = new List<Guid>();
        for (var index = 0; index < 5; index++)
        {
            var caseId = await IntakeDecisionStoreTests.SeedManagedCaseAsync(db);
            await AddRegionAsync(db, caseId, "ZW-HA");
            recentIds.Add(caseId);
        }

        var oldCaseId = await IntakeDecisionStoreTests.SeedManagedCaseAsync(db);
        var oldEntity = await db.ManagedCases.SingleAsync(item => item.CaseId == oldCaseId);
        oldEntity.CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-200);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await CreateQuery(db).GetAsync(
            CreateOfficer("officer-1"), null, TimeSpan.FromDays(30), CancellationToken.None);

        Assert.Equal(SurveillanceAggregateAccess.Allowed, result.Access);
        Assert.False(result.Truncated);
        var country = Assert.Single(result.Areas, area =>
            area.Precision == SurveillanceAggregatePrecision.Country);
        Assert.Equal("ZW", country.AreaCode);
        Assert.False(country.IsSuppressed);
        Assert.Equal(5, country.HumanCount);
        Assert.Equal(0, country.AnimalCount);
        Assert.Equal(5, country.Denominator);
        var region = Assert.Single(result.Areas, area =>
            area.Precision == SurveillanceAggregatePrecision.Region);
        Assert.Equal("ZW-HA", region.AreaCode);
        Assert.Equal(5, region.HumanCount);
    }

    [IntakePostgresFact]
    public async Task FewOwnedCases_CountryRowIsSuppressed()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        await CaseRegistrationBackendTests.SeedRegistrationGrantAsync(db);
        await IntakeDecisionStoreTests.SeedManagedCaseAsync(db);

        var result = await CreateQuery(db).GetAsync(
            CreateOfficer("officer-1"), null, TimeSpan.FromDays(30), CancellationToken.None);

        Assert.Equal(SurveillanceAggregateAccess.Allowed, result.Access);
        var country = Assert.Single(result.Areas);
        Assert.True(country.IsSuppressed);
        Assert.Null(country.HumanCount);
        Assert.True(country.IsValid);
    }

    private static async Task AddRegionAsync(SwiftAidDbContext db, Guid caseId, string regionId)
    {
        db.ManagedCaseRegions.Add(new ManagedCaseRegionEntity { CaseId = caseId, RegionId = regionId });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static SurveillanceMapAggregateQuery CreateQuery(SwiftAidDbContext db)
    {
        // Policy stores resolve the ambient case lease through the accessor, so all
        // three collaborators must share one unit of work.
        var unitOfWork = new PostgresCaseCommandUnitOfWork(db);
        return new SurveillanceMapAggregateQuery(
            new PostgresMapAggregateStore(db),
            new CaseAccessPolicy(
                new PostgresCurrentAccessGrantStore(db, unitOfWork),
                new PostgresCaseAccessDescriptorStore(db, unitOfWork)),
            unitOfWork,
            new StubMetadata(),
            TimeProvider.System);
    }

    private static ActorContext CreateOfficer(string actorId) => ActorContext.FromValidatedPrincipal(
        new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, actorId),
            new Claim(ActorContext.AccessEpochClaimType, "7")
        ], "synthetic-test")));

    private sealed class StubMetadata : IIntakePublicMetadataProvider
    {
        public PublicIntakeMetadata GetMetadata() => new("test", "test", "test", "test", "test");

        public Task<PublicIntakeCountryCatalogue> GetCountriesAsync(CancellationToken ct) =>
            Task.FromResult(new PublicIntakeCountryCatalogue(
                "test",
                "test",
                [new PublicIntakeCountry("ZW", "Zimbabwe")]));
    }
}
