using System.Reflection;
using SwiftAid.Contracts.Cases;
using SwiftAid.UI.Components.Cases;

namespace SwiftAid.Ui.Tests;

public sealed class CaseTimelineAssistanceLabelTests
{
    [Fact]
    public void Action_owner_transfer_uses_specific_label_and_category()
    {
        Assert.Equal("Action owner changed", InvokeTimelineMapper("SafeEventLabel", "ActionOwnerChanged"));
        Assert.Equal("Action ownership", InvokeTimelineMapper("EventCategory", "ActionOwnerChanged"));
    }

    [Fact]
    public void Escalation_action_event_uses_specific_label_and_category()
    {
        Assert.Equal("Escalation action recorded", InvokeTimelineMapper("SafeEventLabel", "CaseEscalationActionRecorded"));
        Assert.Equal("Escalation", InvokeTimelineMapper("EventCategory", "CaseEscalationActionRecorded"));
    }

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

public sealed class CaseTimelineFollowUpLabelTests
{
    [Fact]
    public void FollowUpAcknowledgement_has_explicit_label_and_category()
    {
        var type = typeof(CaseTimelineView);
        var labelMapper = type.GetMethod("SafeEventLabel", BindingFlags.NonPublic | BindingFlags.Static);
        var categoryMapper = type.GetMethod("EventCategory", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(labelMapper);
        Assert.NotNull(categoryMapper);
        Assert.Equal("Follow-up acknowledged", labelMapper!.Invoke(null, ["CaseFollowUpAcknowledged", CaseTimelineEntryKind.Authoritative]));
        Assert.Equal("Follow-up", categoryMapper!.Invoke(null, ["CaseFollowUpAcknowledged", CaseTimelineEntryKind.Authoritative]));
    }
}

public sealed class CaseAgentActivityPresentationTests
{
    [Theory]
    [InlineData("ops.quality", "Data Quality", "Reported-data quality assessed")]
    [InlineData("ops.followup", "Follow-up", "Follow-up activity recorded")]
    [InlineData("unknown", "Agent", "Activity recorded")]
    public void ActivityLabelsAreRoleSpecificAndBounded(string roleId, string roleLabel, string activityLabel)
    {
        Assert.Equal(roleLabel, CaseAgentActivityPresentation.RoleLabel(roleId));
        Assert.Equal(activityLabel, CaseAgentActivityPresentation.ActivityLabel(roleId));
    }
}
