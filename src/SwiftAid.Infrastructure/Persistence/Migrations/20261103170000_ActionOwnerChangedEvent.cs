using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations;

[DbContext(typeof(SwiftAidDbContext))]
[Migration("20261103170000_ActionOwnerChangedEvent")]
public sealed class ActionOwnerChangedEvent : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint("ck_case_events_type", "case_events");
        migrationBuilder.AddCheckConstraint("ck_case_events_type", "case_events",
            "event_type IN ('CaseReported', 'CaseAssigned', 'CaseReportCorrected', 'CaseActionRequested', 'CaseActionRequestReviewed', 'CaseResolved', 'CaseReopened', 'CaseObservationRecorded', 'CaseFollowUpScheduled', 'CaseFollowUpAcknowledged', 'CaseFollowUpOutcomeRecorded', 'CaseEscalationRaised', 'CaseEscalationAcknowledged', 'CaseEscalationClosed', 'CaseAssignmentAcknowledged', 'CaseCollaboratorGranted', 'CaseCollaboratorRevoked', 'AssistanceRequested', 'AssistanceAccepted', 'AssistanceProgressRecorded', 'AssistanceProvided', 'AssistanceReceiptConfirmed', 'AssistanceUnfulfilled', 'AssistanceCancelled', 'ActionOwnerChanged')");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint("ck_case_events_type", "case_events");
        migrationBuilder.AddCheckConstraint("ck_case_events_type", "case_events",
            "event_type IN ('CaseReported', 'CaseAssigned', 'CaseReportCorrected', 'CaseActionRequested', 'CaseActionRequestReviewed', 'CaseResolved', 'CaseReopened', 'CaseObservationRecorded', 'CaseFollowUpScheduled', 'CaseFollowUpAcknowledged', 'CaseFollowUpOutcomeRecorded', 'CaseEscalationRaised', 'CaseEscalationAcknowledged', 'CaseEscalationClosed', 'CaseAssignmentAcknowledged', 'CaseCollaboratorGranted', 'CaseCollaboratorRevoked', 'AssistanceRequested', 'AssistanceAccepted', 'AssistanceProgressRecorded', 'AssistanceProvided', 'AssistanceReceiptConfirmed', 'AssistanceUnfulfilled', 'AssistanceCancelled')");
    }
}
