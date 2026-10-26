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
}
