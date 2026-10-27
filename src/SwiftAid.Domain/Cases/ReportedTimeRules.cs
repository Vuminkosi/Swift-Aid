using SwiftAid.Contracts.Geography;

namespace SwiftAid.Domain.Cases;

/// <summary>Validates reporter-supplied time representations and normalizes only explicit instants to UTC.</summary>
public static class ReportedTimeRules
{
    public static ReportedTime Normalize(ReportedTime reportedTime)
    {
        Validate(reportedTime);
        return reportedTime with { Value = reportedTime.Value?.ToUniversalTime() };
    }

    public static ReportedFact<ReportedTime> Normalize(ReportedFact<ReportedTime> reportedTime)
    {
        ArgumentNullException.ThrowIfNull(reportedTime);
        reportedTime.Validate();
        if (!reportedTime.Value.HasValue)
        {
            return reportedTime;
        }

        return reportedTime with
        {
            Value = ReportedValue<ReportedTime>.Known(Normalize(reportedTime.Value.Value))
        };
    }

    public static void Validate(ReportedTime reportedTime)
    {
        ArgumentNullException.ThrowIfNull(reportedTime);
        if (!Enum.IsDefined(reportedTime.Precision))
        {
            throw new ArgumentOutOfRangeException(nameof(reportedTime), "Reported-time precision must be a defined value.");
        }

        var hasInstant = reportedTime.Value.HasValue;
        var hasLocalDate = reportedTime.LocalDate.HasValue;
        var hasRangeStart = reportedTime.RangeStart.HasValue;
        var hasRangeEnd = reportedTime.RangeEnd.HasValue;
        var hasAnyDateRepresentation = hasInstant || hasLocalDate || hasRangeStart || hasRangeEnd;

        switch (reportedTime.Precision)
        {
            case ReportedTimePrecision.Exact when !hasInstant || hasLocalDate || hasRangeStart || hasRangeEnd:
                throw InvalidRepresentation("Exact precision requires exactly one explicit instant.");
            case ReportedTimePrecision.DateOnly when !hasLocalDate || hasInstant || hasRangeStart || hasRangeEnd:
                throw InvalidRepresentation("DateOnly precision requires exactly one local date.");
            case ReportedTimePrecision.Approximate:
                ValidateApproximate(hasInstant, hasLocalDate, hasRangeStart, hasRangeEnd, reportedTime.RangeStart, reportedTime.RangeEnd);
                break;
            case ReportedTimePrecision.Unknown when hasAnyDateRepresentation:
                throw InvalidRepresentation("Unknown precision cannot contain an invented instant, date or range.");
        }

        ValidateOffsetAndZone(reportedTime.Value, reportedTime.OriginalOffset, reportedTime.ZoneId,
            nameof(ReportedTime.OriginalOffset));
    }

    internal static void ValidateOffsetAndZone(
        DateTimeOffset? instant,
        string? offset,
        string? zoneId,
        string offsetParameterName)
    {
        TimeSpan? suppliedOffset = offset is null ? null : ParseOffset(offset, offsetParameterName);
        var timeZone = ResolveZone(zoneId);
        if (instant is DateTimeOffset value && suppliedOffset is TimeSpan expectedOffset && timeZone is not null &&
            timeZone.GetUtcOffset(value.ToUniversalTime()) != expectedOffset)
        {
            throw new ArgumentException("The supplied offset conflicts with the time-zone identifier at the reported instant.", offsetParameterName);
        }
    }

    private static void ValidateApproximate(
        bool hasInstant,
        bool hasLocalDate,
        bool hasRangeStart,
        bool hasRangeEnd,
        DateOnly? rangeStart,
        DateOnly? rangeEnd)
    {
        if (hasRangeStart != hasRangeEnd)
        {
            throw InvalidRepresentation("An approximate date range requires both endpoints.");
        }

        if (hasRangeStart)
        {
            if (hasInstant || hasLocalDate)
            {
                throw InvalidRepresentation("An approximate range cannot compete with an instant or local date.");
            }

            if (rangeStart > rangeEnd)
            {
                throw InvalidRepresentation("An approximate date range must be ordered from start to end.");
            }

            return;
        }

        if (hasInstant == hasLocalDate)
        {
            throw InvalidRepresentation("Approximate precision requires one instant, one local date, or a complete range.");
        }
    }

    private static TimeSpan ParseOffset(string offset, string parameterName)
    {
        if (offset.Length != 6 || (offset[0] != '+' && offset[0] != '-') || offset[3] != ':' ||
            !char.IsAsciiDigit(offset[1]) || !char.IsAsciiDigit(offset[2]) ||
            !char.IsAsciiDigit(offset[4]) || !char.IsAsciiDigit(offset[5]))
        {
            throw new ArgumentException("A reported UTC offset must use numeric ±HH:mm format.", parameterName);
        }

        var hours = (offset[1] - '0') * 10 + offset[2] - '0';
        var minutes = (offset[4] - '0') * 10 + offset[5] - '0';
        if (hours > 14 || minutes > 59 || (hours == 14 && minutes != 0))
        {
            throw new ArgumentOutOfRangeException(parameterName, "A reported UTC offset must be within DateTimeOffset's ±14:00 range.");
        }

        var sign = offset[0] == '-' ? -1 : 1;
        return TimeSpan.FromMinutes(sign * (hours * 60 + minutes));
    }

    private static TimeZoneInfo? ResolveZone(string? zoneId)
    {
        if (zoneId is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(zoneId))
        {
            throw new ArgumentException("A supplied time-zone identifier cannot be blank.", nameof(zoneId));
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        }
        catch (TimeZoneNotFoundException exception)
        {
            throw new ArgumentException("The supplied time-zone identifier is unknown.", nameof(zoneId), exception);
        }
        catch (InvalidTimeZoneException exception)
        {
            throw new ArgumentException("The supplied time-zone identifier is invalid.", nameof(zoneId), exception);
        }
    }

    private static ArgumentException InvalidRepresentation(string message) => new(message, nameof(ReportedTime));
}
