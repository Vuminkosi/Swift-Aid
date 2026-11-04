using SwiftAid.Client.Presentation;

namespace SwiftAid.Mobile.Services.Presentation;

public sealed record MobileCaseWorkResult(Guid CaseId, UiCapabilityResult<WorkManagementSnapshot>? Result, bool IsOffline);

/// <summary>Reads bounded concurrent per-case snapshots and rejects inconsistent pages.</summary>
public static class MobileCaseWorkReader
{
    public static async Task<IReadOnlyList<MobileCaseWorkResult>> ReadAsync(IWorkManagementClient client,
        IEnumerable<Guid> caseIds, WorkManagementQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(caseIds);
        ArgumentNullException.ThrowIfNull(query);
        using var throttle = new SemaphoreSlim(8);
        var requests = caseIds.Distinct().Select(async caseId =>
        {
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var items = new List<WorkManagementItem>();
                long? version = null;
                int? totalCount = null;
                for (var pageNumber = 1; pageNumber <= 100_000; pageNumber++)
                {
                    var result = await client.GetAsync(caseId, query with { Page = new PageRequest(pageNumber, 100) }, cancellationToken).ConfigureAwait(false);
                    if (!result.Succeeded || result.Value is not { } snapshot)
                        return new MobileCaseWorkResult(caseId, result, false);
                    var page = snapshot.Items;
                    if (snapshot.CaseId != caseId || snapshot.Version <= 0 || page is null || page.Items is null ||
                        page.PageNumber != pageNumber || page.PageSize != 100 || page.TotalCount < 0 ||
                        version is not null && version != snapshot.Version || totalCount is not null && totalCount != page.TotalCount ||
                        page.Items.Count > 100 || page.HasMore && page.Items.Count == 0)
                        return new MobileCaseWorkResult(caseId, null, false);
                    version = snapshot.Version;
                    totalCount = page.TotalCount;
                    items.AddRange(page.Items);
                    if (items.Count > totalCount || page.HasMore != (items.Count < totalCount))
                        return new MobileCaseWorkResult(caseId, null, false);
                    if (!page.HasMore)
                    {
                        var combined = new WorkManagementSnapshot(caseId, snapshot.Version,
                            new Page<WorkManagementItem>(items, 1, 100, items.Count, false));
                        return new MobileCaseWorkResult(caseId, result with { Value = combined }, false);
                    }
                }
                return new MobileCaseWorkResult(caseId, null, false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (HttpRequestException) { return new MobileCaseWorkResult(caseId, null, true); }
            catch (Exception) { return new MobileCaseWorkResult(caseId, null, false); }
            finally { throttle.Release(); }
        });
        return await Task.WhenAll(requests).ConfigureAwait(false);
    }
}
