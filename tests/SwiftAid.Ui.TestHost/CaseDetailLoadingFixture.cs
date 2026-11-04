using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Ui.TestHost;

public sealed class CaseDetailLoadingFixture : ICaseReportDetailsClient, ICaseObservationClient
{
    public static readonly Guid FirstCase = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    public static readonly Guid SecondCase = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private readonly TaskCompletionSource<UiCapabilityResult<CaseObservationSnapshot>> _observations =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<UiCapabilityResult<CaseReportDetails>> GetAsync(Guid caseId, CancellationToken cancellationToken = default)
    {
        if (caseId == SecondCase) throw new HttpRequestException("Synthetic failure");
        var report = new CaseReportDetails(caseId, 1, "Synthetic first report", [], null);
        return Task.FromResult(new UiCapabilityResult<CaseReportDetails>(UiCapabilityResultCode.Success, report,
            UiCapabilityResults.SyntheticMetadata("report", "Fixture")));
    }

    public Task<UiCapabilityResult<CaseObservationSnapshot>> GetAsync(Guid caseId, PageRequest page, CancellationToken cancellationToken = default) =>
        _observations.Task.WaitAsync(cancellationToken);

    public Task<UiCapabilityResult<CaseObservationWriteResult>> RecordAsync(CaseObservationCommand command, CancellationToken cancellationToken = default) =>
        Task.FromResult(UiCapabilityResults.Unavailable<CaseObservationWriteResult>("fixture"));

    public void Release() => _observations.TrySetResult(UiCapabilityResults.Unavailable<CaseObservationSnapshot>("fixture"));
}
