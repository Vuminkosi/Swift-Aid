using System.Net.Http;
using SwiftAid.Client.Presentation;

namespace SwiftAid.Client.Tests;

public sealed class ClientRequestFailureTests
{
    [Fact]
    public void TimeoutMapsToTimeoutCopy()
    {
        var message = ClientRequestFailure.MessageFor(new TaskCanceledException(), "Email change");

        Assert.Contains("timed out", message, StringComparison.Ordinal);
        Assert.Contains("Check your connection", message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnreachableHostMapsToUnreachableCopy()
    {
        var message = ClientRequestFailure.MessageFor(new HttpRequestException(), "Redeeming the invitation");

        Assert.Contains("could not be reached", message, StringComparison.Ordinal);
        Assert.Contains("Check your connection", message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownFailureMapsToGenericCopy()
    {
        var message = ClientRequestFailure.MessageFor(new InvalidOperationException(), "Email change");

        Assert.Contains("temporarily unavailable", message, StringComparison.Ordinal);
    }
}
