using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CaseDiscussionJournal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_case_events_type",
                table: "case_events");

            migrationBuilder.AddCheckConstraint(
                name: "ck_case_events_type",
                table: "case_events",
                sql: "event_type IN ('CaseReported', 'CaseAssigned', 'CaseReportCorrected', 'CaseActionRequested', 'CaseActionRequestReviewed', 'CaseResolved', 'CaseReopened', 'CaseObservationRecorded', 'CaseFollowUpScheduled', 'CaseFollowUpAcknowledged', 'CaseFollowUpOutcomeRecorded', 'CaseEscalationRaised', 'CaseEscalationAcknowledged', 'CaseEscalationActionRecorded', 'CaseEscalationClosed', 'CaseAssignmentAcknowledged', 'CaseCollaboratorGranted', 'CaseCollaboratorRevoked', 'AssistanceRequested', 'AssistanceAccepted', 'AssistanceProgressRecorded', 'AssistanceProvided', 'AssistanceReceiptConfirmed', 'AssistanceUnfulfilled', 'AssistanceCancelled', 'ActionOwnerChanged', 'CaseDiscussionPosted')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_case_events_type",
                table: "case_events");

            migrationBuilder.AddCheckConstraint(
                name: "ck_case_events_type",
                table: "case_events",
                sql: "event_type IN ('CaseReported', 'CaseAssigned', 'CaseReportCorrected', 'CaseActionRequested', 'CaseActionRequestReviewed', 'CaseResolved', 'CaseReopened', 'CaseObservationRecorded', 'CaseFollowUpScheduled', 'CaseFollowUpAcknowledged', 'CaseFollowUpOutcomeRecorded', 'CaseEscalationRaised', 'CaseEscalationAcknowledged', 'CaseEscalationActionRecorded', 'CaseEscalationClosed', 'CaseAssignmentAcknowledged', 'CaseCollaboratorGranted', 'CaseCollaboratorRevoked', 'AssistanceRequested', 'AssistanceAccepted', 'AssistanceProgressRecorded', 'AssistanceProvided', 'AssistanceReceiptConfirmed', 'AssistanceUnfulfilled', 'AssistanceCancelled', 'ActionOwnerChanged')");
        }
    }
}
