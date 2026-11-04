namespace SwiftAid.Client.Presentation;

/// <summary>
/// Static source-linked education catalogue. Cards are bibliographic pointers to
/// approved external references plus app-owned operational pointers; they carry no
/// model-authored health guidance. Review state and reuse rights travel with each
/// card, and withdrawn material is removed from this list rather than hidden.
/// </summary>
public sealed class StaticEducationClient : IEducationClient
{
    private const string Capability = "education";

    private static readonly IReadOnlyList<EducationMaterial> Catalogue = new[]
    {
        new EducationMaterial(
            "who-cholera-factsheet",
            "Cholera — WHO fact sheet",
            "External WHO reference on cholera. Open the source for authoritative content; this card carries no clinical guidance.",
            "Human health",
            "en",
            "World Health Organization",
            "source-identified 2026-10-25",
            IsApproved: false,
            IsAvailableOffline: false,
            ReviewState: EducationReviewState.DemoOnly,
            ReuseRights: EducationReuseRights.Restricted),
        new EducationMaterial(
            "who-malaria-factsheet",
            "Malaria — WHO fact sheet",
            "External WHO reference on malaria. Open the source for authoritative content; this card carries no clinical guidance.",
            "Human health",
            "en",
            "World Health Organization",
            "source-identified 2026-10-25",
            IsApproved: false,
            IsAvailableOffline: false,
            ReviewState: EducationReviewState.DemoOnly,
            ReuseRights: EducationReuseRights.Restricted),
        new EducationMaterial(
            "who-tuberculosis-factsheet",
            "Tuberculosis — WHO fact sheet",
            "External WHO reference on tuberculosis. Open the source for authoritative content; this card carries no clinical guidance.",
            "Human health",
            "en",
            "World Health Organization",
            "source-identified 2026-10-25",
            IsApproved: false,
            IsAvailableOffline: false,
            ReviewState: EducationReviewState.DemoOnly,
            ReuseRights: EducationReuseRights.Restricted),
        new EducationMaterial(
            "woah-animal-health-reference",
            "Animal health — WOAH reference",
            "External WOAH animal-health reference. Open the source for authoritative content; this card carries no veterinary guidance.",
            "Animal health",
            "en",
            "World Organisation for Animal Health",
            "source-identified 2026-10-25",
            IsApproved: false,
            IsAvailableOffline: false,
            ReviewState: EducationReviewState.DemoOnly,
            ReuseRights: EducationReuseRights.Restricted),
        new EducationMaterial(
            "swiftaid-field-reporting-guide",
            "Recording a field report in SwiftAid",
            "App-owned pointer describing where to open the guided report flow. It is not health guidance.",
            "Operations",
            "en",
            "SwiftAid",
            "app-1",
            IsApproved: false,
            IsAvailableOffline: false,
            ReviewState: EducationReviewState.AppAuthoredProcess,
            ReuseRights: EducationReuseRights.AppOwnedOriginal)
    };

    public Task<UiCapabilityResult<Page<EducationMaterial>>> ListAsync(
        EducationQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        if (query.Page is null || !query.Page.IsValid)
        {
            return Task.FromResult(new UiCapabilityResult<Page<EducationMaterial>>(
                UiCapabilityResultCode.InvalidRequest,
                null,
                Metadata(),
                "Choose a valid page to browse education material."));
        }

        IEnumerable<EducationMaterial> matches = Catalogue;
        if (query.OfflineOnly)
        {
            matches = Array.Empty<EducationMaterial>();
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(query.Category))
            {
                var category = query.Category.Trim();
                matches = matches.Where(material =>
                    string.Equals(material.Category, category, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(query.Language))
            {
                var language = query.Language.Trim();
                matches = matches.Where(material =>
                    string.Equals(material.Language, language, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(query.Search))
            {
                var search = query.Search.Trim();
                matches = matches.Where(material =>
                    material.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    material.Summary.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    material.Source.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    material.Category.Contains(search, StringComparison.OrdinalIgnoreCase));
            }
        }

        var ordered = matches.OrderBy(material => material.Id, StringComparer.Ordinal).ToList();
        var skip = (query.Page.PageNumber - 1) * query.Page.PageSize;
        var items = ordered.Skip(skip).Take(query.Page.PageSize).ToList();
        var page = new Page<EducationMaterial>(
            items,
            query.Page.PageNumber,
            query.Page.PageSize,
            ordered.Count,
            skip + items.Count < ordered.Count);

        return Task.FromResult(new UiCapabilityResult<Page<EducationMaterial>>(
            UiCapabilityResultCode.Success,
            page,
            Metadata(),
            null));
    }

    private static UiCapabilityMetadata Metadata() => new(
        Capability,
        UiCapabilityResults.ContractVersion,
        UiCapabilityProvenance.LocalOnly,
        DateTimeOffset.UtcNow,
        IsAvailable: true,
        "Static source-linked catalogue; open each source for authoritative content.");
}
