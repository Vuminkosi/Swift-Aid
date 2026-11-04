namespace SwiftAid.Gateway.Notifications;

/// <summary>External session reminder delivery is off unless a channel is explicitly enabled.</summary>
public sealed class SessionReminderDeliveryOptions
{
    public bool Enabled { get; set; }

    public bool EmailEnabled { get; set; }

    public bool MobilePushEnabled { get; set; }
}
