using SwiftAid.Contracts.Cases;

namespace SwiftAid.Browser.Services;

/// <summary>Fail-closed adapter required by the shared guest home component; it never reads staff cases.</summary>
public sealed class BrowserUnavailableCaseQueueClient : ICaseQueueClient
{
    public Task<CaseQueueResult> GetMineAsync(int? limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new CaseQueueResult(CaseQueueAccess.Denied, []));
    }
}
