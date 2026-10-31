namespace SwiftAid.Client.Presentation;

public enum UiScenarioFeature
{
    Education,
    Subscriptions,
    Analytics,
    Map,
    Organization,
    Investigation,
    DemoJourneys
}

public enum UiScenarioFault
{
    None,
    Offline,
    Denied,
    Retryable,
    Conflict
}

public enum UiScenarioAction
{
    ClearFault,
    SimulateOffline,
    SimulateDenied,
    SimulateRetryableFailure,
    SimulateConflict,
    Retry
}

public sealed record UiScenarioControlResult(
    UiCapabilityResultCode Code,
    UiScenarioFeature Feature,
    UiScenarioFault Fault,
    UiCapabilityMetadata Metadata,
    string Message);

/// <summary>Controls isolated deterministic UI fixtures; it does not authorize or call application services.</summary>
public interface IUiScenarioClient
{
    Task<UiScenarioControlResult> GetStateAsync(
        UiScenarioFeature feature,
        CancellationToken cancellationToken = default);

    Task<UiScenarioControlResult> ExecuteAsync(
        UiScenarioFeature feature,
        UiScenarioAction action,
        CancellationToken cancellationToken = default);
}
