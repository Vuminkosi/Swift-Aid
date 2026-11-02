using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SwiftAid.Infrastructure.Persistence;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations;

[DbContext(typeof(SwiftAidDbContext))]
[Migration("20261103153000_CaseEventTypeConstraint")]
public sealed class CaseEventTypeConstraint : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_case_events_type",
            table: "case_events");

        migrationBuilder.AddCheckConstraint(
            name: "ck_case_events_type",
            table: "case_events",
            sql: "event_type IN ('CaseReported', 'CaseAssigned', 'CaseReportCorrected', 'CaseActionRequested', 'CaseActionRequestReviewed', 'CaseResolved', 'CaseReopened', 'CaseObservationRecorded', 'CaseFollowUpScheduled', 'CaseFollowUpOutcomeRecorded', 'CaseEscalationRaised', 'CaseEscalationAcknowledged', 'CaseEscalationClosed', 'CaseAssignmentAcknowledged', 'CaseCollaboratorGranted', 'CaseCollaboratorRevoked', 'AssistanceRequested', 'AssistanceAccepted', 'AssistanceProgressRecorded', 'AssistanceProvided', 'AssistanceReceiptConfirmed', 'AssistanceUnfulfilled', 'AssistanceCancelled', 'CaseFollowUpAcknowledged')");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_case_events_type",
            table: "case_events");

        migrationBuilder.AddCheckConstraint(
            name: "ck_case_events_type",
            table: "case_events",
            sql: "event_type IN ('CaseReported', 'CaseAssigned', 'CaseReportCorrected', 'CaseActionRequested', 'CaseActionRequestReviewed', 'CaseResolved', 'CaseReopened', 'CaseObservationRecorded', 'CaseFollowUpScheduled', 'CaseFollowUpOutcomeRecorded', 'CaseEscalationRaised', 'CaseEscalationAcknowledged', 'CaseEscalationClosed')");
    }
}
