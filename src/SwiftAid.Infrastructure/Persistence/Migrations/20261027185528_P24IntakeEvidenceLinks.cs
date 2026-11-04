using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class P24IntakeEvidenceLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "intake_evidence_links",
                schema: "swiftaid_intake",
                columns: table => new
                {
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    case_id = table.Column<Guid>(type: "uuid", nullable: false),
                    linking_officer_actor_id = table.Column<string>(type: "text", nullable: false),
                    source_revision = table.Column<long>(type: "bigint", nullable: false),
                    linked_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_intake_evidence_links", x => x.receipt_id);
                    table.CheckConstraint("ck_intake_evidence_links_revision", "source_revision > 0");
                    table.ForeignKey(
                        name: "fk_intake_evidence_links_case",
                        column: x => x.case_id,
                        principalTable: "managed_cases",
                        principalColumn: "case_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_intake_evidence_links_report",
                        column: x => x.receipt_id,
                        principalSchema: "swiftaid_intake",
                        principalTable: "intake_reports",
                        principalColumn: "receipt_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_intake_evidence_links_case",
                schema: "swiftaid_intake",
                table: "intake_evidence_links",
                column: "case_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "intake_evidence_links",
                schema: "swiftaid_intake");
        }
    }
}
