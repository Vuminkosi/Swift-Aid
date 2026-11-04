using System.Security.Cryptography;
using System.Text;
using SwiftAid.Contracts.Commands;
using SwiftAid.Contracts.Geography;
using SwiftAid.Contracts.Intake;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Application.Intake;

/// <summary>Pure mapping from a sealed intake submission to a managed-case registration report.</summary>
/// <remarks>
/// Guest intake collects no structured clinical facts: human subject reference and
/// concern, animal species, holding, and head counts stay explicitly unknown with reporter
/// provenance, while the reporter narrative and observations are preserved verbatim as
/// evidence. The first original-audio attachment, when present, is linked by its stable
/// intake reference with original-audio provenance; additional originals remain on the
/// intake record for follow-up evidence handling because a managed report carries a
/// single original-audio reference. Report and command identifiers derive
/// deterministically from the receipt identifier so every retry of the same promotion
/// hashes identically and replays instead of duplicating the case.
/// </remarks>
public static class IntakePromotionMapper
{
    public static CaseReportEventData ToReport(Guid receiptId, IntakeSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (receiptId == Guid.Empty)
        {
            throw new ArgumentException("A receipt identifier is required.", nameof(receiptId));
        }

        return ToCapturedReport(IntakePromotionIds.ReportId(receiptId), $"intake:{receiptId:N}", submission);
    }

    /// <summary>Maps source-attributed capture without claiming that an import is a promoted intake receipt.</summary>
    public static CaseReportEventData ToCapturedReport(Guid reportId, string sourcePrefix, IntakeSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (reportId == Guid.Empty || string.IsNullOrWhiteSpace(sourcePrefix) || sourcePrefix.Length > 400)
            throw new ArgumentException("A stable report and source reference are required.");
        if (submission.AgeYears < 0 ||
            (submission.AgeYears.HasValue && submission.SubjectScope != IntakeSubjectScope.Human) ||
            (submission.Gender is { } gender && (!Enum.IsDefined(gender) || submission.SubjectScope != IntakeSubjectScope.Human)))
            throw new ArgumentException("A reported demographic detail is invalid for this subject.", nameof(submission));
        var context = new ReportContext(
            ToOccurrenceFact(sourcePrefix, submission.Occurrence),
            submission.CapturedAt.Value?.ToUniversalTime(),
            submission.CapturedAt.OriginalOffset,
            submission.CapturedAt.ZoneId,
            submission.CapturedAt.Precision,
            submission.Location);
        var evidence = ToEvidence(sourcePrefix, submission);
        var confirmations = new ReportConfirmations(
            submission.Confirmations.ReportReviewConfirmed,
            submission.Confirmations.OccurrenceCountryCode,
            submission.Confirmations.OccurrenceCountryCatalogueVersion,
            submission.Confirmations.OccurrenceCountryConfirmed,
            submission.Confirmations.ProcessingConfirmed,
            submission.Confirmations.ProcessingPolicyReference,
            submission.Confirmations.ProcessingPolicyVersion,
            submission.Confirmations.RecordingConfirmed,
            submission.Confirmations.RecordingPolicyReference,
            submission.Confirmations.RecordingPolicyVersion);

        CaseReportEventData report = submission.SubjectScope == IntakeSubjectScope.Human
            ? new CaseReportEventData.HumanV2(
                HumanReport.Create(
                    reportId,
                    context,
                    ReportedFact<string>.Unknown(ReporterProvenance(sourcePrefix, "subject-reference")),
                    submission.AgeYears is { } age
                        ? ReportedFact<int>.Known(age, ReporterProvenance(sourcePrefix, "age"))
                        : ReportedFact<int>.Unknown(ReporterProvenance(sourcePrefix, "age")),
                    ReportedFact<string>.Unknown(ReporterProvenance(sourcePrefix, "concern")),
                    evidence),
                confirmations)
            : new CaseReportEventData.AnimalV2(
                AnimalReport.Create(
                    reportId,
                    context,
                    ToAnimalSubjectScope(submission.SubjectScope),
                    ReportedFact<string>.Unknown(ReporterProvenance(sourcePrefix, "species")),
                    ReportedFact<string>.Unknown(ReporterProvenance(sourcePrefix, "holding")),
                    evidence,
                    UnknownCount(ReporterProvenance(sourcePrefix, "animals-at-risk")),
                    UnknownCount(ReporterProvenance(sourcePrefix, "animals-affected")),
                    UnknownCount(ReporterProvenance(sourcePrefix, "animals-dead"))),
                confirmations);

        switch (report)
        {
            case CaseReportEventData.HumanV2 human:
                human.Validate();
                break;
            case CaseReportEventData.AnimalV2 animal:
                animal.Validate();
                break;
            default:
                throw new ArgumentException("Only human or animal intake can be promoted.", nameof(submission));
        }

        return report;
    }

    private static ReportedFact<ReportedTime> ToOccurrenceFact(string sourcePrefix, ReportedTime occurrence)
    {
        var provenance = new ReportProvenance(ReportSourceKind.ReporterObservation, $"{sourcePrefix}:occurrence");
        return occurrence is { Value: null, Precision: ReportedTimePrecision.Unknown }
            ? ReportedFact<ReportedTime>.Unknown(provenance)
            : ReportedFact<ReportedTime>.Known(occurrence, provenance);
    }

    private static ReportEvidence ToEvidence(string sourcePrefix, IntakeSubmission submission)
    {
        ReportedText? narrative = string.IsNullOrWhiteSpace(submission.Narrative)
            ? null
            : new ReportedText(
                submission.Narrative,
                new ReportProvenance(ReportSourceKind.ReporterNarrative, $"{sourcePrefix}:narrative"));
        var observations = submission.Observations
            .Select((text, index) => new ReportedText(
                text,
                new ReportProvenance(ReportSourceKind.ReporterObservation, $"{sourcePrefix}:observation:{index}")))
            .ToList();
        if (submission.Gender is { } gender)
            observations.Add(new ReportedText($"Reported gender: {gender}", ReporterProvenance(sourcePrefix, "gender")));
        ReportEvidenceReference? originalAudio = submission.OriginalAudioAttachmentIds.Count == 0
            ? null
            : new ReportEvidenceReference(
                $"intake-attachment:{submission.OriginalAudioAttachmentIds[0]:D}",
                new ReportProvenance(ReportSourceKind.OriginalAudio, $"{sourcePrefix}:audio"));
        return new ReportEvidence(narrative, observations, originalAudio);
    }

    private static ReportProvenance ReporterProvenance(string sourcePrefix, string name) =>
        new(ReportSourceKind.ReporterObservation, $"{sourcePrefix}:{name}");

    private static AnimalCount UnknownCount(ReportProvenance provenance) =>
        new(ReportedValue<int>.Unknown, provenance, null, null);

    private static AnimalSubjectScope ToAnimalSubjectScope(IntakeSubjectScope scope) => scope switch
    {
        IntakeSubjectScope.Individual => AnimalSubjectScope.Individual,
        IntakeSubjectScope.Herd => AnimalSubjectScope.Herd,
        IntakeSubjectScope.Flock => AnimalSubjectScope.Flock,
        IntakeSubjectScope.OtherGroup => AnimalSubjectScope.OtherGroup,
        _ => throw new ArgumentException("A human intake cannot be promoted as an animal report.", nameof(scope)),
    };
}

/// <summary>Deterministic promotion identifiers derived from the intake receipt identifier.</summary>
public static class IntakePromotionIds
{
    public static Guid CommandId(Guid receiptId) => Derive(receiptId, "command");

    public static Guid ReportId(Guid receiptId) => Derive(receiptId, "report");

    private static Guid Derive(Guid receiptId, string purpose)
    {
        if (receiptId == Guid.Empty)
        {
            throw new ArgumentException("A receipt identifier is required.", nameof(receiptId));
        }

        var input = Encoding.UTF8.GetBytes($"SwiftAid.Intake.Promotion.v1:{purpose}:{receiptId:N}");
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(input, digest);
        return new Guid(digest[..16]);
    }
}
