using Microsoft.Maui.Networking;

namespace SwiftAid.Mobile.Services.Connectivity;

public enum MobileConnectivityStatus
{
    Offline,
    InternetAvailable,
    CheckingGateway,
    GatewayUnavailable,
    GatewayReachable
}

public sealed record MobileConnectivitySnapshot(
    MobileConnectivityStatus Status,
    DateTimeOffset CheckedAtUtc,
    string Message);

public interface IMobileConnectivityStatusService
{
    event EventHandler<MobileConnectivitySnapshot>? StateChanged;

    MobileConnectivitySnapshot Current { get; }

    Task<MobileConnectivitySnapshot> CheckGatewayAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Reports device connectivity separately from Gateway reachability. Neither state represents
/// receipt, synchronization, or server acceptance of a report.
/// </summary>
public sealed class MobileConnectivityStatusService : IMobileConnectivityStatusService, IDisposable
{
    private const string GatewayHealthPath = "/health/live";
    private readonly IConnectivity _connectivity;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private MobileConnectivitySnapshot _current;
    private bool _disposed;

    public MobileConnectivityStatusService(
        IConnectivity connectivity,
        IHttpClientFactory httpClientFactory,
        TimeProvider? timeProvider = null)
    {
        _connectivity = connectivity ?? throw new ArgumentNullException(nameof(connectivity));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _current = CreateSnapshot(ReadNetworkStatus());
        _connectivity.ConnectivityChanged += OnConnectivityChanged;
    }

    public event EventHandler<MobileConnectivitySnapshot>? StateChanged;

    public MobileConnectivitySnapshot Current => _current;

    public async Task<MobileConnectivitySnapshot> CheckGatewayAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _checkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var networkStatus = ReadNetworkStatus();
            if (networkStatus == MobileConnectivityStatus.Offline)
            {
                return Publish(MobileConnectivityStatus.Offline);
            }

            Publish(MobileConnectivityStatus.CheckingGateway);
            var client = _httpClientFactory.CreateClient("SwiftAid.NotificationApi");
            if (client.BaseAddress is null)
            {
                return Publish(MobileConnectivityStatus.GatewayUnavailable);
            }

            try
            {
                using var response = await client.GetAsync(GatewayHealthPath, cancellationToken).ConfigureAwait(false);
                return Publish(response.IsSuccessStatusCode
                    ? MobileConnectivityStatus.GatewayReachable
                    : MobileConnectivityStatus.GatewayUnavailable);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Publish(MobileConnectivityStatus.InternetAvailable);
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
            {
                return Publish(MobileConnectivityStatus.GatewayUnavailable);
            }
        }
        finally
        {
            _checkGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connectivity.ConnectivityChanged -= OnConnectivityChanged;
        _checkGate.Dispose();
    }

    private void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs eventArgs)
    {
        var status = eventArgs.NetworkAccess == NetworkAccess.Internet
            ? MobileConnectivityStatus.InternetAvailable
            : MobileConnectivityStatus.Offline;
        Publish(status);
    }

    private MobileConnectivityStatus ReadNetworkStatus() =>
        _connectivity.NetworkAccess == NetworkAccess.Internet
            ? MobileConnectivityStatus.InternetAvailable
            : MobileConnectivityStatus.Offline;

    private MobileConnectivitySnapshot Publish(MobileConnectivityStatus status)
    {
        var snapshot = CreateSnapshot(status);
        _current = snapshot;
        MainThread.BeginInvokeOnMainThread(() => StateChanged?.Invoke(this, snapshot));
        return snapshot;
    }

    private MobileConnectivitySnapshot CreateSnapshot(MobileConnectivityStatus status)
    {
        var message = status switch
        {
            MobileConnectivityStatus.Offline => "The device has no internet connection. Drafts can still be saved privately on this device.",
            MobileConnectivityStatus.InternetAvailable => "Internet access is available. SwiftAid has not yet confirmed receipt of any report.",
            MobileConnectivityStatus.CheckingGateway => "Checking whether SwiftAid is reachable. Your reports remain on this device until a receipt confirms they were received.",
            MobileConnectivityStatus.GatewayUnavailable => "SwiftAid could not be reached. Drafts remain saved on this device and have not been received by the server.",
            MobileConnectivityStatus.GatewayReachable => "SwiftAid is reachable. Reports remain pending until a server receipt confirms they were received.",
            _ => "Connection status is unavailable. Drafts remain on this device."
        };

        return new MobileConnectivitySnapshot(status, _timeProvider.GetUtcNow(), message);
    }
}
