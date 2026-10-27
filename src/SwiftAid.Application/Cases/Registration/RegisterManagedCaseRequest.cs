using SwiftAid.Domain.Cases;

namespace SwiftAid.Application.Cases.Registration;

/// <summary>Trusted-workspace staff registration input; identity and initial owner come from the server actor.</summary>
public sealed record RegisterManagedCaseRequest
{
    public RegisterManagedCaseRequest(Guid commandId, string workspaceId, CaseReportEventData report)
    {
        if (commandId == Guid.Empty)
        {
            throw new ArgumentException("A stable command identifier is required.", nameof(commandId));
        }

        if (string.IsNullOrWhiteSpace(workspaceId) || !string.Equals(workspaceId, workspaceId.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("A canonical workspace identifier is required.", nameof(workspaceId));
        }

        ArgumentNullException.ThrowIfNull(report);
        var (reportCountry, confirmations, originalAudioPresent) = report switch
        {
            CaseReportEventData.HumanV2 human => (
                GetHumanCountry(human),
                human.Confirmations ?? throw new ArgumentNullException(nameof(report)),
                human.Report.Evidence.OriginalAudio is not null),
            CaseReportEventData.AnimalV2 animal => (
                GetAnimalCountry(animal),
                animal.Confirmations ?? throw new ArgumentNullException(nameof(report)),
                animal.Report.Evidence.OriginalAudio is not null),
            CaseReportEventData.LegacyV1 => throw new ArgumentException("New managed registrations require a typed human or animal report.", nameof(report)),
            _ => throw new ArgumentException("The report variant is not supported for new managed registration.", nameof(report))
        };

        if (string.IsNullOrWhiteSpace(reportCountry) ||
            !string.Equals(reportCountry, confirmations.OccurrenceCountryCode, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The confirmed occurrence country must match the report's occurrence country.", nameof(report));
        }

        confirmations.Validate(originalAudioPresent);

        CommandId = commandId;
        WorkspaceId = workspaceId;
        Report = report;
    }

    public Guid CommandId { get; }

    /// <summary>A scope hint checked against the authenticated actor's current grant.</summary>
    public string WorkspaceId { get; }

    /// <summary>Immutable typed report data; confirmations are carried by the HumanV2/AnimalV2 variant.</summary>
    public CaseReportEventData Report { get; }

    private static string GetHumanCountry(CaseReportEventData.HumanV2 report)
    {
        ArgumentNullException.ThrowIfNull(report.Report);
        ArgumentNullException.ThrowIfNull(report.Report.Context);
        ArgumentNullException.ThrowIfNull(report.Report.Context.Location);
        return report.Report.Context.Location.CountryCode;
    }

    private static string GetAnimalCountry(CaseReportEventData.AnimalV2 report)
    {
        ArgumentNullException.ThrowIfNull(report.Report);
        ArgumentNullException.ThrowIfNull(report.Report.Context);
        ArgumentNullException.ThrowIfNull(report.Report.Context.Location);
        return report.Report.Context.Location.CountryCode;
    }
}
