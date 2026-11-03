using System.Text;

namespace SwiftAid.Domain.Sessions;

/// <summary>
/// Builds the public calendar file and the manual copy-invitation text for a community
/// session. Logistics only: title, time, duration, presenter, destination. No clinical
/// content, no attendee data, no tracking.
/// </summary>
public static class CommunitySessionSharing
{
    public static string BuildIcs(CommunitySession session, DateTimeOffset generatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Status is not (CommunitySessionStatus.Scheduled or CommunitySessionStatus.Cancelled or CommunitySessionStatus.Completed))
        {
            throw new InvalidOperationException("A calendar file is only available for a published session.");
        }

        var status = session.Status switch
        {
            CommunitySessionStatus.Cancelled => "CANCELLED",
            _ => "CONFIRMED"
        };
        var duration = TimeSpan.FromMinutes(session.DurationMinutes);
        var builder = new StringBuilder();
        builder.AppendLine("BEGIN:VCALENDAR");
        builder.AppendLine("VERSION:2.0");
        builder.AppendLine("PRODID:-//SwiftAid//Community Session//EN");
        builder.AppendLine("BEGIN:VEVENT");
        AppendFolded(builder, "UID", $"swiftaid-community-session-{session.SessionId:D}");
        AppendFolded(builder, "DTSTAMP", FormatUtc(generatedAtUtc));
        AppendFolded(builder, "DTSTART", FormatUtc(session.StartUtc));
        AppendFolded(builder, "DURATION", $"PT{(int)duration.TotalHours}H{duration.Minutes}M");
        AppendFolded(builder, "SUMMARY", session.Title);
        AppendFolded(builder, "DESCRIPTION", Describe(session));
        AppendFolded(builder, "LOCATION", Locate(session));
        if (session.ChannelLink is not null)
        {
            AppendFolded(builder, "URL", session.ChannelLink);
        }

        AppendFolded(builder, "SEQUENCE", session.IcsSequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendFolded(builder, "STATUS", status);
        AppendFolded(builder, "TRANSP", "OPAQUE");
        builder.AppendLine("END:VEVENT");
        builder.Append("END:VCALENDAR\r\n");
        return builder.ToString();
    }

    public static string BuildInvitationText(CommunitySession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Status is not (CommunitySessionStatus.Scheduled or CommunitySessionStatus.Cancelled))
        {
            throw new InvalidOperationException("An invitation is only available for a published session.");
        }

        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(session.TimeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new InvalidOperationException("The session time zone is unknown on this host.", ex);
        }

        var localStart = TimeZoneInfo.ConvertTime(session.StartUtc, zone);
        var builder = new StringBuilder();
        builder.Append("You are invited: ").AppendLine(session.Title);
        builder.Append("When: ").Append(localStart.ToString("f", System.Globalization.CultureInfo.InvariantCulture))
            .Append(" (").Append(session.TimeZoneId).AppendLine(")");
        builder.Append("Duration: ").Append(session.DurationMinutes
            .ToString(System.Globalization.CultureInfo.InvariantCulture)).AppendLine(" minutes");
        builder.Append("Presenter: ").AppendLine(session.Presenter);
        if (session.ChannelLink is not null)
        {
            builder.Append("Join: ").AppendLine(session.ChannelLink);
        }
        else
        {
            builder.Append("Where: ").Append(session.RegionId).Append(", ").AppendLine(session.CountryCode);
        }

        builder.AppendLine(session.Summary);
        if (session.Status == CommunitySessionStatus.Cancelled)
        {
            builder.Append("Status: cancelled. Reason: ").AppendLine(session.CancellationReason ?? "not recorded");
        }

        return builder.ToString();
    }

    private static string Describe(CommunitySession session)
    {
        var builder = new StringBuilder(session.Summary);
        builder.Append("\\nPresenter: ").Append(session.Presenter);
        builder.Append("\\nLanguage: ").Append(session.Language);
        if (session.Status == CommunitySessionStatus.Cancelled)
        {
            builder.Append("\\nStatus: cancelled. Reason: ").Append(session.CancellationReason ?? "not recorded");
        }

        return builder.ToString();
    }

    private static string Locate(CommunitySession session) =>
        session.ChannelLink is not null
            ? $"Online ({session.ChannelType})"
            : $"{session.RegionId}, {session.CountryCode}";

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static void AppendFolded(StringBuilder builder, string name, string value)
    {
        var escaped = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r\n", "\\n", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace(";", "\\;", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal);
        var line = $"{name}:{escaped}";
        var bytes = Encoding.UTF8.GetBytes(line);
        if (bytes.Length <= 75)
        {
            builder.Append(line).Append("\r\n");
            return;
        }

        var first = TakeBytes(bytes, 75);
        builder.Append(Encoding.UTF8.GetString(first)).Append("\r\n");
        var rest = bytes[first.Length..];
        while (rest.Length > 0)
        {
            var chunk = TakeBytes(rest, 74);
            builder.Append(' ').Append(Encoding.UTF8.GetString(chunk)).Append("\r\n");
            rest = rest[chunk.Length..];
        }
    }

    private static byte[] TakeBytes(byte[] source, int max)
    {
        var length = Math.Min(max, source.Length);
        while (length > 0 && (source[length - 1] & 0xC0) == 0x80)
        {
            length--;
        }

        if (length == 0)
        {
            length = Math.Min(max, source.Length);
        }

        return source[..length];
    }
}
