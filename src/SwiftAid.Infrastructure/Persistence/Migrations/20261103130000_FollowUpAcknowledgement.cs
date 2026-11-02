using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FollowUpAcknowledgement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_managed_case_follow_ups_status",
                table: "managed_case_follow_ups");

            migrationBuilder.DropCheckConstraint(
                name: "ck_case_events_type",
                table: "case_events");

            migrationBuilder.AddCheckConstraint(
                name: "ck_managed_case_follow_ups_status",
                table: "managed_case_follow_ups",
                sql: "status IN ('Scheduled', 'Completed', 'Missed', 'Unreachable', 'Cancelled', 'Acknowledged')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_case_events_type",
                table: "case_events",
                sql: "event_type IN ('CaseReported', 'CaseAssigned', 'CaseReportCorrected', 'CaseActionRequested', 'CaseActionRequestReviewed', 'CaseResolved', 'CaseReopened', 'CaseObservationRecorded', 'CaseFollowUpScheduled', 'CaseFollowUpAcknowledged', 'CaseFollowUpOutcomeRecorded', 'CaseEscalationRaised', 'CaseEscalationAcknowledged', 'CaseEscalationClosed', 'CaseAssignmentAcknowledged', 'CaseCollaboratorGranted', 'CaseCollaboratorRevoked', 'AssistanceRequested', 'AssistanceAccepted', 'AssistanceProgressRecorded', 'AssistanceProvided', 'AssistanceReceiptConfirmed', 'AssistanceUnfulfilled', 'AssistanceCancelled')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_managed_case_follow_ups_status",
                table: "managed_case_follow_ups");

            migrationBuilder.DropCheckConstraint(
                name: "ck_case_events_type",
                table: "case_events");

            migrationBuilder.AddCheckConstraint(
                name: "ck_managed_case_follow_ups_status",
                table: "managed_case_follow_ups",
                sql: "status IN ('Scheduled', 'Completed', 'Missed', 'Unreachable', 'Cancelled')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_case_events_type",
                table: "case_events",
                sql: "event_type IN ('CaseReported', 'CaseAssigned', 'CaseReportCorrected', 'CaseActionRequested', 'CaseActionRequestReviewed', 'CaseResolved', 'CaseReopened', 'CaseObservationRecorded', 'CaseFollowUpScheduled', 'CaseFollowUpOutcomeRecorded', 'CaseEscalationRaised', 'CaseEscalationAcknowledged', 'CaseEscalationClosed', 'CaseAssignmentAcknowledged', 'CaseCollaboratorGranted', 'CaseCollaboratorRevoked', 'AssistanceRequested', 'AssistanceAccepted', 'AssistanceProgressRecorded', 'AssistanceProvided', 'AssistanceReceiptConfirmed', 'AssistanceUnfulfilled', 'AssistanceCancelled')");
        }
    }
}
