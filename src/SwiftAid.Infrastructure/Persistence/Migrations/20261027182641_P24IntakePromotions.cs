using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class P24IntakePromotions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "swiftaid_intake");

            migrationBuilder.CreateTable(
                name: "intake_routes",
                schema: "swiftaid_intake",
                columns: table => new
                {
                    route_id = table.Column<Guid>(type: "uuid", nullable: false),
                    country_code = table.Column<string>(type: "text", nullable: false),
                    catalogue_version = table.Column<string>(type: "text", nullable: false),
                    region_id = table.Column<string>(type: "text", nullable: true),
                    area_id = table.Column<string>(type: "text", nullable: true),
                    receiving_organization_id = table.Column<string>(type: "text", nullable: false),
                    intake_queue_id = table.Column<string>(type: "text", nullable: false),
                    effective_from_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_intake_routes", x => x.route_id);
                    table.CheckConstraint("ck_intake_routes_country", "length(country_code) = 2 AND country_code = upper(country_code)");
                    table.CheckConstraint("ck_intake_routes_ids", "length(btrim(catalogue_version)) > 0 AND length(btrim(receiving_organization_id)) > 0 AND length(btrim(intake_queue_id)) > 0");
                    table.CheckConstraint("ck_intake_routes_validity", "expires_at_utc IS NULL OR expires_at_utc > effective_from_utc");
                    table.CheckConstraint("ck_intake_routes_version", "version > 0");
                    table.ForeignKey(
                        name: "fk_intake_routes_organization",
                        column: x => x.receiving_organization_id,
                        principalTable: "organizations",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "intake_sessions",
                schema: "swiftaid_intake",
                columns: table => new
                {
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    receipt_proof_digest = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_intake_sessions", x => x.session_id);
                    table.CheckConstraint("ck_intake_sessions_proof_digest", "octet_length(receipt_proof_digest) = 32");
                });

            migrationBuilder.CreateTable(
                name: "intake_capabilities",
                schema: "swiftaid_intake",
                columns: table => new
                {
                    capability_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    capability_digest = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_intake_capabilities", x => x.capability_id);
                    table.CheckConstraint("ck_intake_capabilities_digest", "octet_length(capability_digest) = 32");
                    table.CheckConstraint("ck_intake_capabilities_expiry", "expires_at_utc > created_at_utc");
                    table.ForeignKey(
                        name: "fk_intake_capabilities_session",
                        column: x => x.session_id,
                        principalSchema: "swiftaid_intake",
                        principalTable: "intake_sessions",
                        principalColumn: "session_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "intake_reports",
                schema: "swiftaid_intake",
                columns: table => new
                {
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_submission_id = table.Column<Guid>(type: "uuid", nullable: false),
                    canonical_payload_digest = table.Column<byte[]>(type: "bytea", nullable: false),
                    protected_payload = table.Column<byte[]>(type: "bytea", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    routing_status = table.Column<string>(type: "text", nullable: false),
                    intake_queue_id = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    received_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decision_actor_id = table.Column<string>(type: "text", nullable: true),
                    review_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_intake_reports", x => x.receipt_id);
                    table.CheckConstraint("ck_intake_reports_digest", "octet_length(canonical_payload_digest) = 32");
                    table.CheckConstraint("ck_intake_reports_payload", "octet_length(protected_payload) > 0");
                    table.CheckConstraint("ck_intake_reports_routing", "routing_status IN ('NeedsRouting', 'RoutedForReview', 'NoServiceCoverage')");
                    table.CheckConstraint("ck_intake_reports_status", "status IN ('ReceivedUnverified', 'AwaitingReview', 'AcceptedIntoCase', 'LinkedAsEvidence', 'Declined')");
                    table.CheckConstraint("ck_intake_reports_version", "version > 0");
                    table.ForeignKey(
                        name: "fk_intake_reports_session",
                        column: x => x.session_id,
                        principalSchema: "swiftaid_intake",
                        principalTable: "intake_sessions",
                        principalColumn: "session_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "intake_attachments",
                schema: "swiftaid_intake",
                columns: table => new
                {
                    attachment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    capability_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_submission_id = table.Column<Guid>(type: "uuid", nullable: false),
                    object_key = table.Column<string>(type: "text", nullable: false),
                    content_type = table.Column<string>(type: "text", nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    duration_seconds = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attached_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_intake_attachments", x => x.attachment_id);
                    table.CheckConstraint("ck_intake_attachments_content_type", "length(btrim(content_type)) BETWEEN 1 AND 128");
                    table.CheckConstraint("ck_intake_attachments_duration", "duration_seconds BETWEEN 1 AND 600");
                    table.CheckConstraint("ck_intake_attachments_keys", "length(btrim(object_key)) BETWEEN 1 AND 512");
                    table.CheckConstraint("ck_intake_attachments_size", "size_bytes BETWEEN 1 AND 104857600");
                    table.CheckConstraint("ck_intake_attachments_state", "state IN ('Uploaded', 'Attached', 'Rejected', 'Expired')");
                    table.CheckConstraint("ck_intake_attachments_validity", "expires_at_utc > created_at_utc");
                    table.ForeignKey(
                        name: "fk_intake_attachments_capability",
                        column: x => x.capability_id,
                        principalSchema: "swiftaid_intake",
                        principalTable: "intake_capabilities",
                        principalColumn: "capability_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_intake_attachments_session",
                        column: x => x.session_id,
                        principalSchema: "swiftaid_intake",
                        principalTable: "intake_sessions",
                        principalColumn: "session_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "intake_outbox",
                schema: "swiftaid_intake",
                columns: table => new
                {
                    outbox_id = table.Column<Guid>(type: "uuid", nullable: false),
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_intake_outbox", x => x.outbox_id);
                    table.CheckConstraint("ck_intake_outbox_attempts", "attempts >= 0");
                    table.CheckConstraint("ck_intake_outbox_event_type", "event_type IN ('IntakeReceived')");
                    table.CheckConstraint("ck_intake_outbox_revision", "revision > 0");
                    table.ForeignKey(
                        name: "fk_intake_outbox_report",
                        column: x => x.receipt_id,
                        principalSchema: "swiftaid_intake",
                        principalTable: "intake_reports",
                        principalColumn: "receipt_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "intake_promotions",
                schema: "swiftaid_intake",
                columns: table => new
                {
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    case_id = table.Column<Guid>(type: "uuid", nullable: false),
                    accepting_officer_actor_id = table.Column<string>(type: "text", nullable: false),
                    source_revision = table.Column<long>(type: "bigint", nullable: false),
                    initial_assignee_actor_id = table.Column<string>(type: "text", nullable: true),
                    promoted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_intake_promotions", x => x.receipt_id);
                    table.CheckConstraint("ck_intake_promotions_revision", "source_revision > 0");
                    table.ForeignKey(
                        name: "fk_intake_promotions_report",
                        column: x => x.receipt_id,
                        principalSchema: "swiftaid_intake",
                        principalTable: "intake_reports",
                        principalColumn: "receipt_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_intake_attachments_binding",
                schema: "swiftaid_intake",
                table: "intake_attachments",
                columns: new[] { "capability_id", "session_id", "client_submission_id", "attachment_id" });

            migrationBuilder.CreateIndex(
                name: "ix_intake_attachments_expiry",
                schema: "swiftaid_intake",
                table: "intake_attachments",
                columns: new[] { "expires_at_utc", "state" });

            migrationBuilder.CreateIndex(
                name: "IX_intake_attachments_session_id",
                schema: "swiftaid_intake",
                table: "intake_attachments",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ux_intake_attachments_object_key",
                schema: "swiftaid_intake",
                table: "intake_attachments",
                column: "object_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_intake_capabilities_session_expiry",
                schema: "swiftaid_intake",
                table: "intake_capabilities",
                columns: new[] { "session_id", "expires_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_intake_capabilities_digest",
                schema: "swiftaid_intake",
                table: "intake_capabilities",
                column: "capability_digest",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_intake_outbox_pending",
                schema: "swiftaid_intake",
                table: "intake_outbox",
                columns: new[] { "created_at_utc", "outbox_id" },
                filter: "published_at_utc IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_intake_outbox_occurrence",
                schema: "swiftaid_intake",
                table: "intake_outbox",
                columns: new[] { "receipt_id", "revision", "event_type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_intake_promotions_case",
                schema: "swiftaid_intake",
                table: "intake_promotions",
                column: "case_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_intake_reports_review_queue",
                schema: "swiftaid_intake",
                table: "intake_reports",
                columns: new[] { "intake_queue_id", "status", "received_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_intake_reports_session_submission",
                schema: "swiftaid_intake",
                table: "intake_reports",
                columns: new[] { "session_id", "client_submission_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_intake_routes_match",
                schema: "swiftaid_intake",
                table: "intake_routes",
                columns: new[] { "country_code", "catalogue_version", "region_id", "area_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ix_intake_routes_organization",
                schema: "swiftaid_intake",
                table: "intake_routes",
                column: "receiving_organization_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "intake_attachments",
                schema: "swiftaid_intake");

            migrationBuilder.DropTable(
                name: "intake_outbox",
                schema: "swiftaid_intake");

            migrationBuilder.DropTable(
                name: "intake_promotions",
                schema: "swiftaid_intake");

            migrationBuilder.DropTable(
                name: "intake_routes",
                schema: "swiftaid_intake");

            migrationBuilder.DropTable(
                name: "intake_capabilities",
                schema: "swiftaid_intake");

            migrationBuilder.DropTable(
                name: "intake_reports",
                schema: "swiftaid_intake");

            migrationBuilder.DropTable(
                name: "intake_sessions",
                schema: "swiftaid_intake");
        }
    }
}
