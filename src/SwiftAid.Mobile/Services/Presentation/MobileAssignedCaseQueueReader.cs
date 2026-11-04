using SwiftAid.Contracts.Cases;

namespace SwiftAid.Mobile.Services.Presentation;

/// <summary>Reads the complete authorized queue without treating a cursor as an access grant.</summary>
public static class MobileAssignedCaseQueueReader
{
    public static async Task<CaseQueueResult> ReadAsync(ICaseQueueClient client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var items = new List<CaseQueueItem>();
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var page = await client.GetMineAsync(100, cursor, cancellationToken).ConfigureAwait(false);
            if (page.Access != CaseQueueAccess.Allowed)
                return new CaseQueueResult(page.Access, []);
            items.AddRange(page.Items);
            cursor = page.NextCursor;
            if (cursor is not null && !seenCursors.Add(cursor))
                return new CaseQueueResult(CaseQueueAccess.Unavailable, []);
        }
        while (cursor is not null);

        return new CaseQueueResult(CaseQueueAccess.Allowed, items);
    }
}
