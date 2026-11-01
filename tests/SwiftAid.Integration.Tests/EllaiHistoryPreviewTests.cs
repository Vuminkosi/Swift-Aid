using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Infrastructure.Persistence.Ellai;

namespace SwiftAid.Integration.Tests;

[Collection(IntakePostgresCollection.Name)]
public sealed class EllaiHistoryPreviewTests
{
    [IntakePostgresFact]
    public async Task HistoryPreviewsAreBoundedOwnerScopedAndOrderedWithEmptySessionFallback()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var owner = "history-preview-" + Guid.NewGuid().ToString("N");
        var store = new PostgresEllaiSessionStore(db);
        for (var index = 0; index < 3; index++)
        {
            var id = owner + "-" + index;
            await store.CreateAsync(new(id, EllaiMode.Ask, "Completed", 1, 1, 1, 1, 0, 12,
                index == 2 ? owner + "-other" : owner, "test", null, null, DateTimeOffset.UtcNow.AddMinutes(index)), default);
            if (index == 0) continue;
            db.Set<EllaiUtteranceEntity>().AddRange(
                new() { SessionId = id, ClientUtteranceId = "first", Sequence = 1, AcceptedAtUtc = DateTimeOffset.UtcNow,
                    UserText = index == 2 ? "Other owner private question" : new string('x', 200) },
                new() { SessionId = id, ClientUtteranceId = "next", Sequence = 2, AcceptedAtUtc = DateTimeOffset.UtcNow, UserText = "Later question" });
            await db.SaveChangesAsync();
        }
        db.ChangeTracker.Clear();
        var first = Assert.Single(await store.ListOwnedAsync(owner, 0, 1, default));
        Assert.Equal(new string('x', 160), first.HistoryPreview);
        Assert.Equal(2, first.HistoryTurnCount);
        var empty = Assert.Single(await store.ListOwnedAsync(owner, 1, 1, default));
        Assert.Null(empty.HistoryPreview);
        Assert.Equal(0, empty.HistoryTurnCount);
        Assert.Empty(await store.ListOwnedAsync(owner + "-unknown", 0, 20, default));
        Assert.Equal("Other owner private question", Assert.Single(
            await store.ListOwnedAsync(owner + "-other", 0, 20, default)).HistoryPreview);
        // Rollback removes only this test's synthetic sessions and messages.
        await transaction.RollbackAsync();
    }
}
