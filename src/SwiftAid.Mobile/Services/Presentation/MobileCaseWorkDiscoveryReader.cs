using SwiftAid.Contracts.Cases;

namespace SwiftAid.Mobile.Services.Presentation;

public static class MobileCaseWorkDiscoveryReader
{
    public static async Task<CaseWorkDiscoveryPage> ReadAsync(ICaseWorkDiscoveryClient client, CaseWorkDiscoveryKind kind,
        CaseWorkDiscoveryScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var cases = new List<Guid>();
        var seen = new HashSet<Guid>();
        Guid? cursor = null;
        do
        {
            var page = await client.GetAsync(kind, scope, 100, cursor, cancellationToken).ConfigureAwait(false);
            if (page.Access != CaseQueueAccess.Allowed) return new(page.Access, []);
            if (page.CaseIds is null || page.CaseIds.Count > 100 ||
                page.CaseIds.Any(id => id == Guid.Empty || !seen.Add(id)) ||
                page.NextCursor is { } next && (page.CaseIds.Count != 100 || next != page.CaseIds[^1]))
                return new(CaseQueueAccess.Unavailable, []);
            cases.AddRange(page.CaseIds);
            cursor = page.NextCursor;
        }
        while (cursor is not null);
        return new(CaseQueueAccess.Allowed, cases);
    }
}
