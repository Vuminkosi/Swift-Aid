using SwiftAid.Contracts.Cases;
using SwiftAid.Mobile.Services.Presentation;

namespace SwiftAid.Client.Tests;

public sealed class MobileAssignedCaseQueueReaderTests
{
    [Fact]
    public async Task ReadsEveryPageAndPassesItsCursor()
    {
        var client = new Queue(false, false);
        var result = await MobileAssignedCaseQueueReader.ReadAsync(client);
        Assert.Equal(CaseQueueAccess.Allowed, result.Access);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(new string?[] { null, "next" }, client.Cursors);
    }

    [Theory]
    [InlineData(true, false, CaseQueueAccess.Denied)]
    [InlineData(false, true, CaseQueueAccess.Unavailable)]
    public async Task DenialOrRepeatedCursorNeverReturnsPartialRows(bool denied, bool repeated, CaseQueueAccess expected)
    {
        var result = await MobileAssignedCaseQueueReader.ReadAsync(new Queue(denied, repeated));
        Assert.Equal(expected, result.Access);
        Assert.Empty(result.Items);
    }

    private sealed class Queue(bool denySecond, bool repeatCursor) : ICaseQueueClient
    {
        public List<string?> Cursors { get; } = [];
        public Task<CaseQueueResult> GetMineAsync(int? limit, CancellationToken cancellationToken = default) => GetMineAsync(limit, null, cancellationToken);
        public Task<CaseQueueResult> GetMineAsync(int? limit, string? cursor, CancellationToken cancellationToken = default)
        {
            Cursors.Add(cursor);
            if (cursor is not null && denySecond) return Task.FromResult(new CaseQueueResult(CaseQueueAccess.Denied, []));
            var item = new CaseQueueItem(Guid.NewGuid(), "Open", "SZ", "fixture", 1, DateTimeOffset.UtcNow);
            return Task.FromResult(new CaseQueueResult(CaseQueueAccess.Allowed, [item], cursor is null || repeatCursor ? "next" : null));
        }
    }
}
