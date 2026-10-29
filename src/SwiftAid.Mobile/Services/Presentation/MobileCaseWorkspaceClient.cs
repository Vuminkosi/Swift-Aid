using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Mobile.Services.Presentation;

/// <summary>Projects only fields supported by the existing authorized Mobile case queue.</summary>
public sealed class MobileCaseWorkspaceClient(ICaseQueueClient caseQueueClient) : ICaseWorkspaceClient
{
    private const int MaximumQueueRows = 500;
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
            var result = await _caseQueueClient.GetMineAsync(MaximumQueueRows, cancellationToken).ConfigureAwait(false);
            if (result.Access == CaseQueueAccess.Denied)
                return Denied<Page<CaseWorkspaceRow>>();
            if (result.Access != CaseQueueAccess.Allowed)
                return Unavailable<Page<CaseWorkspaceRow>>("The authorized case list is unavailable right now.");
            if (result.Items.Count >= MaximumQueueRows)
                return Unavailable<Page<CaseWorkspaceRow>>("The case list has reached its current limit; a complete filtered result cannot be confirmed.");

            var matching = result.Items
                .Where(item => Matches(item, query))
                .OrderBy(item => item.UpdatedAtUtc,
                    query.Sort == CaseWorkspaceSort.UpdatedNewestFirst
                        ? Comparer<DateTimeOffset>.Create((left, right) => right.CompareTo(left))
                        : Comparer<DateTimeOffset>.Default)
                .ToArray();
            var rows = matching
                .Skip((query.Page.PageNumber - 1) * query.Page.PageSize)
                .Take(query.Page.PageSize)
                .Select(ToRow)
                .ToArray();
            var page = new Page<CaseWorkspaceRow>(
                rows,
                query.Page.PageNumber,
                query.Page.PageSize,
                matching.Length,
                query.Page.PageNumber * query.Page.PageSize < matching.Length);
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
            var result = await _caseQueueClient.GetMineAsync(MaximumQueueRows, cancellationToken).ConfigureAwait(false);
            if (result.Access == CaseQueueAccess.Denied)
                return Denied<CaseWorkspaceFilterOptions>();
            if (result.Access != CaseQueueAccess.Allowed || result.Items.Count >= MaximumQueueRows)
                return Unavailable<CaseWorkspaceFilterOptions>("Complete filter choices are not available for this case list.");

            var options = new CaseWorkspaceFilterOptions(
                Sectors: [],
                Workflows: ToOptions(result.Items.Select(item => item.Workflow)),
                Countries: ToOptions(result.Items.Select(item => item.CountryCode)),
                Regions: [],
                Assignees: [],
                ReportedCategories: [],
                Capabilities: new CaseWorkspaceFilterCapabilities(
                    Search: true,
                    Sector: false,
                    Workflow: true,
                    Country: true,
                    Region: false,
                    Assignee: false,
                    ReportedCategory: false,
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
        query.Sector is not null and not CaseWorkspaceSector.Unknown ||
        !string.IsNullOrWhiteSpace(query.RegionKey) ||
        !string.IsNullOrWhiteSpace(query.AssigneeKey) ||
        !string.IsNullOrWhiteSpace(query.ReportedCategoryKey);

    private static bool Matches(CaseQueueItem item, CaseWorkspaceQuery query)
    {
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
        return item.Workflow.Contains(query.Search.Trim(), StringComparison.OrdinalIgnoreCase) ||
            item.CountryCode.Contains(query.Search.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static CaseWorkspaceRow ToRow(CaseQueueItem item) => new(
        item.CaseId,
        PublicReference: null,
        item.Workflow,
        CountryLabel: item.CountryCode,
        CaseWorkspaceSector.Unknown,
        RegionLabel: null,
        AssigneeLabel: null,
        ReportedCategoryLabel: null,
        item.UpdatedAtUtc,
        item.AggregateVersion,
        [CaseWorkspaceAction.Open]);

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
