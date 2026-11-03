using System.Collections.Immutable;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Infrastructure.Persistence.Transactions;

namespace SwiftAid.Integration.Tests;

[Collection("Ellai framework PostgreSQL worker")]
public sealed class CaseAuthorityReadDeadlineTests
{
    [FrameworkPostgresFact]
    public async Task BoundedCaseReadReleasesPostgresAuthorityLockWhenCallerDoesNotReturn()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync(frameworkFixture: true);
        await using var readDb = fixture.CreateDbContext();
        await using var writeDb = fixture.CreateDbContext();
        var reader = new PostgresCaseCommandUnitOfWork(readDb);
        var writer = new PostgresCaseCommandUnitOfWork(writeDb);
        ICaseCommandLease readLease;
        using (CaseAuthorityReadDeadline.Begin(TimeSpan.FromSeconds(2)))
            readLease = await reader.BeginCaseAsync(fixture.CreateActor(), fixture.CaseId,
                ImmutableArray<string>.Empty, CaseLeaseMode.Read, default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<ICaseCommandLease> waitingWriter;
        using (CaseAuthorityReadDeadline.Begin(TimeSpan.FromMilliseconds(100)))
            waitingWriter = writer.BeginCaseAsync(fixture.CreateActor(), fixture.CaseId,
                ImmutableArray<string>.Empty, CaseLeaseMode.Write, timeout.Token).AsTask();
        await Task.Delay(250, timeout.Token);
        Assert.False(waitingWriter.IsCompleted);
        await using var acquired = await waitingWriter.WaitAsync(timeout.Token);
        Assert.True(acquired.IsActive);
        try { await readLease.DisposeAsync(); }
        catch (Exception error) when (error is Npgsql.NpgsqlException or InvalidOperationException or ObjectDisposedException) { }
    }
}
