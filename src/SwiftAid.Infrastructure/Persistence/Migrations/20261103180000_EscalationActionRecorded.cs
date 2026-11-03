using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations;

[DbContext(typeof(SwiftAidDbContext))]
[Migration("20261103180000_EscalationActionRecorded")]
public sealed class EscalationActionRecorded : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "action_recorded_version",
            table: "managed_case_escalations",
            type: "bigint",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "action_summary",
            table: "managed_case_escalations",
            type: "text",
            nullable: true);

        migrationBuilder.DropCheckConstraint("ck_managed_case_escalations_status", "managed_case_escalations");
        migrationBuilder.AddCheckConstraint("ck_managed_case_escalations_status", "managed_case_escalations",
            "status IN ('Raised', 'Acknowledged', 'ActionRecorded', 'Closed')");
        migrationBuilder.DropCheckConstraint("ck_managed_case_escalations_versions", "managed_case_escalations");
        migrationBuilder.AddCheckConstraint("ck_managed_case_escalations_versions", "managed_case_escalations",
            "raised_version > 0 AND (acknowledged_version IS NULL OR acknowledged_version > 0) AND (action_recorded_version IS NULL OR action_recorded_version > 0) AND (closed_version IS NULL OR closed_version > 0)");

        migrationBuilder.DropCheckConstraint("ck_case_events_type", "case_events");
        migrationBuilder.AddCheckConstraint("ck_case_events_type", "case_events",
            "event_type IN ('CaseReported', 'CaseAssigned', 'CaseReportCorrected', 'CaseActionRequested', 'CaseActionRequestReviewed', 'CaseResolved', 'CaseReopened', 'CaseObservationRecorded', 'CaseFollowUpScheduled', 'CaseFollowUpAcknowledged', 'CaseFollowUpOutcomeRecorded', 'CaseEscalationRaised', 'CaseEscalationAcknowledged', 'CaseEscalationActionRecorded', 'CaseEscalationClosed', 'CaseAssignmentAcknowledged', 'CaseCollaboratorGranted', 'CaseCollaboratorRevoked', 'AssistanceRequested', 'AssistanceAccepted', 'AssistanceProgressRecorded', 'AssistanceProvided', 'AssistanceReceiptConfirmed', 'AssistanceUnfulfilled', 'AssistanceCancelled', 'ActionOwnerChanged')");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint("ck_case_events_type", "case_events");
        migrationBuilder.AddCheckConstraint("ck_case_events_type", "case_events",
            "event_type IN ('CaseReported', 'CaseAssigned', 'CaseReportCorrected', 'CaseActionRequested', 'CaseActionRequestReviewed', 'CaseResolved', 'CaseReopened', 'CaseObservationRecorded', 'CaseFollowUpScheduled', 'CaseFollowUpAcknowledged', 'CaseFollowUpOutcomeRecorded', 'CaseEscalationRaised', 'CaseEscalationAcknowledged', 'CaseEscalationClosed', 'CaseAssignmentAcknowledged', 'CaseCollaboratorGranted', 'CaseCollaboratorRevoked', 'AssistanceRequested', 'AssistanceAccepted', 'AssistanceProgressRecorded', 'AssistanceProvided', 'AssistanceReceiptConfirmed', 'AssistanceUnfulfilled', 'AssistanceCancelled', 'ActionOwnerChanged')");

        migrationBuilder.DropCheckConstraint("ck_managed_case_escalations_status", "managed_case_escalations");
        migrationBuilder.AddCheckConstraint("ck_managed_case_escalations_status", "managed_case_escalations",
            "status IN ('Raised', 'Acknowledged', 'Closed')");
        migrationBuilder.DropCheckConstraint("ck_managed_case_escalations_versions", "managed_case_escalations");
        migrationBuilder.AddCheckConstraint("ck_managed_case_escalations_versions", "managed_case_escalations",
            "raised_version > 0 AND (acknowledged_version IS NULL OR acknowledged_version > 0) AND (closed_version IS NULL OR closed_version > 0)");
        migrationBuilder.DropColumn("action_recorded_version", "managed_case_escalations");
        migrationBuilder.DropColumn("action_summary", "managed_case_escalations");
    }
}
