using System.Reflection;
using SwiftAid.Contracts.Cases;
using SwiftAid.UI.Components.Cases;

namespace SwiftAid.Ui.Tests;

public sealed class CaseTimelineAssistanceLabelTests
{
    [Theory]
    [InlineData("AssistanceRequested", "Assistance requested")]
    [InlineData("AssistanceAccepted", "Assistance responsibility accepted")]
    [InlineData("AssistanceProgressRecorded", "Assistance progress recorded")]
    [InlineData("AssistanceProvided", "Assistance provided; receipt unconfirmed")]
    [InlineData("AssistanceReceiptConfirmed", "Assistance receipt confirmed")]
    [InlineData("AssistanceUnfulfilled", "Assistance not fulfilled")]
    [InlineData("AssistanceCancelled", "Assistance request cancelled")]
    public void Assistance_events_have_explicit_labels_and_category(string eventType, string expectedLabel)
    {
        var label = InvokeTimelineMapper("SafeEventLabel", eventType);
        var category = InvokeTimelineMapper("EventCategory", eventType);

        Assert.Equal(expectedLabel, label);
        Assert.Equal("Assistance", category);
    }

    private static string InvokeTimelineMapper(string name, string eventType)
    {
        var method = typeof(CaseTimelineView).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<string>(method!.Invoke(null, [eventType, CaseTimelineEntryKind.Authoritative]));
    }
}
