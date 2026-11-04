using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using SwiftAid.Infrastructure.Persistence;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations;

[DbContext(typeof(SwiftAidDbContext))]
[Migration("20261103120000_AssistanceCommandEvents")]
public sealed partial class AssistanceCommandEvents : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint("ck_case_events_type", "case_events");
        migrationBuilder.AddCheckConstraint(
            "ck_case_events_type", "case_events",
            "event_type IN ('CaseReported', 'CaseAssigned', 'CaseReportCorrected', 'CaseActionRequested', 'CaseActionRequestReviewed', 'CaseResolved', 'CaseReopened', 'CaseObservationRecorded', 'CaseFollowUpScheduled', 'CaseFollowUpOutcomeRecorded', 'CaseEscalationRaised', 'CaseEscalationAcknowledged', 'CaseEscalationClosed', 'CaseAssignmentAcknowledged', 'CaseCollaboratorGranted', 'CaseCollaboratorRevoked', 'AssistanceRequested', 'AssistanceAccepted', 'AssistanceProgressRecorded', 'AssistanceProvided', 'AssistanceReceiptConfirmed', 'AssistanceUnfulfilled', 'AssistanceCancelled')");

        migrationBuilder.DropCheckConstraint("ck_grant_group_operations_value", "access_grant_group_operations");
        migrationBuilder.AddCheckConstraint(
            "ck_grant_group_operations_value", "access_grant_group_operations",
            "operation IN ('ReadCase', 'ReadAttachment', 'Synchronize', 'Search', 'Export', 'Count', 'RequestProtectedAction', 'ExecuteProtectedAction', 'ReadLinkedSignalEvidence', 'CorrectReport', 'ReviewProtectedAction', 'AssignCase', 'ResolveCase', 'ReopenCase', 'ReviewIntake', 'CoordinateIntake', 'RecordObservation', 'ManageFollowUp', 'ManageEscalation', 'AcknowledgeAssignment', 'ManageCollaborators', 'ManageAssistance')");

    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint("ck_case_events_type", "case_events");
        migrationBuilder.AddCheckConstraint(
            "ck_case_events_type", "case_events",
            "event_type IN ('CaseReported', 'CaseAssigned', 'CaseReportCorrected', 'CaseActionRequested', 'CaseActionRequestReviewed', 'CaseResolved', 'CaseReopened', 'CaseObservationRecorded', 'CaseFollowUpScheduled', 'CaseFollowUpOutcomeRecorded', 'CaseEscalationRaised', 'CaseEscalationAcknowledged', 'CaseEscalationClosed', 'CaseAssignmentAcknowledged', 'CaseCollaboratorGranted', 'CaseCollaboratorRevoked')");

        migrationBuilder.DropCheckConstraint("ck_grant_group_operations_value", "access_grant_group_operations");
        migrationBuilder.AddCheckConstraint(
            "ck_grant_group_operations_value", "access_grant_group_operations",
            "operation IN ('ReadCase', 'ReadAttachment', 'Synchronize', 'Search', 'Export', 'Count', 'RequestProtectedAction', 'ExecuteProtectedAction', 'ReadLinkedSignalEvidence', 'CorrectReport', 'ReviewProtectedAction', 'AssignCase', 'ResolveCase', 'ReopenCase', 'ReviewIntake', 'CoordinateIntake', 'RecordObservation', 'ManageFollowUp', 'ManageEscalation', 'AcknowledgeAssignment', 'ManageCollaborators')");
    }
}
