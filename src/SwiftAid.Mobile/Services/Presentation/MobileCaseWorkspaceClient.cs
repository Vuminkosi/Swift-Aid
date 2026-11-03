using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Mobile.Services.Presentation;

/// <summary>Projects only fields supported by the existing authorized Mobile case queue.</summary>
public sealed class MobileCaseWorkspaceClient(ICaseQueueClient caseQueueClient) : ICaseWorkspaceClient
{
    private const int QueuePageSize = 100;
    private const string Capability = "case-workspace";
    private readonly ICaseQueueClient _caseQueueClient = caseQueueClient ?? throw new ArgumentNullException(nameof(caseQueueClient));

    public async Task<UiCapabilityResult<Page<CaseWorkspaceRow>>> ListAsync(
        CaseWorkspaceQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        if (!query.IsValid)
            return Invalid<Page<CaseWorkspaceRow>>("Review the case filters and try again.");

        if (HasUnsupportedFilter(query))
            return Unavailable<Page<CaseWorkspaceRow>>("One or more selected filters are not available for this case list.");

        try
        {
            var result = await GetCompleteQueueAsync(cancellationToken).ConfigureAwait(false);
            if (result.Access == CaseQueueAccess.Denied)
                return Denied<Page<CaseWorkspaceRow>>();
            if (result.Access != CaseQueueAccess.Allowed)
                return Unavailable<Page<CaseWorkspaceRow>>("The authorized case list is unavailable right now.");

            var matching = result.Items
                .Where(item => Matches(item, query))
                .OrderBy(item => item.UpdatedAtUtc,
                    query.Sort == CaseWorkspaceSort.UpdatedNewestFirst
                        ? Comparer<DateTimeOffset>.Create((left, right) => right.CompareTo(left))
                        : Comparer<DateTimeOffset>.Default)
                .ThenBy(item => item.CaseId)
                .ToArray();
            var rows = matching
                .Skip((int)Math.Min((long)(query.Page.PageNumber - 1) * query.Page.PageSize, matching.Length))
                .Take(query.Page.PageSize)
                .Select(ToRow)
                .ToArray();
            var page = new Page<CaseWorkspaceRow>(
                rows,
                query.Page.PageNumber,
                query.Page.PageSize,
                matching.Length,
                (long)query.Page.PageNumber * query.Page.PageSize < matching.Length);
            return Success(page);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Unavailable<Page<CaseWorkspaceRow>>("The authorized case list is unavailable right now.");
        }
    }

    public async Task<UiCapabilityResult<CaseWorkspaceFilterOptions>> GetFilterOptionsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var result = await GetCompleteQueueAsync(cancellationToken).ConfigureAwait(false);
            if (result.Access == CaseQueueAccess.Denied)
                return Denied<CaseWorkspaceFilterOptions>();
            if (result.Access != CaseQueueAccess.Allowed)
                return Unavailable<CaseWorkspaceFilterOptions>("Complete filter choices are not available for this case list.");

            var options = new CaseWorkspaceFilterOptions(
                Sectors: result.Items.Select(item => ToSector(item.Sector)).Distinct()
                    .Select(sector => new CaseWorkspaceOption(sector.ToString(), sector.ToString())).ToArray(),
                Workflows: ToOptions(result.Items.Select(item => item.Workflow)),
                Countries: ToOptions(result.Items.Select(item => item.CountryCode)),
                Regions: [],
                Assignees: [],
                ReportedCategories: ToOptions(result.Items.Select(item => item.Classification).OfType<string>()),
                Capabilities: new CaseWorkspaceFilterCapabilities(
                    Search: true,
                    Sector: true,
                    Workflow: true,
                    Country: true,
                    Region: false,
                    Assignee: false,
                    ReportedCategory: true,
                    UpdatedDate: true));
            return Success(options);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Unavailable<CaseWorkspaceFilterOptions>("Filter choices are not available for this case list.");
        }
    }

    private static bool HasUnsupportedFilter(CaseWorkspaceQuery query) =>
        !string.IsNullOrWhiteSpace(query.RegionKey) ||
        !string.IsNullOrWhiteSpace(query.AssigneeKey);

    private async Task<CaseQueueResult> GetCompleteQueueAsync(CancellationToken cancellationToken)
    {
        var items = new List<CaseQueueItem>();
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        while (true)
        {
            var page = await _caseQueueClient.GetMineAsync(QueuePageSize, cursor, cancellationToken).ConfigureAwait(false);
            if (page.Access != CaseQueueAccess.Allowed)
                return new CaseQueueResult(page.Access, []);
            items.AddRange(page.Items);
            cursor = page.NextCursor;
            if (cursor is null) return new CaseQueueResult(CaseQueueAccess.Allowed, items);
            if (!seenCursors.Add(cursor)) return new CaseQueueResult(CaseQueueAccess.Unavailable, []);
        }
    }

    private static bool Matches(CaseQueueItem item, CaseWorkspaceQuery query)
    {
        if (query.Sector is { } sector && ToSector(item.Sector) != sector)
            return false;
        if (query.ReportedCategoryKey is { } category &&
            !string.Equals(item.Classification, category, StringComparison.OrdinalIgnoreCase))
            return false;
        if (query.Workflow is { } workflow &&
            !string.Equals(item.Workflow, workflow, StringComparison.OrdinalIgnoreCase))
            return false;
        if (query.CountryCode is { } country &&
            !string.Equals(item.CountryCode, country, StringComparison.OrdinalIgnoreCase))
            return false;
        if (query.UpdatedFromUtc is { } from && item.UpdatedAtUtc < from)
            return false;
        if (query.UpdatedToUtc is { } to && item.UpdatedAtUtc > to)
            return false;
        if (string.IsNullOrWhiteSpace(query.Search))
            return true;
        return CaseReference(item.CaseId).Contains(query.Search.Trim(), StringComparison.OrdinalIgnoreCase) ||
            item.Workflow.Contains(query.Search.Trim(), StringComparison.OrdinalIgnoreCase) ||
            item.CountryCode.Contains(query.Search.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static CaseWorkspaceRow ToRow(CaseQueueItem item) => new(
        item.CaseId,
        PublicReference: CaseReference(item.CaseId),
        item.Workflow,
        CountryLabel: item.CountryCode,
        ToSector(item.Sector),
        RegionLabel: null,
        AssigneeLabel: "Assigned to you",
        ReportedCategoryLabel: item.Classification,
        item.UpdatedAtUtc,
        item.AggregateVersion,
        [CaseWorkspaceAction.Open]);

    private static string CaseReference(Guid caseId) => "Case " + caseId.ToString("N")[..8];

    private static CaseWorkspaceSector ToSector(string? sector) => sector switch
    {
        "HumanHealth" => CaseWorkspaceSector.Human,
        "AnimalHealth" => CaseWorkspaceSector.Animal,
        _ => CaseWorkspaceSector.Unknown
    };

    private static IReadOnlyList<CaseWorkspaceOption> ToOptions(IEnumerable<string> values) => values
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
        .Select(value => new CaseWorkspaceOption(value, value))
        .ToArray();

    private static UiCapabilityResult<T> Success<T>(T value) => new(
        UiCapabilityResultCode.Success,
        value,
        Metadata(isAvailable: true));

    private static UiCapabilityResult<T> Invalid<T>(string message) => new(
        UiCapabilityResultCode.InvalidRequest,
        default,
        Metadata(isAvailable: true),
        message);

    private static UiCapabilityResult<T> Denied<T>() => new(
        UiCapabilityResultCode.Denied,
        default,
        Metadata(isAvailable: true),
        "You do not have permission to view this case list.");

    private static UiCapabilityResult<T> Unavailable<T>(string message) => new(
        UiCapabilityResultCode.Unavailable,
        default,
        Metadata(isAvailable: false),
        message);

    private static UiCapabilityMetadata Metadata(bool isAvailable) => new(
        Capability,
        UiCapabilityResults.ContractVersion,
        UiCapabilityProvenance.LiveService,
        DateTimeOffset.UtcNow,
        isAvailable);
}
