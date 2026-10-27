namespace SwiftAid.Domain.Cases;

/// <summary>A report event payload that preserves legacy facts or a complete typed v2 report.</summary>
public abstract record CaseReportEventData
{
    private CaseReportEventData() { }

    public sealed record LegacyV1(SwiftAid.Contracts.Commands.ReportFacts Report) : CaseReportEventData;

    public sealed record HumanV2(HumanReport Report, ReportConfirmations Confirmations) : CaseReportEventData
    {
        public void Validate()
        {
            ArgumentNullException.ThrowIfNull(Report);
            ArgumentNullException.ThrowIfNull(Confirmations);
            Confirmations.Validate(Report.Evidence.OriginalAudio is not null);
        }
    }

    public sealed record AnimalV2(AnimalReport Report, ReportConfirmations Confirmations) : CaseReportEventData
    {
        public void Validate()
        {
            ArgumentNullException.ThrowIfNull(Report);
            ArgumentNullException.ThrowIfNull(Confirmations);
            Confirmations.Validate(Report.Evidence.OriginalAudio is not null);
        }
    }
}
