using System.Globalization;
using SwiftAid.Contracts.Ellai;
using SwiftAid.UI.Ellai.Guidance;

namespace SwiftAid.UI.Components.Surfaces.Operations;

public partial class OperationsCaseQueue
{
    private EllaiCaseFilterContext? _ellaiFilters;
    private bool _ellaiDisposed;
    private bool _ellaiApplyingFilter;
    protected override void OnParametersSet()
    {
        _ellaiFilters = EllaiServices.GetService(typeof(EllaiCaseFilterContext)) as EllaiCaseFilterContext;
        _ellaiFilters?.Register(this, ApplyEllaiFilterAsync);
    }
    public void Dispose()
    {
        _ellaiDisposed = true;
        _ellaiFilters?.Remove(this);
    }

    public async Task<bool> ApplyEllaiFilterAsync(UiIntentProposal proposal, CancellationToken cancellationToken)
    {
        if (_ellaiDisposed || _ellaiApplyingFilter || !OnQueryChanged.HasDelegate ||
            State is not (OperationsSurfaceState.Populated or OperationsSurfaceState.Empty) ||
            !EllaiCaseFilterProposal.IsValid(proposal) || !IsSemanticControlRendered(proposal.ControlId!)) return false;
        cancellationToken.ThrowIfCancellationRequested();
        var query = Query with { PageNumber = 1 };
        var value = proposal.Arguments.GetValueOrDefault("value")?.Trim() ?? string.Empty;
        string? option;
        switch (proposal.ControlId![10..])
        {
            case "search": case "mobile-search": query = query with { Search = NullIfEmpty(value) }; break;
            case "sector":
                if (!ResolveOption(FilterOptions?.Sectors, value, out option)) return false;
                if (option is null) query = query with { Sector = null };
                else if (ParseSector(option) is { } sector)
                    query = query with { Sector = sector };
                else return false;
                break;
            case "status":
                if (!ResolveOption(FilterOptions?.Workflows, value, out option)) return false;
                query = query with { WorkflowKey = option }; break;
            case "country":
                if (!ResolveOption(FilterOptions?.Countries, value, out option)) return false;
                query = query with { CountryCode = option, RegionKey = null }; break;
            case "region":
                if (!ResolveOption(FilterOptions?.Regions, value, out option)) return false;
                query = query with { RegionKey = option }; break;
            case "assignee":
                if (!ResolveOption(FilterOptions?.Assignees, value, out option)) return false;
                query = query with { AssigneeKey = option }; break;
            case "category":
                if (!ResolveOption(FilterOptions?.ReportedCategories, value, out option)) return false;
                query = query with { ReportedCategoryKey = option }; break;
            case "from": query = query with { UpdatedFromUtc = DateBoundary(value, false) }; break;
            case "to": query = query with { UpdatedToUtc = DateBoundary(value, true) }; break;
            case "sort": query = query with { NewestFirst = value == "newest" }; break;
            case "clear": query = query with { Search = null, Sector = null, WorkflowKey = null, CountryCode = null,
                RegionKey = null, AssigneeKey = null, ReportedCategoryKey = null, UpdatedFromUtc = null, UpdatedToUtc = null }; break;
            default: return false;
        }
        if (query.UpdatedFromUtc > query.UpdatedToUtc) return false;
        _ellaiApplyingFilter = true;
        try
        {
            await PublishAsync(query);
            return true; // Callback delivered; never proof of rows loaded or a business action.
        }
        finally { _ellaiApplyingFilter = false; }
    }

    private static bool ResolveOption(IReadOnlyList<FilterOptionView>? options, string value, out string? key)
    {
        key = null;
        if (value.Length == 0) return true;
        var matches = options?.Where(option => string.Equals(option.Key, value, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(option.Label, value, StringComparison.OrdinalIgnoreCase)).Select(option => option.Key).Distinct(StringComparer.Ordinal).Take(2).ToArray();
        if (matches is not { Length: 1 }) return false;
        key = matches[0];
        return true;
    }
    private static DateTimeOffset? DateBoundary(string value, bool end) => value.Length == 0 ? null :
        new DateTimeOffset(DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToDateTime(end ? TimeOnly.MaxValue : TimeOnly.MinValue), TimeSpan.Zero);
}
