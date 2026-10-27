namespace SwiftAid.Infrastructure.Configuration;

public sealed class NatsOptions
{
    public const string SectionName = "Nats";

    public bool Enabled { get; set; }

    public string Url { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string EventStream { get; set; } = string.Empty;

    public string EventSubjectPrefix { get; set; } = string.Empty;

    public string NotificationStream { get; set; } = string.Empty;

    public string NotificationSubject { get; set; } = string.Empty;

    public int NotificationStreamMaxAgeSeconds { get; set; } = 86_400;

    public long NotificationStreamMaxBytes { get; set; } = 67_108_864;

    public long NotificationStreamMaxMessages { get; set; } = 100_000;

    public int NotificationStreamMaxMessageSizeBytes { get; set; } = 16_384;

    public int NotificationStreamReplicas { get; set; } = 1;

    public int NotificationStreamDuplicateWindowSeconds { get; set; } = 120;

    public int NotificationConsumerAckWaitSeconds { get; set; } = 30;

    public int NotificationConsumerMaxDeliveries { get; set; } = 8;

    public int NotificationConsumerInactiveThresholdSeconds { get; set; } = 120;
}
