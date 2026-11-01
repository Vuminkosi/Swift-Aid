using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Registration;
using SwiftAid.Contracts.Geography;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Application.Tests;

public sealed class CaseRegistrationHasherTests
{
    private static readonly Guid CommandId = Guid.Parse("6a2bda2c-0d06-4e8a-9a56-36b86d1d4401");
    private static readonly Guid ReportId = Guid.Parse("faed0d15-8074-4d75-ae25-a76742d49a12");

    [Fact]
    public void Selected_organization_is_bound_to_the_receipt_without_changing_legacy_hashes()
    {
        var legacy = CreateRequest();
        var actor = CreateActor("officer-1");
        var first = new RegisterManagedCaseRequest(legacy.CommandId, legacy.WorkspaceId, legacy.Report, "organization-a");
        var second = new RegisterManagedCaseRequest(legacy.CommandId, legacy.WorkspaceId, legacy.Report, "organization-b");
        var firstHash = CanonicalCaseRegistrationHasher.CreateSnapshot(actor, first).CanonicalPayloadHash.ToArray();
        Assert.NotEqual(firstHash, CanonicalCaseRegistrationHasher.CreateSnapshot(actor, second).CanonicalPayloadHash.ToArray());
        Assert.NotEqual(firstHash, CanonicalCaseRegistrationHasher.CreateSnapshot(actor, legacy).CanonicalPayloadHash.ToArray());
        Assert.Equal("organization-a", CanonicalCaseRegistrationHasher.CreateSnapshot(actor, first).OrganizationId);
    }

    [Fact]
    public void Canonical_json_and_hash_match_the_normative_utf8_golden_vector()
    {
        const string expectedJson = "{\"protocolId\":\"swiftaid.case-command-receipt\",\"receiptProtocolVersion\":1,\"wireCommandSchemaVersion\":1,\"actorId\":\"officer-1\",\"commandId\":\"6a2bda2c-0d06-4e8a-9a56-36b86d1d4401\",\"commandType\":\"RegisterManagedCase\",\"workspaceId\":\"workspace-1\",\"report\":{\"kind\":\"HumanV2\",\"data\":{\"reportId\":\"faed0d15-8074-4d75-ae25-a76742d49a12\",\"context\":{\"occurrence\":{\"value\":{\"state\":\"Unknown\",\"value\":null},\"provenance\":{\"source\":\"ReporterObservation\",\"stableReference\":\"obs-1\"}},\"capturedAtUtc\":null,\"capturedOffset\":null,\"capturedZoneId\":null,\"capturePrecision\":\"Unknown\",\"location\":{\"countryCode\":\"SZ\",\"region\":{\"reportedName\":null,\"normalizedId\":null,\"presence\":\"Unknown\"},\"area\":{\"reportedName\":null,\"normalizedId\":null,\"presence\":\"Unknown\"},\"locality\":{\"reportedName\":null,\"normalizedId\":null,\"presence\":\"Unknown\"},\"latitude\":null,\"longitude\":null,\"accuracyMetres\":null,\"source\":\"Reporter\",\"precision\":\"Unknown\",\"catalogueVersion\":null}},\"pseudonymousSubjectReference\":{\"value\":{\"state\":\"Unknown\",\"value\":null},\"provenance\":{\"source\":\"ReporterObservation\",\"stableReference\":\"obs-1\"}},\"ageYears\":{\"value\":{\"state\":\"Unknown\",\"value\":null},\"provenance\":{\"source\":\"ReporterObservation\",\"stableReference\":\"obs-1\"}},\"reportedConcern\":{\"value\":{\"state\":\"Unknown\",\"value\":null},\"provenance\":{\"source\":\"ReporterObservation\",\"stableReference\":\"obs-1\"}},\"evidence\":{\"narrative\":{\"text\":\"Concern: naïve\",\"provenance\":{\"source\":\"ReporterNarrative\",\"stableReference\":\"narrative-1\"}},\"observations\":[],\"originalAudio\":null}}},\"confirmations\":{\"reportReviewConfirmed\":true,\"occurrenceCountryCode\":\"SZ\",\"occurrenceCountryCatalogueVersion\":\"catalogue-3\",\"occurrenceCountryConfirmed\":true,\"processingConfirmed\":true,\"processingPolicyReference\":\"policy-intake\",\"processingPolicyVersion\":\"2026-10\",\"recordingConfirmed\":null,\"recordingPolicyReference\":null,\"recordingPolicyVersion\":null}}";
        const string expectedSha256 = "E27F2413A6CF17420736199A492798F37E66D78626C5F25540BFF5A10078E692";

        var actor = CreateActor("officer-1");
        var snapshot = CanonicalCaseRegistrationHasher.CreateSnapshot(actor, CreateRequest());

        var independentlyComputed = SHA256.HashData(Encoding.UTF8.GetBytes(expectedJson));
        Assert.Equal(expectedSha256, Convert.ToHexString(independentlyComputed));
        Assert.Equal(independentlyComputed, snapshot.CanonicalPayloadHash.ToArray());
    }

    [Fact]
    public void Equivalent_utc_instants_hash_identically_but_scope_and_confirmation_changes_do_not()
    {
        var actor = CreateActor("officer-1");
        var baseline = CreateRequest(occurrence: new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
        var equivalent = CreateRequest(occurrence: new DateTimeOffset(2026, 10, 27, 14, 0, 0, TimeSpan.FromHours(2)));

        Assert.Equal(
            CanonicalCaseRegistrationHasher.CreateSnapshot(actor, baseline).CanonicalPayloadHash.ToArray(),
            CanonicalCaseRegistrationHasher.CreateSnapshot(actor, equivalent).CanonicalPayloadHash.ToArray());
        Assert.NotEqual(
            CanonicalCaseRegistrationHasher.CreateSnapshot(actor, baseline).CanonicalPayloadHash.ToArray(),
            CanonicalCaseRegistrationHasher.CreateSnapshot(actor, CreateRequest(workspaceId: "workspace-2", occurrence: new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero))).CanonicalPayloadHash.ToArray());
        Assert.NotEqual(
            CanonicalCaseRegistrationHasher.CreateSnapshot(actor, baseline).CanonicalPayloadHash.ToArray(),
            CanonicalCaseRegistrationHasher.CreateSnapshot(actor, CreateRequest(countryCatalogueVersion: "catalogue-4", occurrence: new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero))).CanonicalPayloadHash.ToArray());
        Assert.NotEqual(
            CanonicalCaseRegistrationHasher.CreateSnapshot(actor, baseline).CanonicalPayloadHash.ToArray(),
            CanonicalCaseRegistrationHasher.CreateSnapshot(CreateActor("officer-2"), baseline).CanonicalPayloadHash.ToArray());
        Assert.Equal(
            CanonicalCaseRegistrationHasher.CreateSnapshot(actor, baseline).CanonicalPayloadHash.ToArray(),
            CanonicalCaseRegistrationHasher.CreateSnapshot(CreateActor("officer-1", "9"), baseline).CanonicalPayloadHash.ToArray());
        Assert.NotEqual(
            CanonicalCaseRegistrationHasher.CreateSnapshot(actor, baseline).CanonicalPayloadHash.ToArray(),
            CanonicalCaseRegistrationHasher.CreateSnapshot(actor, CreateRequest(narrative: "Different narrative", occurrence: new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero))).CanonicalPayloadHash.ToArray());
        Assert.NotEqual(
            CanonicalCaseRegistrationHasher.CreateSnapshot(actor, baseline).CanonicalPayloadHash.ToArray(),
            CanonicalCaseRegistrationHasher.CreateSnapshot(actor, CreateRequest(confirmedCountryCode: "sz", occurrence: new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero))).CanonicalPayloadHash.ToArray());
    }

    [Fact]
    public void Hash_is_invariant_under_current_culture_and_exposes_no_canonical_plaintext()
    {
        var request = CreateRequest(occurrence: new DateTimeOffset(2026, 10, 27, 12, 34, 56, TimeSpan.Zero));
        var actor = CreateActor("officer-1");
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            var french = CanonicalCaseRegistrationHasher.CreateSnapshot(actor, request);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-EG");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ar-EG");
            var arabic = CanonicalCaseRegistrationHasher.CreateSnapshot(actor, request);

            Assert.Equal(french.CanonicalPayloadHash.ToArray(), arabic.CanonicalPayloadHash.ToArray());
            Assert.DoesNotContain("naïve", french.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("narrative-1", french.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    [Fact]
    public void Unsupported_versions_and_legacy_reports_fail_closed()
    {
        var actor = CreateActor("officer-1");
        var request = CreateRequest();

        Assert.Throws<NotSupportedException>(() => CanonicalCaseRegistrationHasher.CreateSnapshot(actor, request, receiptProtocolVersion: 2));
        Assert.Throws<NotSupportedException>(() => CanonicalCaseRegistrationHasher.CreateSnapshot(actor, request, wireCommandSchemaVersion: 2));
        Assert.Throws<ArgumentException>(() => new RegisterManagedCaseRequest(
            CommandId,
            "workspace-1",
            new CaseReportEventData.LegacyV1(new SwiftAid.Contracts.Commands.ReportFacts(
                SwiftAid.Contracts.Commands.CaseKind.Human,
                SwiftAid.Contracts.Commands.Sector.HumanHealth,
                "legacy",
                []))));
    }

    [Fact]
    public void Confirmed_occurrence_country_must_match_report_location_without_rewriting_case()
    {
        Assert.Throws<ArgumentException>(() => CreateRequest(confirmedCountryCode: "US"));
        var request = CreateRequest(confirmedCountryCode: "sz");

        Assert.Equal("SZ", Assert.IsType<CaseReportEventData.HumanV2>(request.Report).Report.Context.Location.CountryCode);
        Assert.Equal("sz", Assert.IsType<CaseReportEventData.HumanV2>(request.Report).Confirmations.OccurrenceCountryCode);
    }

    private static RegisterManagedCaseRequest CreateRequest(
        string workspaceId = "workspace-1",
        string countryCatalogueVersion = "catalogue-3",
        string confirmedCountryCode = "SZ",
        string narrative = "Concern: naïve",
        DateTimeOffset? occurrence = null)
    {
        var source = new ReportProvenance(ReportSourceKind.ReporterObservation, "obs-1");
        var occurrenceValue = occurrence is null
            ? ReportedFact<ReportedTime>.Unknown(source)
            : ReportedFact<ReportedTime>.Known(
                new ReportedTime(occurrence, null, null, null, "+00:00", null, ReportedTimePrecision.Exact),
                source);
        var context = new ReportContext(
            occurrenceValue,
            null,
            null,
            null,
            ReportedTimePrecision.Unknown,
            new LocationContext(
                "SZ",
                new OptionalPlace(null, null, PresenceState.Unknown),
                new OptionalPlace(null, null, PresenceState.Unknown),
                new OptionalPlace(null, null, PresenceState.Unknown),
                null,
                null,
                null,
                LocationSource.Reporter,
                LocationPrecision.Unknown,
                null));
        var report = HumanReport.Create(
            ReportId,
            context,
            ReportedFact<string>.Unknown(source),
            ReportedFact<int>.Unknown(source),
            ReportedFact<string>.Unknown(source),
            new ReportEvidence(
                new ReportedText(narrative, new ReportProvenance(ReportSourceKind.ReporterNarrative, "narrative-1")),
                [],
                null));
        var confirmations = new ReportConfirmations(
            true,
            confirmedCountryCode,
            countryCatalogueVersion,
            true,
            true,
            "policy-intake",
            "2026-10",
            null,
            null,
            null);
        return new RegisterManagedCaseRequest(CommandId, workspaceId, new CaseReportEventData.HumanV2(report, confirmations));
    }

    private static ActorContext CreateActor(string actorId, string accessEpoch = "1") => ActorContext.FromValidatedPrincipal(
        new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", actorId),
            new Claim(ActorContext.AccessEpochClaimType, accessEpoch)
        ],
        authenticationType: "test")));
}
