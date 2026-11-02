using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AssistanceProjection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "managed_case_assistance",
                columns: table => new
                {
                    assistance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    case_id = table.Column<Guid>(type: "uuid", nullable: false),
                    need = table.Column<string>(type: "text", nullable: false),
                    responsible_actor_id = table.Column<string>(type: "text", nullable: false),
                    requested_by_actor_id = table.Column<string>(type: "text", nullable: false),
                    requested_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    due_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    last_note = table.Column<string>(type: "text", nullable: false),
                    accepted_by_actor_id = table.Column<string>(type: "text", nullable: true),
                    provided_by_actor_id = table.Column<string>(type: "text", nullable: true),
                    receipt_recorded_by_actor_id = table.Column<string>(type: "text", nullable: true),
                    receipt_method = table.Column<string>(type: "text", nullable: true),
                    receipt_context = table.Column<string>(type: "text", nullable: true),
                    last_event_version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_managed_case_assistance", x => x.assistance_id);
                    table.CheckConstraint("ck_managed_case_assistance_receipt", "(status = 'ReceiptConfirmed' AND receipt_recorded_by_actor_id IS NOT NULL AND receipt_method IS NOT NULL AND receipt_context IS NOT NULL) OR (status <> 'ReceiptConfirmed' AND receipt_recorded_by_actor_id IS NULL AND receipt_method IS NULL AND receipt_context IS NULL)");
                    table.CheckConstraint("ck_managed_case_assistance_status", "status IN ('Requested', 'Accepted', 'InProgress', 'Provided', 'ReceiptConfirmed', 'Unfulfilled', 'Cancelled')");
                    table.CheckConstraint("ck_managed_case_assistance_values", "length(btrim(need)) > 0 AND length(btrim(responsible_actor_id)) > 0 AND length(btrim(requested_by_actor_id)) > 0 AND length(btrim(last_note)) > 0");
                    table.CheckConstraint("ck_managed_case_assistance_versions", "last_event_version > 0");
                    table.ForeignKey(
                        name: "fk_managed_case_assistance_case",
                        column: x => x.case_id,
                        principalTable: "managed_cases",
                        principalColumn: "case_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_managed_case_assistance_case",
                table: "managed_case_assistance",
                column: "case_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "managed_case_assistance");
        }
    }
}
