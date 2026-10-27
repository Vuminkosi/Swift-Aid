using System.Text;
using System.Text.Json;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Notifications;
using SwiftAid.Contracts.Notifications;

namespace SwiftAid.Gateway.Notifications;

/// <summary>Adapts the authorized PostgreSQL notification query and device store to Gateway HTTP.</summary>
public sealed class NotificationApiService : INotificationApiService
{
    private const int MaximumPageSize = 100;
    private const int MaximumTokenLength = 4096;
    private const int MaximumContinuationTokenLength = 128;

    private readonly INotificationItemQuery _items;
    private readonly IPushDeviceStore _devices;
    private readonly TimeProvider _timeProvider;

    public NotificationApiService(
        INotificationItemQuery items,
        IPushDeviceStore devices,
        TimeProvider timeProvider)
    {
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async ValueTask<NotificationFeedPage> GetAsync(
        ActorContext actor,
        int pageSize,
        string? continuationToken,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (pageSize is < 1 or > MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        var pageNumber = DecodePageNumber(continuationToken, pageSize);
        var page = await _items.GetPageAsync(
            actor,
            pageNumber,
            pageSize,
            includeDismissed: true,
            cancellationToken).ConfigureAwait(false);

        var hasMore = checked(page.Page * page.PageSize) < page.TotalCount;
        var nextContinuationToken = hasMore
            ? EncodePageNumber(checked(page.Page + 1), pageSize)
            : null;

        return new NotificationFeedPage(
            page.Items,
            nextContinuationToken,
            page.TotalCount,
            page.UnreadCount);
    }

    public async ValueTask<NotificationMutationStatus> MarkReadAsync(
        ActorContext actor,
        Guid notificationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (notificationId == Guid.Empty)
        {
            return NotificationMutationStatus.NotFoundOrNotAuthorized;
        }

        return await _items.MarkReadAsync(
            actor,
            notificationId,
            _timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false)
            ? NotificationMutationStatus.Applied
            : NotificationMutationStatus.NotFoundOrNotAuthorized;
    }

    public async ValueTask<NotificationMutationStatus> DismissAsync(
        ActorContext actor,
        Guid notificationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (notificationId == Guid.Empty)
        {
            return NotificationMutationStatus.NotFoundOrNotAuthorized;
        }

        return await _items.DismissAsync(
            actor,
            notificationId,
            _timeProvider.GetUtcNow(),
            cancellationToken).ConfigureAwait(false)
            ? NotificationMutationStatus.Applied
            : NotificationMutationStatus.NotFoundOrNotAuthorized;
    }

    public ValueTask<PushDeviceRegistrationStatus> RegisterDeviceAsync(
        ActorContext actor,
        PushDeviceRegistrationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        if (request.InstallationId == Guid.Empty ||
            !Enum.IsDefined(request.Platform) ||
            string.IsNullOrWhiteSpace(request.Token) ||
            request.Token.Length > MaximumTokenLength ||
            !string.Equals(request.Token, request.Token.Trim(), StringComparison.Ordinal))
        {
            return ValueTask.FromResult(PushDeviceRegistrationStatus.Invalid);
        }

        return _devices.RegisterAsync(actor, request, _timeProvider.GetUtcNow(), cancellationToken);
    }

    public ValueTask<PushDeviceRemovalStatus> RemoveDeviceAsync(
        ActorContext actor,
        Guid installationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (installationId == Guid.Empty)
        {
            return ValueTask.FromResult(PushDeviceRemovalStatus.NotFoundOrNotAuthorized);
        }

        return _devices.RevokeAsync(actor, installationId, cancellationToken);
    }

    private static int DecodePageNumber(string? token, int pageSize)
    {
        if (string.IsNullOrEmpty(token))
        {
            return 1;
        }

        if (token.Length > MaximumContinuationTokenLength)
        {
            throw new ArgumentException("The continuation token is invalid.", nameof(token));
        }

        try
        {
            var base64 = token.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight((base64.Length + 3) / 4 * 4, '=');
            var payload = JsonSerializer.Deserialize<NotificationPageCursor>(Convert.FromBase64String(base64));
            if (payload is null || payload.Page < 2 || payload.PageSize != pageSize ||
                payload.Page > int.MaxValue / pageSize)
            {
                throw new ArgumentException("The continuation token is invalid.", nameof(token));
            }

            return payload.Page;
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("The continuation token is invalid.", nameof(token), exception);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("The continuation token is invalid.", nameof(token), exception);
        }
    }

    private static string EncodePageNumber(int page, int pageSize)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new NotificationPageCursor(page, pageSize));
        return Convert.ToBase64String(payload)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private sealed record NotificationPageCursor(int Page, int PageSize);
}
