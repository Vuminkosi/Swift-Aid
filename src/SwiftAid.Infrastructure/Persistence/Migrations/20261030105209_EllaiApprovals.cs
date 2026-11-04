using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EllaiApprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ellai_plan_approvals",
                columns: table => new
                {
                    approval_id = table.Column<string>(type: "text", nullable: false),
                    session_id = table.Column<string>(type: "text", nullable: false),
                    action_id = table.Column<string>(type: "text", nullable: false),
                    idempotency_key = table.Column<string>(type: "text", nullable: false),
                    plan_hash = table.Column<string>(type: "text", nullable: false),
                    plan_version = table.Column<int>(type: "integer", nullable: false),
                    approved_recipient = table.Column<string>(type: "text", nullable: true),
                    approved_due_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    policy_hash = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    verified_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ellai_plan_approvals", x => x.approval_id);
                    table.CheckConstraint("ck_ellai_plan_approvals_expiry", "expires_at_utc > created_at_utc");
                    table.CheckConstraint("ck_ellai_plan_approvals_ids_nonempty", "length(btrim(approval_id)) > 0 AND length(btrim(session_id)) > 0 AND length(btrim(action_id)) > 0 AND length(btrim(idempotency_key)) > 0 AND length(btrim(plan_hash)) > 0");
                    table.CheckConstraint("ck_ellai_plan_approvals_status", "status IN ('Accepted', 'AcceptedUnverified', 'UnknownOutcome', 'PartiallyApplied', 'Applied', 'Failed')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_ellai_plan_approvals_session",
                table: "ellai_plan_approvals",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ux_ellai_plan_approvals_key",
                table: "ellai_plan_approvals",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_ellai_plan_approvals_plan_action",
                table: "ellai_plan_approvals",
                columns: new[] { "plan_hash", "plan_version", "action_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ellai_plan_approvals");
        }
    }
}
