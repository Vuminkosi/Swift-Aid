using SwiftAid.Client.Intake;
using SwiftAid.Client.Presentation;
using SwiftAid.Client.Presentation.PublicWorkflows;
using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Intake;

namespace SwiftAid.Gateway.Cases;

/// <summary>Builds a presentation-only workspace from the authorized mine queue and public country labels.</summary>
public sealed class CaseWorkspaceGatewayClient(
    ICaseQueueClient caseQueueClient,
    IPublicIntakeMetadataClient publicMetadataClient) : ICaseWorkspaceClient
{
    private const int MaximumRows = 100;
    private const string Capability = "case-workspace";

    private readonly ICaseQueueClient _caseQueueClient = caseQueueClient ?? throw new ArgumentNullException(nameof(caseQueueClient));
    private readonly IPublicIntakeMetadataClient _publicMetadataClient = publicMetadataClient ?? throw new ArgumentNullException(nameof(publicMetadataClient));

    public async Task<UiCapabilityResult<Page<CaseWorkspaceRow>>> ListAsync(
        CaseWorkspaceQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!query.IsValid)
        {
            return Invalid<Page<CaseWorkspaceRow>>("Review the case filters and try again.");
        }

        var queue = await _caseQueueClient.GetMineAsync(MaximumRows, cancellationToken).ConfigureAwait(false);
        if (queue.Access == CaseQueueAccess.Denied)
        {
            return Denied<Page<CaseWorkspaceRow>>();
        }

        if (queue.Access != CaseQueueAccess.Allowed)
        {
            return Unavailable<Page<CaseWorkspaceRow>>("The case list is not available right now.");
        }

        if (HasUnsupportedFilter(query))
        {
            return Unavailable<Page<CaseWorkspaceRow>>("One or more selected filters are not available for this case list.");
        }

        var countries = await _publicMetadataClient.GetCountriesAsync(cancellationToken).ConfigureAwait(false);
        var labels = countries.Succeeded && countries.Value is not null
            ? countries.Value.Countries.ToDictionary(country => country.Code, country => country.Label, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var matching = queue.Items
            .Select(item => (Item: item, Row: ToRow(item, labels)))
            .Where(pair => Matches(pair.Item, pair.Row, query))
            .OrderBy(pair => pair.Row.UpdatedAtUtc, query.Sort == CaseWorkspaceSort.UpdatedNewestFirst
                ? Comparer<DateTimeOffset>.Create((left, right) => right.CompareTo(left))
                : Comparer<DateTimeOffset>.Default)
            .ToArray();
        var pageItems = matching
            .Skip((query.Page.PageNumber - 1) * query.Page.PageSize)
            .Take(query.Page.PageSize)
            .Select(pair => pair.Row)
            .ToArray();
        var page = new Page<CaseWorkspaceRow>(
            pageItems,
            query.Page.PageNumber,
            query.Page.PageSize,
            matching.Length,
            query.Page.PageNumber * query.Page.PageSize < matching.Length);

        return Success(page, queue.Items.Count == MaximumRows
            ? "Showing the most recent 100 cases available to this list."
            : null);
    }

    public async Task<UiCapabilityResult<CaseWorkspaceFilterOptions>> GetFilterOptionsAsync(
        CancellationToken cancellationToken = default)
    {
        var queue = await _caseQueueClient.GetMineAsync(MaximumRows, cancellationToken).ConfigureAwait(false);
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
            [],
            workflows,
            countryOptions,
            [],
            [],
            [],
            new CaseWorkspaceFilterCapabilities(
                Search: true,
                Sector: false,
                Workflow: true,
                Country: countries.Succeeded && countries.Value is not null,
                Region: false,
                Assignee: false,
                ReportedCategory: false,
                UpdatedDate: true));

        return Success(filterOptions, "Some filters are not available for this case list.");
    }

    private static CaseWorkspaceRow ToRow(CaseQueueItem item, IReadOnlyDictionary<string, string> countryLabels) => new(
        item.CaseId,
        PublicReference: null,
        item.Workflow,
        countryLabels.GetValueOrDefault(item.CountryCode) ?? "Country not listed",
        CaseWorkspaceSector.Unknown,
        RegionLabel: null,
        AssigneeLabel: "Assigned to you",
        ReportedCategoryLabel: null,
        item.UpdatedAtUtc,
        item.AggregateVersion,
        [CaseWorkspaceAction.Open]);

    private static bool HasUnsupportedFilter(CaseWorkspaceQuery query) =>
        query.Sector.HasValue ||
        !string.IsNullOrWhiteSpace(query.RegionKey) ||
        !string.IsNullOrWhiteSpace(query.AssigneeKey) ||
        !string.IsNullOrWhiteSpace(query.ReportedCategoryKey);

    private static bool Matches(CaseQueueItem item, CaseWorkspaceRow row, CaseWorkspaceQuery query) =>
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
