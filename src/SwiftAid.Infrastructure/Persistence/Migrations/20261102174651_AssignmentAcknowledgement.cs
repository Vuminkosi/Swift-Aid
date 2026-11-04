using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AssignmentAcknowledgement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_case_events_type",
                table: "case_events");

            migrationBuilder.DropCheckConstraint(
                name: "ck_grant_group_operations_value",
                table: "access_grant_group_operations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_case_events_type",
                table: "case_events",
                sql: "event_type IN ('CaseReported', 'CaseAssigned', 'CaseReportCorrected', 'CaseActionRequested', 'CaseActionRequestReviewed', 'CaseResolved', 'CaseReopened', 'CaseObservationRecorded', 'CaseFollowUpScheduled', 'CaseFollowUpOutcomeRecorded', 'CaseEscalationRaised', 'CaseEscalationAcknowledged', 'CaseEscalationClosed', 'CaseAssignmentAcknowledged')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_grant_group_operations_value",
                table: "access_grant_group_operations",
                sql: "operation IN ('ReadCase', 'ReadAttachment', 'Synchronize', 'Search', 'Export', 'Count', 'RequestProtectedAction', 'ExecuteProtectedAction', 'ReadLinkedSignalEvidence', 'CorrectReport', 'ReviewProtectedAction', 'AssignCase', 'ResolveCase', 'ReopenCase', 'ReviewIntake', 'CoordinateIntake', 'RecordObservation', 'ManageFollowUp', 'ManageEscalation', 'AcknowledgeAssignment')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_case_events_type",
                table: "case_events");

            migrationBuilder.DropCheckConstraint(
                name: "ck_grant_group_operations_value",
                table: "access_grant_group_operations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_case_events_type",
                table: "case_events",
                sql: "event_type IN ('CaseReported', 'CaseAssigned', 'CaseReportCorrected', 'CaseActionRequested', 'CaseActionRequestReviewed', 'CaseResolved', 'CaseReopened', 'CaseObservationRecorded', 'CaseFollowUpScheduled', 'CaseFollowUpOutcomeRecorded', 'CaseEscalationRaised', 'CaseEscalationAcknowledged', 'CaseEscalationClosed')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_grant_group_operations_value",
                table: "access_grant_group_operations",
                sql: "operation IN ('ReadCase', 'ReadAttachment', 'Synchronize', 'Search', 'Export', 'Count', 'RequestProtectedAction', 'ExecuteProtectedAction', 'ReadLinkedSignalEvidence', 'CorrectReport', 'ReviewProtectedAction', 'AssignCase', 'ResolveCase', 'ReopenCase', 'ReviewIntake', 'CoordinateIntake', 'RecordObservation', 'ManageFollowUp', 'ManageEscalation')");
        }
    }
}
