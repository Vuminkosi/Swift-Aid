using SwiftAid.Contracts.Geography;
using SwiftAid.UI.Components.Geography;
using SwiftAid.UI.Components.Public;

namespace SwiftAid.Ui.Tests;

public sealed class GeoPlaceDraftMapperTests
{
    private static readonly GeoPlaceOption Mbabane = new(
        "SZ:place:mbabane", "Mbabane", "Settlement", "SZ", "Eswatini", "Mbabane, Hhohho, Eswatini", true,
        [new GeoPlaceAncestor("country:SZ", "Eswatini", "Country"), new GeoPlaceAncestor("SZ:adm1:hhohho", "Hhohho", "Region")]);

    private static readonly GeoPlaceOption Hhohho = new(
        "SZ:adm1:hhohho", "Hhohho", "Region", "SZ", "Eswatini", "Hhohho, Eswatini", true,
        [new GeoPlaceAncestor("country:SZ", "Eswatini", "Country")]);

    private static readonly IReadOnlyList<PublicCountryOption> Countries =
        [new PublicCountryOption("SZ", "Eswatini"), new PublicCountryOption("ZW", "Zimbabwe")];

    [Fact]
    public void Settlement_selection_fills_locality_and_region_with_stable_ids()
    {
        var draft = new PublicReportDraft();

        GeoPlaceDraftMapper.ApplySelection(draft, Mbabane, Countries);

        Assert.Equal("SZ", draft.CountryCode);
        Assert.False(draft.CountryConfirmed);
        Assert.False(draft.LocationUnknown);
        Assert.Equal("Hhohho", draft.Region);
        Assert.Equal("SZ:adm1:hhohho", draft.RegionId);
        Assert.Equal(PresenceState.Known, draft.RegionPresence);
        Assert.Equal("Mbabane", draft.Locality);
        Assert.Equal("SZ:place:mbabane", draft.LocalityId);
        Assert.Equal(PresenceState.Known, draft.LocalityPresence);
    }

    [Fact]
    public void Region_selection_fills_region_without_locality()
    {
        var draft = new PublicReportDraft();

        GeoPlaceDraftMapper.ApplySelection(draft, Hhohho, Countries);

        Assert.Equal("Hhohho", draft.Region);
        Assert.Equal("SZ:adm1:hhohho", draft.RegionId);
        Assert.Null(draft.Locality);
        Assert.Null(draft.LocalityId);
        Assert.Equal(PresenceState.Unknown, draft.LocalityPresence);
    }

    [Fact]
    public void Clearing_selection_keeps_country_but_drops_place_names()
    {
        var draft = new PublicReportDraft { CountryCode = "SZ" };
        GeoPlaceDraftMapper.ApplySelection(draft, Mbabane, Countries);

        GeoPlaceDraftMapper.ApplySelection(draft, null, Countries);

        Assert.Equal("SZ", draft.CountryCode);
        Assert.False(draft.LocationUnknown);
        Assert.Null(draft.Region);
        Assert.Null(draft.RegionId);
        Assert.Null(draft.Locality);
        Assert.Null(draft.LocalityId);
    }

    [Fact]
    public void Unsupported_country_keeps_draft_country_but_applies_names()
    {
        var draft = new PublicReportDraft { CountryCode = "ZW" };
        var foreign = Mbabane with { CountryCode = "XX" };

        GeoPlaceDraftMapper.ApplySelection(draft, foreign, Countries);

        Assert.Equal("ZW", draft.CountryCode);
        Assert.Equal("Mbabane", draft.Locality);
    }

    [Fact]
    public void ToSubmission_carries_stable_ids_and_round_trip_restores_them()
    {
        var draft = new PublicReportDraft
        {
            SubjectScope = SwiftAid.Contracts.Intake.IntakeSubjectScope.Human,
            Narrative = "Synthetic observation",
            CountryCode = "SZ",
            CountryConfirmed = true,
            ReportReviewConfirmed = true,
            ProcessingConfirmed = true
        };
        GeoPlaceDraftMapper.ApplySelection(draft, Mbabane, Countries);
        draft.CountryConfirmed = true;

        var submission = draft.ToSubmission(
            "synthetic-countries-v1",
            "/synthetic-privacy",
            "synthetic-policy-v1",
            Countries,
            countryCatalogueIsCurrent: true);

        Assert.Equal("SZ:adm1:hhohho", submission.Location.Region.NormalizedId);
        Assert.Equal("SZ:place:mbabane", submission.Location.Locality.NormalizedId);

        var restored = PublicReportDraft.FromSubmission(submission);
        Assert.Equal("SZ:adm1:hhohho", restored.RegionId);
        Assert.Equal("SZ:place:mbabane", restored.LocalityId);
        Assert.Equal("Hhohho", restored.Region);
        Assert.Equal("Mbabane", restored.Locality);
    }

    [Fact]
    public void Manual_place_without_ids_submits_null_normalized_ids()
    {
        var draft = new PublicReportDraft
        {
            SubjectScope = SwiftAid.Contracts.Intake.IntakeSubjectScope.Human,
            Narrative = "Synthetic observation",
            CountryCode = "SZ",
            CountryConfirmed = true,
            ReportReviewConfirmed = true,
            ProcessingConfirmed = true,
            RegionPresence = PresenceState.Known,
            Region = "Hhohho"
        };

        var submission = draft.ToSubmission(
            "synthetic-countries-v1",
            "/synthetic-privacy",
            "synthetic-policy-v1",
            Countries,
            countryCatalogueIsCurrent: true);

        Assert.Null(submission.Location.Region.NormalizedId);
        Assert.Equal("Hhohho", submission.Location.Region.ReportedName);
    }
}
