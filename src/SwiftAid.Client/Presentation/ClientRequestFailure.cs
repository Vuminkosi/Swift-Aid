using System.Net.Http;

namespace SwiftAid.Client.Presentation;

/// <summary>Maps transport failures to truthful retry copy without inventing new result codes.
/// Timeouts and unreachable hosts get distinct messages; everything else stays generic.</summary>
public static class ClientRequestFailure
{
    public static string MessageFor(Exception exception, string action) => exception switch
    {
        TaskCanceledException or TimeoutException =>
            $"{action} timed out. Check your connection and try again.",
        HttpRequestException =>
            $"{action} could not be reached. Check your connection and try again.",
        _ => $"{action} is temporarily unavailable. Try again later.",
    };
}
