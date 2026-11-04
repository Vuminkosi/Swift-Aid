using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Intake;
using SwiftAid.Application.Intake.Imports;
using SwiftAid.Contracts.Commands;
using SwiftAid.Contracts.Intake;
using SwiftAid.Domain.Intake;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.IntakeImports;
using Xunit.Abstractions;

namespace SwiftAid.Integration.Tests;

[Collection(IntakePostgresCollection.Name)]
public sealed class CaseImportLoadTests(ITestOutputHelper output)
{
    [ImportLoadFact]
    public async Task Supplied_15000_rows_cross_real_http_registration_and_durable_ledger()
    {
        await using var fixture = await CaseImportPostgresTests.Fixture.CreateAsync();
        await SeedLoadScopesAsync(fixture.Db);
        await using var host = await CaseImportEndpointTests.Host.StartAsync(fixture, new LoadValidator());
        host.Client.Timeout = TimeSpan.FromMinutes(2);
        using var process = Process.GetCurrentProcess();
        var cpuStart = process.TotalProcessorTime;
        var clock = Stopwatch.StartNew();
        var latency = new List<double>();
        var results = new List<object>();
        var importIds = new List<Guid>();
        long peakWorkingSet = 0;
        var acceptedNew = 0;
        var replayed = 0;
        var directory = Environment.GetEnvironmentVariable("SWIFTAID_TEST_CASE_CSV_DIRECTORY")!;
        foreach (var (file, country, workspace, expected) in new[]
        {
            ("zimbabwe_10500_cases.csv", "ZW", "workspace-1", 10500),
            ("eswatini_4500_cases.csv", "SZ", "workspace-import-sz", 4500)
        })
        {
            var id = Guid.NewGuid();
            importIds.Add(id);
            var context = new CaseImportContext(workspace, CaseImportPostgresTests.Context.Confirmations with { OccurrenceCountryCode = country });
            var fileClock = Stopwatch.StartNew();
            CaseImportSummary? final = null;
            CaseCsvBatch? firstBatch = null;
            using var reader = File.OpenText(Path.Combine(directory, file));
            await foreach (var batch in CaseCsvBatchReader.ReadAsync(reader))
            {
                firstBatch ??= batch;
                using var request = CaseImportEndpointTests.Host.Request(id, batch.StartRow, batch.IsFinal, context, batch.Csv);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic-import-token");
                var timer = Stopwatch.StartNew();
                using var response = await host.Client.SendAsync(request);
                var result = await response.Content.ReadFromJsonAsync<CaseImportBatchResult>();
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.NotNull(result);
                Assert.Null(result.ErrorCode);
                Assert.All(result.Rows, row => Assert.Contains(row.Status, new[] { "Accepted", "Replayed" }));
                acceptedNew += result.Rows.Count(row => row.Status == "Accepted");
                replayed += result.Rows.Count(row => row.Status == "Replayed");
                final = result.Summary;
                latency.Add(timer.Elapsed.TotalMilliseconds);
                process.Refresh();
                peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
            }
            Assert.NotNull(final);
            Assert.Equal("Completed", final.State);
            Assert.Equal(expected, final.Accepted);
            Assert.Equal(0, final.Rejected + final.Retryable + final.Unresolved);
            Assert.Equal(expected, await fixture.Db.Set<CaseImportRowEntity>().CountAsync(row => row.ImportId == id));
            results.Add(new { file, country, rows = expected, elapsedSeconds = fileClock.Elapsed.TotalSeconds });
            // Replay a real first batch after completion: same receipt, unchanged unique ledger count.
            using var retry = CaseImportEndpointTests.Host.Request(id, 1, false, context, firstBatch!.Csv);
            retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic-import-token");
            using var replay = await host.Client.SendAsync(retry);
            var replayResult = await replay.Content.ReadFromJsonAsync<CaseImportBatchResult>();
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            Assert.All(replayResult!.Rows, row => Assert.Equal("Replayed", row.Status));
            Assert.Equal(expected, replayResult.Summary!.Accepted);
        }
        var receipts = await (from row in fixture.Db.Set<CaseImportRowEntity>()
            join receipt in fixture.Db.CommandReceipts on row.CommandId equals receipt.CommandId
            where importIds.Contains(row.ImportId) && receipt.ActorId == CaseImportPostgresTests.Actor.ActorId
            select receipt.ReceiptId).Distinct().CountAsync();
        var events = await (from row in fixture.Db.Set<CaseImportRowEntity>()
            join entry in fixture.Db.CaseEvents on row.CommandId equals entry.CommandId
            where importIds.Contains(row.ImportId) select entry.EventId).Distinct().CountAsync();
        var notices = await (from row in fixture.Db.Set<CaseImportRowEntity>()
            join entry in fixture.Db.CaseEvents on row.CommandId equals entry.CommandId
            join notice in fixture.Db.DomainOutbox on entry.EventId equals notice.EventId
            where importIds.Contains(row.ImportId) select notice.OutboxId).Distinct().CountAsync();
        Assert.Equal(15000, receipts);
        Assert.Equal(30000, events);
        Assert.Equal(30000, notices);
        latency.Sort();
        double Percentile(double percentile) => latency[(int)Math.Ceiling(latency.Count * percentile) - 1];
        var report = new
        {
            environment = "Windows, ASP.NET TestServer, real PostgreSQL 17 Docker loopback; fixture identity/policy",
            rows = 15000, acceptedNew, replayed, receipts, events, outboxNotices = notices,
            elapsedSeconds = clock.Elapsed.TotalSeconds, rowsPerSecond = 15000 / clock.Elapsed.TotalSeconds,
            batches = latency.Count, batchRows = CaseCsvBatchReader.MaximumBatchRows,
            batchP50Ms = Percentile(.5), batchP95Ms = Percentile(.95), batchP99Ms = Percentile(.99),
            peakSampledTestHostWorkingSetBytes = peakWorkingSet, testHostCpuSeconds = (process.TotalProcessorTime - cpuStart).TotalSeconds,
            files = results, brokerConsumerAndIntelligence = "NOT VERIFIED; outbox rows persisted, no load dispatch"
        };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        output.WriteLine(json);
        var resultPath = Environment.GetEnvironmentVariable("SWIFTAID_IMPORT_LOAD_RESULT");
        if (!string.IsNullOrEmpty(resultPath)) await File.WriteAllTextAsync(resultPath, json);
    }

    private static async Task SeedLoadScopesAsync(SwiftAidDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var zw = await db.AccessGrantGroups.AsNoTracking().FirstAsync(grant => grant.ActorId == "officer-1" && grant.Status == PersistentGrantStatus.Active);
        AddGrant("workspace-1", "org-zw-1", zw.MembershipId, "ZW", Sector.AnimalHealth);
        if (!await db.Organizations.AnyAsync(row => row.OrganizationId == "org-import-sz"))
        {
            db.Organizations.Add(new OrganizationEntity { OrganizationId = "org-import-sz", Status = PersistentResourceStatus.Active, CreatedAtUtc = now, UpdatedAtUtc = now });
            db.Workspaces.Add(new WorkspaceEntity { WorkspaceId = "workspace-import-sz", OrganizationId = "org-import-sz", Status = PersistentResourceStatus.Active });
        }
        var membership = Guid.NewGuid();
        db.OrganizationMemberships.Add(new OrganizationMembershipEntity { MembershipId = membership, ActorId = "officer-1", OrganizationId = "org-import-sz",
            Status = PersistentMembershipStatus.Active, ValidFromUtc = now.AddHours(-1), GrantedByActorId = "granter-1", Version = 1 });
        AddGrant("workspace-import-sz", "org-import-sz", membership, "SZ", Sector.HumanHealth);
        AddGrant("workspace-import-sz", "org-import-sz", membership, "SZ", Sector.AnimalHealth);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        void AddGrant(string workspace, string organization, Guid member, string country, Sector sector)
        {
            var id = "load-fixture-" + Guid.NewGuid().ToString("N");
            db.AccessGrantGroups.Add(new AccessGrantGroupEntity { GrantGroupId = id, MembershipId = member, ActorId = "officer-1", OrganizationId = organization,
                WorkspaceId = workspace, CountryCode = country, Sector = sector, SensitivityCode = "standard", Status = PersistentGrantStatus.Active,
                ValidFromUtc = now.AddHours(-1), ExpiresAtUtc = now.AddHours(1), GrantedByActorId = "granter-1", Version = 1 });
            db.AccessGrantOperations.Add(new AccessGrantOperationEntity { GrantGroupId = id, Operation = CaseOperation.ReadCase });
        }
    }

    private sealed class LoadValidator : IIntakeSubmissionValidator
    {
        public Task<bool> ValidateAsync(IntakeSubmission submission, IntakeLimits limits, CancellationToken ct)
        { IntakeSubmissionRules.Validate(submission, limits); return Task.FromResult(submission.Location.CountryCode is "ZW" or "SZ"); }
    }
}

public sealed class ImportLoadFactAttribute : FactAttribute
{
    public ImportLoadFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SWIFTAID_RUN_IMPORT_LOAD") != "1" ||
            Environment.GetEnvironmentVariable("SWIFTAID_RUN_INTAKE_POSTGRES_FIXTURES") != "1" ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SWIFTAID_TEST_CASE_CSV_DIRECTORY")))
            Skip = "Requires explicit synthetic 15000-row load opt-in and disposable PostgreSQL.";
    }
}
