using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace SwiftAid.Infrastructure.Identity;

/// <summary>SMTP delivery settings. Credentials arrive via configuration or environment; nothing is hardcoded.</summary>
public sealed class SmtpEmailOptions
{
    public bool Enabled { get; set; }

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 587;

    public bool EnableSsl { get; set; } = true;

    public string UserName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string FromAddress { get; set; } = string.Empty;

    public string FromDisplayName { get; set; } = "SwiftAid";
}

/// <summary>Sends account mail through the configured SMTP relay over MailKit. Fails closed when delivery is not configured.</summary>
public sealed class SmtpEmailSender(
    IOptions<SmtpEmailOptions> options,
    ILogger<SmtpEmailSender> logger) : IEmailSender<SwiftAidUser>, IAccountVerificationMailer
{
    private readonly SmtpEmailOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly ILogger<SmtpEmailSender> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public Task SendConfirmationLinkAsync(SwiftAidUser user, string email, string confirmationLink) =>
        SendAsync(email, "Confirm your SwiftAid account", $"Confirm your SwiftAid account by opening this link: {confirmationLink}");

    public Task SendPasswordResetLinkAsync(SwiftAidUser user, string email, string resetLink) =>
        SendAsync(
            email,
            "Reset your SwiftAid password",
            $"Reset your SwiftAid password by opening this link: {resetLink} If you did not request this, ignore this message.");

    public Task SendPasswordResetCodeAsync(SwiftAidUser user, string email, string resetCode) =>
        throw new InvalidOperationException("Password reset codes are not supported; use the reset link.");

    public Task SendVerificationCodeAsync(string email, string code, CancellationToken cancellationToken) =>
        SendAsync(
            email,
            "Your SwiftAid verification code",
            $"Your SwiftAid verification code is: {code} It expires in 10 minutes. If you did not request this, ignore this message.",
            cancellationToken);

    public Task SendSessionReminderAsync(string email, CancellationToken cancellationToken) =>
        SendAsync(
            email,
            "SwiftAid session reminder",
            "A public session you follow is approaching. Open SwiftAid and find it in your sessions.",
            cancellationToken);

    private async Task SendAsync(string toEmail, string subject, string body, CancellationToken cancellationToken = default)
    {
        var smtp = _options;
        if (!smtp.Enabled ||
            string.IsNullOrWhiteSpace(smtp.Host) ||
            string.IsNullOrWhiteSpace(smtp.FromAddress))
        {
            throw new InvalidOperationException("SMTP delivery is not configured.");
        }

        var message = new MimeMessage();
        message.From.Add(string.IsNullOrWhiteSpace(smtp.FromDisplayName)
            ? MailboxAddress.Parse(smtp.FromAddress)
            : new MailboxAddress(smtp.FromDisplayName, smtp.FromAddress));
        message.To.Add(MailboxAddress.Parse(toEmail));
        message.Subject = subject;
        message.Body = new BodyBuilder { TextBody = body }.ToMessageBody();

        using var smtpClient = new SmtpClient();
        var secureSocketOption = smtp.EnableSsl
            ? SecureSocketOptions.SslOnConnect
            : SecureSocketOptions.None;

        await smtpClient.ConnectAsync(smtp.Host, smtp.Port, secureSocketOption, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(smtp.UserName))
        {
            await smtpClient.AuthenticateAsync(smtp.UserName, smtp.Password, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await smtpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await smtpClient.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("Account mail handed to the SMTP relay.");
    }
}
