using SwiftAid.Client.Presentation;
using SwiftAid.Client.Intake;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Mobile.Services.Presentation;

/// <summary>Projects only fields supported by the existing authorized Mobile case queue.</summary>
public sealed class MobileCaseWorkspaceClient(ICaseQueueClient caseQueueClient, ICaseReportDetailsClient? reportClient = null,
    IGuestIntakeTransport? intakeTransport = null) : IProgressiveCaseWorkspaceClient, IIncrementalCaseWorkspaceClient
{
    private const int QueuePageSize = 100;
    private const string Capability = "case-workspace";
    private readonly ICaseQueueClient _caseQueueClient = caseQueueClient ?? throw new ArgumentNullException(nameof(caseQueueClient));

    public Task<UiCapabilityResult<Page<CaseWorkspaceRow>>> ListAsync(
        CaseWorkspaceQuery query,
        CancellationToken cancellationToken = default) => ListCoreAsync(query, includeReportDetails: true, cancellationToken);

    /// <summary>Loads authorized queue rows without waiting for optional report previews.</summary>
    public Task<UiCapabilityResult<Page<CaseWorkspaceRow>>> ListInitialAsync(
        CaseWorkspaceQuery query,
        CancellationToken cancellationToken = default) => ListCoreAsync(query, includeReportDetails: false, cancellationToken);

    public Task<UiCapabilityResult<Page<CaseWorkspaceRow>>> ListIncrementallyAsync(
        CaseWorkspaceQuery query, Func<Page<CaseWorkspaceRow>, Task> onProgress,
        CancellationToken cancellationToken = default) => ListCoreAsync(query, false, cancellationToken, onProgress);

    private async Task<UiCapabilityResult<Page<CaseWorkspaceRow>>> ListCoreAsync(
        CaseWorkspaceQuery query, bool includeReportDetails, CancellationToken cancellationToken,
        Func<Page<CaseWorkspaceRow>, Task>? onProgress = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        if (!query.IsValid)
            return Invalid<Page<CaseWorkspaceRow>>("Review the case filters and try again.");

        if (HasUnsupportedFilter(query))
            return Unavailable<Page<CaseWorkspaceRow>>("One or more selected filters are not available for this case list.");

        try
        {
            var countries = await GetCountriesAsync(cancellationToken).ConfigureAwait(false);
            var result = await GetCompleteQueueAsync(cancellationToken, onProgress is null ? null :
                items => onProgress(BuildPage(items, query, countries))).ConfigureAwait(false);
            if (result.Access == CaseQueueAccess.Denied)
                return Denied<Page<CaseWorkspaceRow>>();
            if (result.Access != CaseQueueAccess.Allowed)
                return Unavailable<Page<CaseWorkspaceRow>>("The authorized case list is unavailable right now.");

            var page = BuildPage(result.Items, query, countries);
            return includeReportDetails ? await EnrichAsync(page, cancellationToken).ConfigureAwait(false) : Success(page);
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

    private static Page<CaseWorkspaceRow> BuildPage(IReadOnlyList<CaseQueueItem> items,
        CaseWorkspaceQuery query, IReadOnlyDictionary<string, string> countries)
    {
        var matching = items
            .Where(item => Matches(item, query, countries))
            .OrderBy(item => item.UpdatedAtUtc,
                query.Sort == CaseWorkspaceSort.UpdatedNewestFirst
                    ? Comparer<DateTimeOffset>.Create((left, right) => right.CompareTo(left))
                    : Comparer<DateTimeOffset>.Default)
            .ThenBy(item => item.CaseId)
            .ToArray();
        var rows = matching
            .Skip((int)Math.Min((long)(query.Page.PageNumber - 1) * query.Page.PageSize, matching.Length))
            .Take(query.Page.PageSize)
            .Select(item => ToRow(item, countries))
            .ToArray();
        return new Page<CaseWorkspaceRow>(
            rows,
            query.Page.PageNumber,
            query.Page.PageSize,
            matching.Length,
            (long)query.Page.PageNumber * query.Page.PageSize < matching.Length);
    }

    /// <summary>Adds optional report details to an already authorized page.</summary>
    public async Task<UiCapabilityResult<Page<CaseWorkspaceRow>>> EnrichAsync(
        Page<CaseWorkspaceRow> page, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (reportClient is null) return Success(page);
        var rows = page.Items.ToArray();
        try
        {
            for (var offset = 0; offset < rows.Length; offset += 4)
            {
                var batch = rows.Skip(offset).Take(4).ToArray();
                var reports = await Task.WhenAll(batch.Select(row => reportClient.GetAsync(row.CaseId, cancellationToken))).ConfigureAwait(false);
                for (var index = 0; index < batch.Length; index++)
                {
                    var report = reports[index];
                    if (report.Code == UiCapabilityResultCode.Denied)
                        return Denied<Page<CaseWorkspaceRow>>();
                    if (report.Succeeded && report.Value is { } details && details.CaseId == batch[index].CaseId)
                    {
                        rows[offset + index] = batch[index] with
                        {
                            RegionLabel = details.Location is { } place ? CaseReportDisplay.LocationLabel(place) : null,
                            LocationHref = details.Location is { } target ? CaseReportDisplay.MapHref(target) : null,
                            ObservationPreview = CaseReportDisplay.ObservationPreview(details)
                        };
                    }
                }
            }
            return Success(page with { Items = rows });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return Unavailable<Page<CaseWorkspaceRow>>("Case details are temporarily unavailable."); }
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

            var countries = await GetCountriesAsync(cancellationToken).ConfigureAwait(false);
            var options = new CaseWorkspaceFilterOptions(
                Sectors: result.Items.Select(item => ToSector(item.Sector)).Distinct()
                    .Select(sector => new CaseWorkspaceOption(sector.ToString(), sector.ToString())).ToArray(),
                Workflows: result.Items.Select(item => new CaseWorkspaceOption(item.Workflow, WorkflowLabel(item.Workflow)))
                    .DistinctBy(item => item.Key, StringComparer.OrdinalIgnoreCase).OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase).ToArray(),
                Countries: countries.Count > 0 ? countries.Select(item => new CaseWorkspaceOption(item.Key, item.Value))
                    .OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase).ToArray() : ToOptions(result.Items.Select(item => item.CountryCode)),
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

    private static bool Matches(CaseQueueItem item, CaseWorkspaceQuery query, IReadOnlyDictionary<string, string> countries)
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
            WorkflowLabel(item.Workflow).Contains(query.Search.Trim(), StringComparison.OrdinalIgnoreCase) ||
            item.CountryCode.Contains(query.Search.Trim(), StringComparison.OrdinalIgnoreCase) ||
            countries.TryGetValue(item.CountryCode, out var label) && label.Contains(query.Search.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static CaseWorkspaceRow ToRow(CaseQueueItem item, IReadOnlyDictionary<string, string> countries) => new(
        item.CaseId,
        PublicReference: CaseReference(item.CaseId),
        item.Workflow,
        CountryLabel: countries.GetValueOrDefault(item.CountryCode) ?? item.CountryCode,
        ToSector(item.Sector),
        RegionLabel: null,
        AssigneeLabel: "Assigned to you",
        ReportedCategoryLabel: item.Classification,
        item.UpdatedAtUtc,
        item.AggregateVersion,
        [CaseWorkspaceAction.Open]);

    private static string CaseReference(Guid caseId) => "Case " + caseId.ToString("N")[..8];

    private async Task<IReadOnlyDictionary<string, string>> GetCountriesAsync(CancellationToken ct)
    {
        if (intakeTransport is null) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var catalogue = await intakeTransport.GetCountriesAsync(ct).ConfigureAwait(false);
            if (catalogue.Countries is not { Count: > 0 and <= 300 } ||
                catalogue.Countries.Any(item => item is null || item.Code is not { Length: 2 } ||
                    !item.Code.All(char.IsAsciiLetterUpper) || string.IsNullOrWhiteSpace(item.Label) || item.Label.Length > 200) ||
                catalogue.Countries.Select(item => item.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count() != catalogue.Countries.Count)
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return catalogue.Countries.ToDictionary(item => item.Code, item => item.Label, StringComparer.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
    }

    private static string WorkflowLabel(string workflow) => workflow switch
    {
        "Open" => "Open", "Assigned" => "Assigned", "Escalated" => "Escalated",
        "UnderInvestigation" => "Under investigation", "Reported" => "Reported",
        "UnderReview" => "Under review", "Active" => "Active", "AwaitingFollowUp" => "Follow-up due",
        "Resolved" => "Resolved", _ => "Status unavailable"
    };

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
