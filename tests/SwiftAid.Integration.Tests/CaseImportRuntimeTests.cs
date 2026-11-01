using Microsoft.EntityFrameworkCore;
using Npgsql;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Integration.Tests;

/// <summary>Actual non-owner database login; requires the documented isolated grants profile.</summary>
[Collection(IntakePostgresCollection.Name)]
public sealed class CaseImportRuntimeTests
{
    [ImportRuntimeFact]
    public async Task Restricted_runtime_registers_and_replays_without_authority_or_source_mutation_rights()
    {
        await using var fixture = await CaseImportPostgresTests.Fixture.CreateAsync();
        var shared = await SharedWorkspaceImportTests.AddOrganizationAsync(fixture.Db, CaseImportPostgresTests.Actor.ActorId);
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SWIFTAID_TEST_IMPORT_RUNTIME_CONNECTION"));
        var owner = new NpgsqlConnectionStringBuilder(fixture.Db.Database.GetConnectionString());
        Assert.Equal(owner.Host, connection.Host);
        Assert.Equal(owner.Port, connection.Port);
        Assert.Equal(owner.Database, connection.Database);
        Assert.NotEqual(owner.Username, connection.Username);
        await using var db = new SwiftAidDbContext(new DbContextOptionsBuilder<SwiftAidDbContext>().UseNpgsql(connection.ConnectionString).Options);
        var context = CaseImportPostgresTests.Context with { OrganizationId = shared.Organization };
        var importId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        var csv = CaseImportPostgresTests.Csv(commandId);
        var accepted = await fixture.CreateService(db).ProcessAsync(shared.Actor, importId, context, 1, true, new StringReader(csv), default);
        Assert.Null(accepted.ErrorCode);
        Assert.Equal("Accepted", Assert.Single(accepted.Rows).Status);
        var replay = await fixture.CreateService(db).ProcessAsync(shared.Actor, importId, context, 1, true, new StringReader(csv), default);
        Assert.Null(replay.ErrorCode);
        Assert.Equal("Replayed", Assert.Single(replay.Rows).Status);
        Assert.Equal(accepted.Rows[0].CaseId, replay.Rows[0].CaseId);
        Assert.Equal(1, await fixture.Db.CommandReceipts.CountAsync(row => row.CommandId == commandId));
        Assert.Equal(2, await fixture.Db.CaseEvents.CountAsync(row => row.CommandId == commandId));

        foreach (var sql in new[]
        {
            "UPDATE public.workspace_organizations SET status = status WHERE false",
            "UPDATE public.organization_memberships SET status = status WHERE false",
            "UPDATE swiftaid_ingestion.case_import_rows SET \"ProtectedSource\" = \"ProtectedSource\" WHERE false",
            "UPDATE swiftaid_ingestion.case_imports SET \"OrganizationId\" = \"OrganizationId\" WHERE false",
            "DELETE FROM swiftaid_ingestion.case_import_rows WHERE false",
            "UPDATE public.domain_outbox SET published_at_utc = CURRENT_TIMESTAMP WHERE false"
        })
        {
            var denied = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        }
    }
}

public sealed class ImportRuntimeFactAttribute : FactAttribute
{
    public ImportRuntimeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SWIFTAID_RUN_INTAKE_POSTGRES_FIXTURES") != "1" ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SWIFTAID_TEST_IMPORT_RUNTIME_CONNECTION")))
            Skip = "Requires the disposable PostgreSQL fixture and separately provisioned restricted import runtime.";
    }
}
