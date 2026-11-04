namespace SwiftAid.Application.Cases.Commands;

/// <summary>Optional absolute deadline for read leases created by an isolated operation.</summary>
public static class CaseAuthorityReadDeadline
{
    private static readonly AsyncLocal<DateTimeOffset?> Deadline = new();

    public static DateTimeOffset? Current => Deadline.Value;

    public static IDisposable Begin(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(duration));
        var previous = Deadline.Value;
        var requested = DateTimeOffset.UtcNow.Add(duration);
        Deadline.Value = previous is { } existing && existing < requested ? existing : requested;
        return new Scope(previous);
    }

    private sealed class Scope(DateTimeOffset? previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Deadline.Value = previous;
        }
    }
}
