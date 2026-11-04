using SwiftAid.Contracts.Cases;

namespace SwiftAid.Client.Presentation;

public enum CaseWorkspaceSector
{
    Unknown,
    Human,
    Animal
}

public enum CaseWorkspaceSort
{
    UpdatedNewestFirst,
    UpdatedOldestFirst
}

public sealed record CaseWorkspaceQuery(
    PageRequest Page,
    string? Search = null,
    CaseWorkspaceSector? Sector = null,
    string? Workflow = null,
    string? CountryCode = null,
    string? RegionKey = null,
    string? AssigneeKey = null,
    string? ReportedCategoryKey = null,
    DateTimeOffset? UpdatedFromUtc = null,
    DateTimeOffset? UpdatedToUtc = null,
    CaseWorkspaceSort Sort = CaseWorkspaceSort.UpdatedNewestFirst)
{
    public bool IsValid =>
        Page is not null && Page.IsValid &&
        (Search is null || Search.Length <= 200) &&
        (Workflow is null || Workflow.Length <= 80) &&
        (CountryCode is null || CountryCode.Length <= 8) &&
        (RegionKey is null || RegionKey.Length <= 128) &&
        (AssigneeKey is null || AssigneeKey.Length <= 128) &&
        (ReportedCategoryKey is null || ReportedCategoryKey.Length <= 128) &&
        IsUtc(UpdatedFromUtc) && IsUtc(UpdatedToUtc) &&
        (!UpdatedFromUtc.HasValue || !UpdatedToUtc.HasValue || UpdatedFromUtc.Value <= UpdatedToUtc.Value) &&
        Enum.IsDefined(Sort) && (!Sector.HasValue || Enum.IsDefined(Sector.Value));

    private static bool IsUtc(DateTimeOffset? value) => value is null || value.Value.Offset == TimeSpan.Zero;
}

/// <summary>Page row for an authorized case workspace; keys and server identifiers stay out of display labels.</summary>
public sealed record CaseWorkspaceRow(
    Guid CaseId,
    string? PublicReference,
    string Workflow,
    string CountryLabel,
    CaseWorkspaceSector Sector,
    string? RegionLabel,
    string? AssigneeLabel,
    string? ReportedCategoryLabel,
    DateTimeOffset UpdatedAtUtc,
    long AggregateVersion,
    IReadOnlyList<CaseWorkspaceAction> AvailableActions,
    string? LocationHref = null,
    string? ObservationPreview = null);

public enum CaseWorkspaceAction
{
    Open,
    Assign,
    RequestAssistance,
    ScheduleFollowUp,
    Escalate
}

public sealed record CaseWorkspaceOption(string Key, string Label);

public sealed record CaseWorkspaceFilterCapabilities(
    bool Search,
    bool Sector,
    bool Workflow,
    bool Country,
    bool Region,
    bool Assignee,
    bool ReportedCategory,
    bool UpdatedDate);

public sealed record CaseWorkspaceFilterOptions(
    IReadOnlyList<CaseWorkspaceOption> Sectors,
    IReadOnlyList<CaseWorkspaceOption> Workflows,
    IReadOnlyList<CaseWorkspaceOption> Countries,
    IReadOnlyList<CaseWorkspaceOption> Regions,
    IReadOnlyList<CaseWorkspaceOption> Assignees,
    IReadOnlyList<CaseWorkspaceOption> ReportedCategories,
    CaseWorkspaceFilterCapabilities Capabilities);

/// <summary>Read-only, authorized case workspace query. Unsupported filters fail closed with scoped Unavailable.</summary>
public interface ICaseWorkspaceClient
{
    Task<UiCapabilityResult<Page<CaseWorkspaceRow>>> ListAsync(
        CaseWorkspaceQuery query,
        CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<CaseWorkspaceFilterOptions>> GetFilterOptionsAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>Allows a host to render authorized rows while optional details load.</summary>
public interface IProgressiveCaseWorkspaceClient : ICaseWorkspaceClient
{
    Task<UiCapabilityResult<Page<CaseWorkspaceRow>>> ListInitialAsync(
        CaseWorkspaceQuery query, CancellationToken cancellationToken = default);

    Task<UiCapabilityResult<Page<CaseWorkspaceRow>>> EnrichAsync(
        Page<CaseWorkspaceRow> page, CancellationToken cancellationToken = default);
}

/// <summary>Internal fixture projection used only by the isolated sample case client.</summary>
internal sealed record CaseWorkspaceSeed(
    CaseQueueItem Case,
    CaseWorkspaceSector Sector,
    string CountryLabel,
    string? RegionKey,
    string? RegionLabel,
    string? AssigneeKey,
    string? AssigneeLabel,
    string? ReportedCategoryKey,
    string? ReportedCategoryLabel,
    string? PublicReference);
