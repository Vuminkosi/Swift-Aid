using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using SwiftAid.Application.Abstractions;
using SwiftAid.Application.Notifications;
using SwiftAid.Application.Sessions;
using SwiftAid.Contracts.Notifications;
using SwiftAid.Infrastructure.Identity;

namespace SwiftAid.Infrastructure.Persistence.Sessions;

/// <summary>Uses only the channel explicitly captured in a current, versioned session subscription.</summary>
public sealed class SessionReminderChannelSender(
    IEventSubscriptionService subscriptions,
    ISessionReminderDeliveryStore deliveries,
    IPushDeviceStore pushDevices,
    IPushNotificationSender pushSender,
    UserManager<SwiftAidUser>? users = null,
    SmtpEmailSender? emailSender = null,
    IOptions<SmtpEmailOptions>? smtpOptions = null) : ISessionReminderChannelSender
{
    private readonly IEventSubscriptionService _subscriptions = subscriptions ?? throw new ArgumentNullException(nameof(subscriptions));
    private readonly ISessionReminderDeliveryStore _deliveries = deliveries ?? throw new ArgumentNullException(nameof(deliveries));
    private readonly UserManager<SwiftAidUser>? _users = users;
    private readonly SmtpEmailSender? _emailSender = emailSender;
    private readonly SmtpEmailOptions? _smtpOptions = smtpOptions?.Value;
    private readonly IPushDeviceStore _pushDevices = pushDevices ?? throw new ArgumentNullException(nameof(pushDevices));
    private readonly IPushNotificationSender _pushSender = pushSender ?? throw new ArgumentNullException(nameof(pushSender));

    public async Task<SessionReminderSendStatus> SendAsync(
        SessionReminderDeliveryLease delivery,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        return delivery.Channel switch
        {
            "Email" => await SendEmailAsync(delivery, cancellationToken).ConfigureAwait(false),
            "MobilePush" => await SendPushAsync(delivery, cancellationToken).ConfigureAwait(false),
            _ => SessionReminderSendStatus.NoRoute
        };
    }

    private async Task<SessionReminderSendStatus> SendEmailAsync(
        SessionReminderDeliveryLease delivery,
        CancellationToken cancellationToken)
    {
        if (_users is null || _emailSender is null || _smtpOptions is null ||
            !_smtpOptions.Enabled || string.IsNullOrWhiteSpace(_smtpOptions.Host) ||
            string.IsNullOrWhiteSpace(_smtpOptions.FromAddress))
            return SessionReminderSendStatus.NotConfigured;

        SwiftAidUser? user;
        try
        {
            user = await _users.FindByIdAsync(delivery.AccountId).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
        {
            throw new SessionReminderPreflightException(exception);
        }

        if (user is not { EmailConfirmed: true } || string.IsNullOrWhiteSpace(user.Email))
            return SessionReminderSendStatus.NoRoute;
        if (!await IsCurrentAsync(delivery, cancellationToken).ConfigureAwait(false))
            return SessionReminderSendStatus.NoRoute;

        // Exceptions from SMTP after invocation are ambiguous and the dispatcher records OutcomeUnknown.
        await _emailSender.SendSessionReminderAsync(user.Email, cancellationToken).ConfigureAwait(false);
        return SessionReminderSendStatus.Accepted;
    }

    private async Task<SessionReminderSendStatus> SendPushAsync(
        SessionReminderDeliveryLease delivery,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<PushDeviceForDelivery> devices;
        try
        {
            devices = await _pushDevices.GetActiveForRecipientAsync(delivery.AccountId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new SessionReminderPreflightException(exception);
        }

        if (devices.Count == 0) return SessionReminderSendStatus.NoRoute;

        var accepted = false;
        foreach (var device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await IsCurrentAsync(delivery, cancellationToken).ConfigureAwait(false))
                return accepted ? SessionReminderSendStatus.OutcomeUnknown : SessionReminderSendStatus.NoRoute;

            IReadOnlyList<PushDeviceForDelivery> currentDevices;
            try
            {
                currentDevices = await _pushDevices.GetActiveForRecipientAsync(delivery.AccountId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                if (accepted) return SessionReminderSendStatus.OutcomeUnknown;
                throw new SessionReminderPreflightException(exception);
            }

            var currentDevice = currentDevices.SingleOrDefault(item => item.InstallationId == device.InstallationId);
            if (currentDevice is null) continue;

            PushNotificationResult result;
            try
            {
                result = await _pushSender.SendAsync(
                    new PushNotificationRequest(
                        currentDevice.Token,
                        PushNotificationKind.PublicSessionReminder,
                        delivery.DeliveryId,
                        currentDevice.Platform,
                        currentDevice.SoundEnabled),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return SessionReminderSendStatus.OutcomeUnknown;
            }

            if (result.Status == PushNotificationStatus.NotConfigured)
                return accepted ? SessionReminderSendStatus.OutcomeUnknown : SessionReminderSendStatus.NotConfigured;
            if (result.Status != PushNotificationStatus.AcceptedByProvider)
                return SessionReminderSendStatus.OutcomeUnknown;
            accepted = true;
        }

        return accepted ? SessionReminderSendStatus.Accepted : SessionReminderSendStatus.NoRoute;
    }

    private async Task<bool> IsCurrentAsync(
        SessionReminderDeliveryLease delivery,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _subscriptions.CanDispatchReminderAsync(
                    delivery.SessionId,
                    delivery.AccountId,
                    delivery.SessionVersion,
                    delivery.ConsentReference,
                    delivery.Channel,
                    cancellationToken,
                    delivery.SubscriptionVersion)
                .ConfigureAwait(false) &&
                await _deliveries.IsLeaseCurrentAsync(delivery.DeliveryId, delivery.LeaseId, cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new SessionReminderPreflightException(exception);
        }
    }
}
