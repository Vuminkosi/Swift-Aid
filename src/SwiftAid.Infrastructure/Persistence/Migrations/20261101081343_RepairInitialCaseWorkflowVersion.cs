using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RepairInitialCaseWorkflowVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Repair only the exact untouched registration shape. Do not guess versions for later workflows.
            migrationBuilder.Sql("""
                UPDATE public.managed_cases c SET workflow_version = 1
                WHERE c.workflow_version = 0 AND c.workflow = 'Reported' AND c.aggregate_version = 2 AND c.report_revision = 1
                  AND EXISTS (
                    SELECT 1 FROM public.case_events reported JOIN public.case_events assigned
                      ON assigned.case_id = reported.case_id AND assigned.command_id = reported.command_id AND assigned.actor_id = reported.actor_id
                    WHERE reported.case_id = c.case_id AND reported.event_type = 'CaseReported'
                      AND reported.aggregate_version = 1 AND reported.report_revision = 1
                      AND assigned.event_type = 'CaseAssigned' AND assigned.aggregate_version = 2 AND assigned.report_revision = 1
                      AND reported.recorded_at_utc = assigned.recorded_at_utc)
                  AND (SELECT count(*) FROM public.case_events e WHERE e.case_id = c.case_id) = 2;
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restoring the invalid zero version would break replay; data repair is intentionally retained.

        }
    }
}
