using SwiftAid.Client.Intake;
using SwiftAid.Client.Presentation;
using SwiftAid.Client.Presentation.PublicWorkflows;
using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Intake;

namespace SwiftAid.Gateway.Cases;

/// <summary>Builds a presentation-only workspace from the authorized mine queue and public country labels.</summary>
public sealed class CaseWorkspaceGatewayClient(
    ICaseQueueClient caseQueueClient,
    IPublicIntakeMetadataClient publicMetadataClient,
    ICaseReportDetailsClient? reportClient = null) : IIncrementalCaseWorkspaceClient
{
    private const int QueuePageSize = 100;
    private const string Capability = "case-workspace";

    private readonly ICaseQueueClient _caseQueueClient = caseQueueClient ?? throw new ArgumentNullException(nameof(caseQueueClient));
    private readonly IPublicIntakeMetadataClient _publicMetadataClient = publicMetadataClient ?? throw new ArgumentNullException(nameof(publicMetadataClient));

    public Task<UiCapabilityResult<Page<CaseWorkspaceRow>>> ListAsync(
        CaseWorkspaceQuery query,
        CancellationToken cancellationToken = default) => ListCoreAsync(query, null, cancellationToken);

    public Task<UiCapabilityResult<Page<CaseWorkspaceRow>>> ListIncrementallyAsync(
        CaseWorkspaceQuery query, Func<Page<CaseWorkspaceRow>, Task> onProgress,
        CancellationToken cancellationToken = default) => ListCoreAsync(query, onProgress, cancellationToken);

    private async Task<UiCapabilityResult<Page<CaseWorkspaceRow>>> ListCoreAsync(
        CaseWorkspaceQuery query, Func<Page<CaseWorkspaceRow>, Task>? onProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!query.IsValid)
        {
            return Invalid<Page<CaseWorkspaceRow>>("Review the case filters and try again.");
        }

        if (HasUnsupportedFilter(query))
            return Unavailable<Page<CaseWorkspaceRow>>("One or more selected filters are not available for this case list.");

        var countries = await _publicMetadataClient.GetCountriesAsync(cancellationToken).ConfigureAwait(false);
        var labels = countries.Succeeded && countries.Value is not null
            ? countries.Value.Countries.ToDictionary(country => country.Code, country => country.Label, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var queue = await GetCompleteQueueAsync(cancellationToken, onProgress is null ? null :
            items => onProgress(BuildPage(items, labels, query))).ConfigureAwait(false);
        if (queue.Access == CaseQueueAccess.Denied)
        {
            return Denied<Page<CaseWorkspaceRow>>();
        }

        if (queue.Access != CaseQueueAccess.Allowed)
        {
            return Unavailable<Page<CaseWorkspaceRow>>("The case list is not available right now.");
        }

        var page = BuildPage(queue.Items, labels, query);
        var pageItems = page.Items.ToArray();
        if (reportClient is not null)
        {
            for (var index = 0; index < pageItems.Length; index++)
            {
                var result = await reportClient.GetAsync(pageItems[index].CaseId, cancellationToken);
                if (result.Code == UiCapabilityResultCode.Denied) return Denied<Page<CaseWorkspaceRow>>();
                if (result.Succeeded && result.Value is { } report && report.CaseId == pageItems[index].CaseId)
                    pageItems[index] = pageItems[index] with
                    {
                        RegionLabel = report.Location is { } place ? CaseReportDisplay.LocationLabel(place) : pageItems[index].RegionLabel,
                        LocationHref = report.Location is { } target ? CaseReportDisplay.MapHref(target) : null,
                        ObservationPreview = CaseReportDisplay.ObservationPreview(report)
                    };
            }
        }
        return Success(page with { Items = pageItems });
    }

    private static Page<CaseWorkspaceRow> BuildPage(IReadOnlyList<CaseQueueItem> items,
        IReadOnlyDictionary<string, string> labels, CaseWorkspaceQuery query)
    {
        var matching = items
            .Select(item => (Item: item, Row: ToRow(item, labels)))
            .Where(pair => Matches(pair.Item, pair.Row, query))
            .OrderBy(pair => pair.Row.UpdatedAtUtc, query.Sort == CaseWorkspaceSort.UpdatedNewestFirst
                ? Comparer<DateTimeOffset>.Create((left, right) => right.CompareTo(left))
                : Comparer<DateTimeOffset>.Default)
            .ThenBy(pair => pair.Row.CaseId)
            .ToArray();
        var pageItems = matching
            .Skip((int)Math.Min((long)(query.Page.PageNumber - 1) * query.Page.PageSize, matching.Length))
            .Take(query.Page.PageSize)
            .Select(pair => pair.Row)
            .ToArray();
        return new Page<CaseWorkspaceRow>(
            pageItems,
            query.Page.PageNumber,
            query.Page.PageSize,
            matching.Length,
            (long)query.Page.PageNumber * query.Page.PageSize < matching.Length);
    }

    public async Task<UiCapabilityResult<CaseWorkspaceFilterOptions>> GetFilterOptionsAsync(
        CancellationToken cancellationToken = default)
    {
        var queue = await GetCompleteQueueAsync(cancellationToken).ConfigureAwait(false);
        if (queue.Access == CaseQueueAccess.Denied)
        {
            return Denied<CaseWorkspaceFilterOptions>();
        }

        if (queue.Access != CaseQueueAccess.Allowed)
        {
            return Unavailable<CaseWorkspaceFilterOptions>("Case filters are not available right now.");
        }

        var countries = await _publicMetadataClient.GetCountriesAsync(cancellationToken).ConfigureAwait(false);
        var countryOptions = countries.Succeeded && countries.Value is not null
            ? countries.Value.Countries
                .Select(country => new CaseWorkspaceOption(country.Code, country.Label))
                .OrderBy(option => option.Label, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : Array.Empty<CaseWorkspaceOption>();
        var workflows = queue.Items
            .Select(item => new CaseWorkspaceOption(item.Workflow, WorkflowLabel(item.Workflow)))
            .DistinctBy(option => option.Key, StringComparer.OrdinalIgnoreCase)
            .OrderBy(option => option.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var filterOptions = new CaseWorkspaceFilterOptions(
            queue.Items.Select(item => ToSector(item.Sector)).Distinct()
                .Select(sector => new CaseWorkspaceOption(sector.ToString(), sector.ToString())).ToArray(),
            workflows,
            countryOptions,
            [],
            [],
            queue.Items.Select(item => item.Classification).OfType<string>()
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .Select(value => new CaseWorkspaceOption(value, value)).ToArray(),
            new CaseWorkspaceFilterCapabilities(
                Search: true,
                Sector: true,
                Workflow: true,
                Country: countries.Succeeded && countries.Value is not null,
                Region: false,
                Assignee: false,
                ReportedCategory: true,
                UpdatedDate: true));

        return Success(filterOptions, "Some filters are not available for this case list.");
    }

    private async Task<CaseQueueResult> GetCompleteQueueAsync(CancellationToken cancellationToken,
        Func<IReadOnlyList<CaseQueueItem>, Task>? onProgress = null)
    {
        var items = new List<CaseQueueItem>();
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        while (true)
        {
            using var pageTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            pageTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            var page = await _caseQueueClient.GetMineAsync(QueuePageSize, cursor, pageTimeout.Token).ConfigureAwait(false);
            if (page.Access != CaseQueueAccess.Allowed)
                return new CaseQueueResult(page.Access, []);
            items.AddRange(page.Items);
            if (onProgress is not null) await onProgress(items).ConfigureAwait(false);
            cursor = page.NextCursor;
            if (cursor is null) return new CaseQueueResult(CaseQueueAccess.Allowed, items);
            if (!seenCursors.Add(cursor)) return new CaseQueueResult(CaseQueueAccess.Unavailable, []);
        }
    }

    private static CaseWorkspaceRow ToRow(CaseQueueItem item, IReadOnlyDictionary<string, string> countryLabels) => new(
        item.CaseId,
        PublicReference: "Case " + item.CaseId.ToString("N")[..8],
        item.Workflow,
        countryLabels.GetValueOrDefault(item.CountryCode) ?? "Country not listed",
        ToSector(item.Sector),
        RegionLabel: null,
        AssigneeLabel: "Assigned to you",
        ReportedCategoryLabel: item.Classification,
        item.UpdatedAtUtc,
        item.AggregateVersion,
        [CaseWorkspaceAction.Open]);

    private static bool HasUnsupportedFilter(CaseWorkspaceQuery query) =>
        !string.IsNullOrWhiteSpace(query.RegionKey) ||
        !string.IsNullOrWhiteSpace(query.AssigneeKey);

    private static CaseWorkspaceSector ToSector(string? sector) => sector switch
    {
        "HumanHealth" => CaseWorkspaceSector.Human,
        "AnimalHealth" => CaseWorkspaceSector.Animal,
        _ => CaseWorkspaceSector.Unknown
    };

    private static bool Matches(CaseQueueItem item, CaseWorkspaceRow row, CaseWorkspaceQuery query) =>
        (!query.Sector.HasValue || row.Sector == query.Sector.Value) &&
        (query.ReportedCategoryKey is null || string.Equals(item.Classification, query.ReportedCategoryKey, StringComparison.OrdinalIgnoreCase)) &&
        (query.Workflow is null || string.Equals(item.Workflow, query.Workflow, StringComparison.OrdinalIgnoreCase)) &&
        (query.CountryCode is null || string.Equals(item.CountryCode, query.CountryCode, StringComparison.OrdinalIgnoreCase)) &&
        (!query.UpdatedFromUtc.HasValue || item.UpdatedAtUtc >= query.UpdatedFromUtc.Value) &&
        (!query.UpdatedToUtc.HasValue || item.UpdatedAtUtc <= query.UpdatedToUtc.Value) &&
        (string.IsNullOrWhiteSpace(query.Search) ||
         Contains(row.PublicReference, query.Search) ||
         Contains(row.CountryLabel, query.Search) ||
         Contains(WorkflowLabel(row.Workflow), query.Search));

    private static bool Contains(string? value, string search) =>
        value?.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase) == true;

    private static string WorkflowLabel(string workflow) => workflow switch
    {
        "Reported" => "Reported",
        "UnderReview" => "Under review",
        "Active" => "Active",
        "AwaitingFollowUp" => "Awaiting follow-up",
        "Resolved" => "Resolved",
        _ => "Status unavailable"
    };

    private static UiCapabilityResult<T> Success<T>(T value, string? message = null) => new(
        UiCapabilityResultCode.Success,
        value,
        Metadata(available: true),
        message);

    private static UiCapabilityResult<T> Invalid<T>(string message) => new(
        UiCapabilityResultCode.InvalidRequest,
        default,
        Metadata(available: false),
        message);

    private static UiCapabilityResult<T> Denied<T>() => new(
        UiCapabilityResultCode.Denied,
        default,
        Metadata(available: false),
        "You do not have access to this case list.");

    private static UiCapabilityResult<T> Unavailable<T>(string message) => new(
        UiCapabilityResultCode.Unavailable,
        default,
        Metadata(available: false, message),
        message);

    private static UiCapabilityMetadata Metadata(bool available, string? notice = null) => new(
        Capability,
        UiCapabilityResults.ContractVersion,
        UiCapabilityProvenance.LiveService,
        DateTimeOffset.UtcNow,
        available,
        notice);
}
