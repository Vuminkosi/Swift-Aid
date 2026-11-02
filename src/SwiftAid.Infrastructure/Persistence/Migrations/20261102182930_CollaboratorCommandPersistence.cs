using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CollaboratorCommandPersistence : Migration
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

            migrationBuilder.AddColumn<Guid>(
                name: "grant_id",
                table: "case_shares",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_case_events_type",
                table: "case_events",
                sql: "event_type IN ('CaseReported', 'CaseAssigned', 'CaseReportCorrected', 'CaseActionRequested', 'CaseActionRequestReviewed', 'CaseResolved', 'CaseReopened', 'CaseObservationRecorded', 'CaseFollowUpScheduled', 'CaseFollowUpOutcomeRecorded', 'CaseEscalationRaised', 'CaseEscalationAcknowledged', 'CaseEscalationClosed', 'CaseAssignmentAcknowledged', 'CaseCollaboratorGranted', 'CaseCollaboratorRevoked')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_grant_group_operations_value",
                table: "access_grant_group_operations",
                sql: "operation IN ('ReadCase', 'ReadAttachment', 'Synchronize', 'Search', 'Export', 'Count', 'RequestProtectedAction', 'ExecuteProtectedAction', 'ReadLinkedSignalEvidence', 'CorrectReport', 'ReviewProtectedAction', 'AssignCase', 'ResolveCase', 'ReopenCase', 'ReviewIntake', 'CoordinateIntake', 'RecordObservation', 'ManageFollowUp', 'ManageEscalation', 'AcknowledgeAssignment', 'ManageCollaborators')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM case_shares WHERE grant_id IS NOT NULL) THEN
                        RAISE EXCEPTION 'Cannot remove collaborator grant identity while collaborator shares exist.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropCheckConstraint(
                name: "ck_case_events_type",
                table: "case_events");

            migrationBuilder.DropCheckConstraint(
                name: "ck_grant_group_operations_value",
                table: "access_grant_group_operations");

            migrationBuilder.DropColumn(
                name: "grant_id",
                table: "case_shares");

            migrationBuilder.AddCheckConstraint(
                name: "ck_case_events_type",
                table: "case_events",
                sql: "event_type IN ('CaseReported', 'CaseAssigned', 'CaseReportCorrected', 'CaseActionRequested', 'CaseActionRequestReviewed', 'CaseResolved', 'CaseReopened', 'CaseObservationRecorded', 'CaseFollowUpScheduled', 'CaseFollowUpOutcomeRecorded', 'CaseEscalationRaised', 'CaseEscalationAcknowledged', 'CaseEscalationClosed', 'CaseAssignmentAcknowledged')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_grant_group_operations_value",
                table: "access_grant_group_operations",
                sql: "operation IN ('ReadCase', 'ReadAttachment', 'Synchronize', 'Search', 'Export', 'Count', 'RequestProtectedAction', 'ExecuteProtectedAction', 'ReadLinkedSignalEvidence', 'CorrectReport', 'ReviewProtectedAction', 'AssignCase', 'ResolveCase', 'ReopenCase', 'ReviewIntake', 'CoordinateIntake', 'RecordObservation', 'ManageFollowUp', 'ManageEscalation', 'AcknowledgeAssignment')");
        }
    }
}
