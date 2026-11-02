using System.Text.Json;
using SwiftAid.Contracts.Ellai;
using SwiftAid.Contracts.Intake;
using SwiftAid.UI.Components.Public;

namespace SwiftAid.Ui.Tests;

public sealed class EllaiReceiptSemanticsTests
{
    [Fact]
    public void ReceiptSurfaceReportsPresenceWithoutPrivateIdentifiersOrCompletionProof()
    {
        var component = new PublicReportReceipt();
        Assert.Equal(UiObservationState.Unmounted, Assert.Single(component.GetState().Controls).State);
        var id = Guid.NewGuid();
        var receipt = new IntakeReceipt(id, Guid.NewGuid(), IntakeStatus.ReceivedUnverified,
            IntakeRoutingStatus.NeedsRouting, DateTimeOffset.UtcNow);
        typeof(PublicReportReceipt).GetProperty(nameof(component.Receipt))!.SetValue(component, receipt);
        var state = component.GetState();
        Assert.Equal(UiObservationState.Ready, Assert.Single(state.Controls).State);
        Assert.DoesNotContain(id.ToString(), JsonSerializer.Serialize(state));
        Assert.DoesNotContain(id.ToString(), JsonSerializer.Serialize(component.Describe()));
        Assert.Equal("intake-receipt-accepted", Assert.Single(component.Describe().Controls).ProofPolicyId);
    }
}
