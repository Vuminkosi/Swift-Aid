using Microsoft.EntityFrameworkCore;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Entities.Intake;

namespace SwiftAid.Integration.Tests;

public sealed class IngestionOutboxModelTests
{
    [Theory]
    [InlineData(typeof(DomainOutboxEntity))]
    [InlineData(typeof(IntakeOutboxEntity))]
    public void Accepted_ingestion_outboxes_can_fence_retries_and_retain_terminal_failures(Type entityType)
    {
        using var db = new SwiftAidDbContextFactory().CreateDbContext([]);
        var entity = db.Model.FindEntityType(entityType)!;
        Assert.NotNull(entity.FindProperty("LeaseId"));
        Assert.NotNull(entity.FindProperty("LeaseUntilUtc"));
        Assert.NotNull(entity.FindProperty("AvailableAtUtc"));
        Assert.NotNull(entity.FindProperty("ExhaustedAtUtc"));
        Assert.NotNull(entity.FindProperty("LastFailureCode"));
    }
}
