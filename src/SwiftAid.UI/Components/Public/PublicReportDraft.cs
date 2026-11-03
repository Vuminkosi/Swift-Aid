using System.ComponentModel.DataAnnotations;
using SwiftAid.Contracts.Geography;
using SwiftAid.Contracts.Intake;

namespace SwiftAid.UI.Components.Public;

/// <summary>Temporary in-memory form values; this type does not represent saved or submitted work.</summary>
public sealed class PublicReportDraft : IValidatableObject
{
    public PublicReportDraft Copy() => (PublicReportDraft)MemberwiseClone();

    public GuestDraftFormSnapshot ToFormSnapshot()
    {
        var snapshot = new GuestDraftFormSnapshot(GuestDraftFormSnapshot.CurrentSchemaVersion,
            System.Text.Json.JsonSerializer.Serialize(this));
        if (!snapshot.IsValid()) throw new InvalidOperationException("The editable draft is too large or invalid.");
        return snapshot;
    }

    public static PublicReportDraft FromFormSnapshot(GuestDraftFormSnapshot snapshot)
    {
        if (snapshot is null || !snapshot.IsValid()) throw new InvalidOperationException("The editable draft is invalid or unsupported.");
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(snapshot.Json);
            var expected = new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(SubjectScope), nameof(Narrative), nameof(ObservationsText), nameof(OccurrencePrecision),
                nameof(OccurrenceDate), nameof(OccurrenceTime), nameof(OccurrenceOffset), nameof(TimeNote), nameof(CapturedAt),
                nameof(LocationUnknown), nameof(CountryCode), nameof(CountryConfirmed), nameof(RegionPresence), nameof(Region),
                nameof(RegionId), nameof(AreaPresence), nameof(Area), nameof(AreaId), nameof(LocalityPresence), nameof(Locality),
                nameof(LocalityId), nameof(ReportReviewConfirmed), nameof(ProcessingConfirmed)
            };
            var names = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
            if (names.Length != expected.Count || names.Distinct(StringComparer.Ordinal).Count() != names.Length ||
                names.Any(name => !expected.Contains(name)))
                throw new InvalidOperationException("The editable draft fields are incomplete or unsupported.");
            var draft = System.Text.Json.JsonSerializer.Deserialize<PublicReportDraft>(snapshot.Json)
                ?? throw new InvalidOperationException("The editable draft is empty.");
            if (draft.SubjectScope is null || !Enum.IsDefined(draft.SubjectScope.Value))
                throw new InvalidOperationException("The editable draft subject is invalid.");
            return draft;
        }
        catch (System.Text.Json.JsonException error)
        {
            throw new InvalidOperationException("The editable draft cannot be read.", error);
        }
    }

    [Required(ErrorMessage = "Choose whether this concern is about a person or an animal.")]
    public IntakeSubjectScope? SubjectScope { get; set; }

    [StringLength(6000, ErrorMessage = "Keep the description under 6,000 characters.")]
    public string? Narrative { get; set; }

    [StringLength(20000, ErrorMessage = "Keep the observations under 20,000 characters.")]
    public string? ObservationsText { get; set; }

    public ReportedTimePrecision OccurrencePrecision { get; set; } = ReportedTimePrecision.Unknown;
    public DateTime? OccurrenceDate { get; set; }
    public string? OccurrenceTime { get; set; }
    public string? OccurrenceOffset { get; set; }
    public string? TimeNote { get; set; }
    public DateTimeOffset? CapturedAt { get; set; }
    public bool LocationUnknown { get; set; } = true;
    public string? CountryCode { get; set; }
    public bool CountryConfirmed { get; set; }
    public PresenceState RegionPresence { get; set; } = PresenceState.Unknown;
    public string? Region { get; set; }
    /// <summary>Stable registry ID for the region, when chosen from the place registry.</summary>
    public string? RegionId { get; set; }
    public PresenceState AreaPresence { get; set; } = PresenceState.Unknown;
    public string? Area { get; set; }
    /// <summary>Stable registry ID for the area, when chosen from the place registry.</summary>
    public string? AreaId { get; set; }
    public PresenceState LocalityPresence { get; set; } = PresenceState.Unknown;
    public string? Locality { get; set; }
    /// <summary>Stable registry ID for the locality, when chosen from the place registry.</summary>
    public string? LocalityId { get; set; }
    public bool ReportReviewConfirmed { get; set; }
    public bool ProcessingConfirmed { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var observations = GetObservations();
        if (observations.Count > 20)
            yield return new ValidationResult("Add no more than 20 separate observations.", [nameof(ObservationsText)]);
        if (observations.Any(value => value.Length > 1000))
            yield return new ValidationResult("Keep each observation under 1,000 characters.", [nameof(ObservationsText)]);

        if (string.IsNullOrWhiteSpace(Narrative) && observations.Count == 0)
        {
            yield return new ValidationResult(
                "Describe what happened or add an observation.",
                [nameof(Narrative), nameof(ObservationsText)]);
        }

        if (!ReportReviewConfirmed)
        {
            yield return new ValidationResult(
                "Confirm that you reviewed the information you entered.",
                [nameof(ReportReviewConfirmed)]);
        }

        if (OccurrencePrecision != ReportedTimePrecision.Unknown && !OccurrenceDate.HasValue)
        {
            yield return new ValidationResult("Add the reported date or choose that the time is unknown.", [nameof(OccurrenceDate)]);
        }

        if (OccurrencePrecision == ReportedTimePrecision.Exact && !TryGetExactOccurrence(out _))
        {
            yield return new ValidationResult(
                "For an exact time, enter the time and its UTC offset. Otherwise choose an approximate time.",
                [nameof(OccurrenceTime), nameof(OccurrenceOffset)]);
        }

        if (!IsValidPlaceName(RegionPresence, Region) ||
            !IsValidPlaceName(AreaPresence, Area) ||
            !IsValidPlaceName(LocalityPresence, Locality))
        {
            yield return new ValidationResult("Enter each known place name using at most 200 characters, or leave it unknown.", [nameof(Region), nameof(Area), nameof(Locality)]);
        }
    }

    public static PublicReportDraft FromSubmission(IntakeSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        var occurrence = submission.Occurrence;
        var localOccurrence = ToLocalOccurrence(occurrence);
        var location = submission.Location;
        var confirmations = submission.Confirmations;

        return new PublicReportDraft
        {
            SubjectScope = submission.SubjectScope,
            Narrative = submission.Narrative,
            ObservationsText = string.Join(Environment.NewLine, submission.Observations),
            OccurrencePrecision = occurrence.Precision,
            OccurrenceDate = localOccurrence.Date,
            OccurrenceTime = localOccurrence.Time,
            OccurrenceOffset = localOccurrence.Offset,
            TimeNote = occurrence.ZoneId,
            CapturedAt = submission.CapturedAt.Value,
            LocationUnknown = string.IsNullOrWhiteSpace(location.CountryCode) &&
                              location.Region.Presence != PresenceState.Known &&
                              location.Area.Presence != PresenceState.Known &&
                              location.Locality.Presence != PresenceState.Known,
            CountryCode = location.CountryCode,
            CountryConfirmed = confirmations.OccurrenceCountryConfirmed,
            RegionPresence = location.Region.Presence,
            Region = location.Region.ReportedName,
            RegionId = location.Region.NormalizedId,
            AreaPresence = location.Area.Presence,
            Area = location.Area.ReportedName,
            AreaId = location.Area.NormalizedId,
            LocalityPresence = location.Locality.Presence,
            Locality = location.Locality.ReportedName,
            LocalityId = location.Locality.NormalizedId,
            ReportReviewConfirmed = confirmations.ReportReviewConfirmed,
            ProcessingConfirmed = confirmations.ProcessingConfirmed
        };
    }

    public IntakeSubmission ToSubmission(
        string? countryCatalogueVersion,
        string? processingPolicyReference,
        string? processingPolicyVersion,
        IReadOnlyList<PublicCountryOption> countries,
        bool countryCatalogueIsCurrent,
        bool allowUnverifiedCountryForDraft = false)
    {
        ArgumentNullException.ThrowIfNull(countries);

        if (SubjectScope is null)
            throw new InvalidOperationException("Choose whether the concern is about a person or an animal before saving.");

        if (Narrative?.Length > 6000)
            throw new InvalidOperationException("Keep the description under 6,000 characters.");

        var selectedCountry = string.IsNullOrWhiteSpace(CountryCode)
            ? null
            : countries.FirstOrDefault(country => string.Equals(
                country.Code,
                CountryCode.Trim(),
                StringComparison.OrdinalIgnoreCase));

        var hasCurrentCountry = countryCatalogueIsCurrent &&
            !string.IsNullOrWhiteSpace(countryCatalogueVersion) && selectedCountry is not null;
        var enteredCountry = CountryCode?.Trim().ToUpperInvariant();
        var canPreserveUnverifiedCountry = allowUnverifiedCountryForDraft && enteredCountry is { Length: 2 } &&
            enteredCountry.All(char.IsAsciiLetter);
        if (!string.IsNullOrWhiteSpace(CountryCode) && !hasCurrentCountry && !canPreserveUnverifiedCountry)
        {
            throw new InvalidOperationException(
                "The selected country is not in the current country list. Choose a current country or mark the location unknown.");
        }

        var observations = GetObservations();
        if (!HasValidObservations(observations))
            throw new InvalidOperationException("Use at most 20 observations, 1,000 characters each and 20,000 characters in total.");

        if (!IsValidPlaceName(RegionPresence, Region) ||
            !IsValidPlaceName(AreaPresence, Area) ||
            !IsValidPlaceName(LocalityPresence, Locality))
        {
            throw new InvalidOperationException("Enter a place name using at most 200 characters, or mark it unknown.");
        }

        if (!TryGetExactOccurrence(out var exactOccurrence) && OccurrencePrecision == ReportedTimePrecision.Exact)
            throw new InvalidOperationException("Enter the exact time and its UTC offset, or mark the time approximate.");

        var capturedAt = CapturedAt.HasValue
            ? new ReportedTime(
                CapturedAt.Value.ToUniversalTime(),
                null,
                null,
                null,
                FormatOffset(CapturedAt.Value.Offset),
                SafeLocalZoneId(),
                ReportedTimePrecision.Exact)
            : new ReportedTime(null, null, null, null, null, null, ReportedTimePrecision.Unknown);

        var occurrence = OccurrencePrecision switch
        {
            ReportedTimePrecision.Unknown => new ReportedTime(null, null, null, null, null, null, ReportedTimePrecision.Unknown),
            ReportedTimePrecision.Exact => exactOccurrence!,
            _ => new ReportedTime(
                null,
                OccurrenceDate is { } date ? DateOnly.FromDateTime(date) : null,
                null,
                null,
                OccurrenceOffset,
                TimeNote,
                OccurrencePrecision)
        };

        var hasReportedLocation =
            !string.IsNullOrWhiteSpace(CountryCode) ||
            RegionPresence == PresenceState.Known ||
            AreaPresence == PresenceState.Known ||
            LocalityPresence == PresenceState.Known;

        var location = LocationUnknown && !hasReportedLocation
            ? new LocationContext(
                string.Empty,
                new OptionalPlace(null, null, PresenceState.Unknown),
                new OptionalPlace(null, null, PresenceState.Unknown),
                new OptionalPlace(null, null, PresenceState.Unknown),
                null,
                null,
                null,
                LocationSource.Reporter,
                LocationPrecision.Unknown,
                null)
            : new LocationContext(
                hasCurrentCountry ? selectedCountry!.Code : canPreserveUnverifiedCountry ? enteredCountry! : string.Empty,
                ToOptionalPlace(RegionPresence, Region, RegionId),
                ToOptionalPlace(AreaPresence, Area, AreaId),
                ToOptionalPlace(LocalityPresence, Locality, LocalityId),
                null,
                null,
                null,
                LocationSource.Reporter,
                LocationPrecision.Approximate,
                hasCurrentCountry ? countryCatalogueVersion : null);

        var countryCode = location.CountryCode;
        var confirmations = new IntakeConfirmations(
            ReportReviewConfirmed,
            countryCode,
            hasCurrentCountry ? countryCatalogueVersion! : string.Empty,
            hasCurrentCountry && CountryConfirmed,
            ProcessingConfirmed,
            processingPolicyReference ?? string.Empty,
            processingPolicyVersion ?? string.Empty,
            null,
            null,
            null);

        return new IntakeSubmission(
            1,
            Guid.Empty,
            SubjectScope.Value,
            string.IsNullOrWhiteSpace(Narrative) ? null : Narrative.Trim(),
            observations,
            [],
            capturedAt,
            occurrence,
            location,
            confirmations);
    }

    internal bool HasValidObservations(IReadOnlyList<string> observations, int maximumCount = 20, int maximumCharacters = 1000) =>
        (ObservationsText?.Length ?? 0) <= 20000 && observations.Count <= maximumCount &&
        observations.All(value => value.Length <= maximumCharacters);

    public IReadOnlyList<string> GetObservations() => (ObservationsText ?? string.Empty)
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .ToArray();

    internal bool HasValidExactOccurrence() => TryGetExactOccurrence(out _);

    private bool TryGetExactOccurrence(out ReportedTime? occurrence)
    {
        occurrence = null;
        if (OccurrencePrecision != ReportedTimePrecision.Exact ||
            !OccurrenceDate.HasValue ||
            !TimeOnly.TryParse(OccurrenceTime, out var time) ||
            !TryParseOffset(OccurrenceOffset, out var offset))
        {
            return false;
        }

        try
        {
            var local = DateOnly.FromDateTime(OccurrenceDate.Value).ToDateTime(time, DateTimeKind.Unspecified);
            var instant = new DateTimeOffset(local, offset);
            occurrence = new ReportedTime(
                instant.ToUniversalTime(),
                null,
                null,
                null,
                FormatOffset(offset),
                TimeNote,
                ReportedTimePrecision.Exact);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static (DateTime? Date, string? Time, string? Offset) ToLocalOccurrence(ReportedTime occurrence)
    {
        if (occurrence.Value is { } instant)
        {
            var offset = TimeSpan.Zero;
            if (!string.IsNullOrWhiteSpace(occurrence.OriginalOffset) &&
                TimeSpan.TryParse(occurrence.OriginalOffset, out var parsedOffset) &&
                parsedOffset >= TimeSpan.FromHours(-14) && parsedOffset <= TimeSpan.FromHours(14))
            {
                offset = parsedOffset;
            }
            var local = instant.ToOffset(offset);
            return (local.DateTime.Date, local.ToString("HH:mm"), offset.ToString());
        }

        var date = occurrence.LocalDate ?? occurrence.RangeStart;
        return (date?.ToDateTime(TimeOnly.MinValue), null, occurrence.OriginalOffset);
    }

    /// <summary>Formats an offset exactly as the intake contract requires (signed ±HH:mm).</summary>
    private static string FormatOffset(TimeSpan offset)
    {
        var absolute = offset.Duration();
        return string.Concat(offset < TimeSpan.Zero ? "-" : "+", absolute.ToString(@"hh\:mm"));
    }

    internal static bool TryParseOffset(string? value, out TimeSpan offset)
    {
        offset = default;
        if (value is null || value.Length != 6 || value[3] != ':' ||
            value[0] is not ('+' or '-') ||
            value[1] is < '0' or > '9' || value[2] is < '0' or > '9' ||
            value[4] is < '0' or > '9' || value[5] is < '0' or > '9' ||
            !int.TryParse(value.AsSpan(1, 2), out var hours) ||
            !int.TryParse(value.AsSpan(4, 2), out var minutes) ||
            hours > 14 || minutes > 59 || (hours == 14 && minutes != 0))
        {
            return false;
        }

        var totalMinutes = (hours * 60) + minutes;
        if (value[0] == '-')
            totalMinutes = -totalMinutes;
        offset = TimeSpan.FromMinutes(totalMinutes);
        return true;
    }

    internal static bool IsValidPlaceName(PresenceState presence, string? name) =>
        presence != PresenceState.Known || (!string.IsNullOrWhiteSpace(name) && name.Length <= 200);

    private static OptionalPlace ToOptionalPlace(PresenceState presence, string? name, string? normalizedId = null) =>
        new(presence == PresenceState.Known ? name?.Trim() : null,
            presence == PresenceState.Known && !string.IsNullOrWhiteSpace(normalizedId) ? normalizedId.Trim() : null,
            presence);

    private static string? SafeLocalZoneId()
    {
        try
        {
            return TimeZoneInfo.Local.Id;
        }
        catch
        {
            return null;
        }
    }
}

public sealed record PublicCountryOption(string Code, string Name);

public sealed record PublicReportSaveRequest(Guid? PreviousDraftId, IntakeSubmission Submission, GuestDraftFormSnapshot? FormSnapshot = null)
{
    // Set by the storage callback only after persistence succeeds, never by Ellai.
    public Guid? PersistedDraftId { get; private set; }

    public void ConfirmPersisted(Guid draftId, bool formSnapshotPersisted = false)
    {
        if (draftId == Guid.Empty) throw new ArgumentException("A persisted draft identity is required.", nameof(draftId));
        if (FormSnapshot is not null && !formSnapshotPersisted)
            throw new InvalidOperationException("The editable form snapshot was not confirmed as saved.");
        PersistedDraftId = draftId;
    }
}
