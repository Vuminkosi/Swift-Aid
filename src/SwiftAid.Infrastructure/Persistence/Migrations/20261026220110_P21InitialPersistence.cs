using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class P21InitialPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "actors",
                columns: table => new
                {
                    actor_id = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_actors", x => x.actor_id);
                    table.CheckConstraint("ck_actors_expiry", "expires_at_utc IS NULL OR expires_at_utc > created_at_utc");
                    table.CheckConstraint("ck_actors_id_nonempty", "length(btrim(actor_id)) > 0");
                    table.CheckConstraint("ck_actors_status", "status IN ('Active', 'Suspended', 'Revoked')");
                });

            migrationBuilder.CreateTable(
                name: "organizations",
                columns: table => new
                {
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_organizations", x => x.organization_id);
                    table.CheckConstraint("ck_organizations_id_nonempty", "length(btrim(organization_id)) > 0");
                    table.CheckConstraint("ck_organizations_status", "status IN ('Active', 'Suspended', 'Revoked')");
                });

            migrationBuilder.CreateTable(
                name: "protected_event_payloads",
                columns: table => new
                {
                    payload_id = table.Column<Guid>(type: "uuid", nullable: false),
                    protected_bytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    protection_purpose_version = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_protected_event_payloads", x => x.payload_id);
                    table.CheckConstraint("ck_protected_event_payloads_bytes", "octet_length(protected_bytes) > 0");
                    table.CheckConstraint("ck_protected_event_payloads_purpose", "length(btrim(protection_purpose_version)) > 0");
                });

            migrationBuilder.CreateTable(
                name: "security_audit_entries",
                columns: table => new
                {
                    audit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<string>(type: "text", nullable: false),
                    target_actor_id = table.Column<string>(type: "text", nullable: true),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: true),
                    operation = table.Column<string>(type: "text", nullable: false),
                    decision_code = table.Column<string>(type: "text", nullable: false),
                    access_epoch = table.Column<long>(type: "bigint", nullable: false),
                    recorded_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reason_code = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_security_audit_entries", x => x.audit_id);
                    table.CheckConstraint("ck_security_audit_actor", "length(btrim(actor_id)) > 0");
                    table.CheckConstraint("ck_security_audit_decision", "length(btrim(decision_code)) > 0");
                    table.CheckConstraint("ck_security_audit_epoch", "access_epoch >= 0");
                    table.CheckConstraint("ck_security_audit_operation", "length(btrim(operation)) > 0");
                });

            migrationBuilder.CreateTable(
                name: "actor_access_states",
                columns: table => new
                {
                    actor_id = table.Column<string>(type: "text", nullable: false),
                    access_epoch = table.Column<long>(type: "bigint", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_actor_access_states", x => x.actor_id);
                    table.CheckConstraint("ck_actor_access_states_epoch", "access_epoch >= 0");
                    table.CheckConstraint("ck_actor_access_states_version", "version > 0");
                    table.ForeignKey(
                        name: "fk_actor_access_states_actor",
                        column: x => x.actor_id,
                        principalTable: "actors",
                        principalColumn: "actor_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "organization_memberships",
                columns: table => new
                {
                    membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    valid_from_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    granted_by_actor_id = table.Column<string>(type: "text", nullable: false),
                    revoked_by_actor_id = table.Column<string>(type: "text", nullable: true),
                    change_reason = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_organization_memberships", x => x.membership_id);
                    table.UniqueConstraint("ak_memberships_id_actor_organization", x => new { x.membership_id, x.actor_id, x.organization_id });
                    table.CheckConstraint("ck_memberships_ids_nonempty", "length(btrim(actor_id)) > 0 AND length(btrim(organization_id)) > 0 AND length(btrim(granted_by_actor_id)) > 0");
                    table.CheckConstraint("ck_memberships_status", "status IN ('Active', 'Suspended', 'Revoked')");
                    table.CheckConstraint("ck_memberships_validity", "expires_at_utc IS NULL OR expires_at_utc > valid_from_utc");
                    table.CheckConstraint("ck_memberships_version", "version > 0");
                    table.ForeignKey(
                        name: "fk_memberships_actor",
                        column: x => x.actor_id,
                        principalTable: "actors",
                        principalColumn: "actor_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_memberships_grantor",
                        column: x => x.granted_by_actor_id,
                        principalTable: "actors",
                        principalColumn: "actor_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_memberships_organization",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_memberships_revoker",
                        column: x => x.revoked_by_actor_id,
                        principalTable: "actors",
                        principalColumn: "actor_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "workspaces",
                columns: table => new
                {
                    workspace_id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workspaces", x => x.workspace_id);
                    table.UniqueConstraint("ak_workspaces_workspace_organization", x => new { x.workspace_id, x.organization_id });
                    table.CheckConstraint("ck_workspaces_ids_nonempty", "length(btrim(workspace_id)) > 0 AND length(btrim(organization_id)) > 0");
                    table.CheckConstraint("ck_workspaces_status", "status IN ('Active', 'Suspended', 'Revoked')");
                    table.ForeignKey(
                        name: "fk_workspaces_organization",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "organization_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "access_grant_groups",
                columns: table => new
                {
                    grant_group_id = table.Column<string>(type: "text", nullable: false),
                    membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    workspace_id = table.Column<string>(type: "text", nullable: false),
                    country_code = table.Column<string>(type: "text", nullable: false),
                    sector = table.Column<string>(type: "text", nullable: false),
                    sensitivity_code = table.Column<string>(type: "text", nullable: false),
                    can_access_jurisdictional_cases = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    valid_from_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    granted_by_actor_id = table.Column<string>(type: "text", nullable: false),
                    revoked_by_actor_id = table.Column<string>(type: "text", nullable: true),
                    change_reason = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_access_grant_groups", x => x.grant_group_id);
                    table.CheckConstraint("ck_access_grant_groups_ids_nonempty", "length(btrim(grant_group_id)) > 0 AND length(btrim(actor_id)) > 0 AND length(btrim(organization_id)) > 0 AND length(btrim(workspace_id)) > 0 AND length(btrim(country_code)) > 0 AND length(btrim(sensitivity_code)) > 0 AND length(btrim(granted_by_actor_id)) > 0");
                    table.CheckConstraint("ck_access_grant_groups_sector", "sector IN ('HumanHealth', 'AnimalHealth')");
                    table.CheckConstraint("ck_access_grant_groups_status", "status IN ('Active', 'Suspended', 'Revoked')");
                    table.CheckConstraint("ck_access_grant_groups_validity", "expires_at_utc > valid_from_utc");
                    table.CheckConstraint("ck_access_grant_groups_version", "version > 0");
                    table.ForeignKey(
                        name: "fk_access_grant_groups_actor",
                        column: x => x.actor_id,
                        principalTable: "actors",
                        principalColumn: "actor_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_access_grant_groups_grantor",
                        column: x => x.granted_by_actor_id,
                        principalTable: "actors",
                        principalColumn: "actor_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_access_grant_groups_membership_scope",
                        columns: x => new { x.membership_id, x.actor_id, x.organization_id },
                        principalTable: "organization_memberships",
                        principalColumns: new[] { "membership_id", "actor_id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_access_grant_groups_revoker",
                        column: x => x.revoked_by_actor_id,
                        principalTable: "actors",
                        principalColumn: "actor_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_access_grant_groups_workspace_scope",
                        columns: x => new { x.workspace_id, x.organization_id },
                        principalTable: "workspaces",
                        principalColumns: new[] { "workspace_id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "managed_cases",
                columns: table => new
                {
                    case_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    workspace_id = table.Column<string>(type: "text", nullable: false),
                    country_code = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    sector = table.Column<string>(type: "text", nullable: false),
                    sensitivity_code = table.Column<string>(type: "text", nullable: false),
                    accountable_owner_actor_id = table.Column<string>(type: "text", nullable: false),
                    resource_version = table.Column<long>(type: "bigint", nullable: false),
                    aggregate_version = table.Column<long>(type: "bigint", nullable: false),
                    report_revision = table.Column<long>(type: "bigint", nullable: false),
                    workflow = table.Column<string>(type: "text", nullable: false),
                    workflow_version = table.Column<long>(type: "bigint", nullable: false),
                    classification = table.Column<string>(type: "text", nullable: false),
                    classification_version = table.Column<long>(type: "bigint", nullable: false),
                    laboratory = table.Column<string>(type: "text", nullable: false),
                    laboratory_version = table.Column<long>(type: "bigint", nullable: false),
                    outcome_state = table.Column<string>(type: "text", nullable: false),
                    outcome_value = table.Column<string>(type: "text", nullable: true),
                    outcome_version = table.Column<long>(type: "bigint", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_managed_cases", x => x.case_id);
                    table.UniqueConstraint("ak_managed_cases_id_organization", x => new { x.case_id, x.organization_id });
                    table.CheckConstraint("ck_managed_cases_classification", "classification IN ('Unclassified', 'Suspected', 'Probable', 'Confirmed', 'RuledOut')");
                    table.CheckConstraint("ck_managed_cases_ids_nonempty", "length(btrim(organization_id)) > 0 AND length(btrim(workspace_id)) > 0 AND length(btrim(country_code)) > 0 AND length(btrim(sensitivity_code)) > 0 AND length(btrim(accountable_owner_actor_id)) > 0");
                    table.CheckConstraint("ck_managed_cases_kind", "kind IN ('Human', 'Animal')");
                    table.CheckConstraint("ck_managed_cases_kind_sector", "(kind = 'Human' AND sector = 'HumanHealth') OR (kind = 'Animal' AND sector = 'AnimalHealth')");
                    table.CheckConstraint("ck_managed_cases_laboratory", "laboratory IN ('NotRecorded', 'Requested', 'Collected', 'Pending', 'ResultRecorded')");
                    table.CheckConstraint("ck_managed_cases_outcome_state", "outcome_state IN ('Unknown', 'NotRecorded', 'NotApplicable', 'Absent', 'Known')");
                    table.CheckConstraint("ck_managed_cases_outcome_value", "(outcome_state = 'Known' AND outcome_value IS NOT NULL) OR (outcome_state <> 'Known' AND outcome_value IS NULL)");
                    table.CheckConstraint("ck_managed_cases_sector", "sector IN ('HumanHealth', 'AnimalHealth')");
                    table.CheckConstraint("ck_managed_cases_versions", "resource_version > 0 AND aggregate_version >= 2 AND report_revision > 0 AND report_revision <= aggregate_version AND workflow_version >= 0 AND classification_version >= 0 AND laboratory_version >= 0 AND outcome_version >= 0");
                    table.CheckConstraint("ck_managed_cases_workflow", "workflow IN ('Reported', 'UnderReview', 'Active', 'AwaitingFollowUp', 'Resolved')");
                    table.ForeignKey(
                        name: "fk_managed_cases_accountable_owner",
                        column: x => x.accountable_owner_actor_id,
                        principalTable: "actors",
                        principalColumn: "actor_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_managed_cases_workspace_scope",
                        columns: x => new { x.workspace_id, x.organization_id },
                        principalTable: "workspaces",
                        principalColumns: new[] { "workspace_id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "access_grant_group_operations",
                columns: table => new
                {
                    grant_group_id = table.Column<string>(type: "text", nullable: false),
                    operation = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_access_grant_group_operations", x => new { x.grant_group_id, x.operation });
                    table.CheckConstraint("ck_grant_group_operations_value", "operation IN ('ReadCase', 'ReadAttachment', 'Synchronize', 'Search', 'Export', 'Count', 'RequestProtectedAction', 'ExecuteProtectedAction', 'ReadLinkedSignalEvidence')");
                    table.ForeignKey(
                        name: "fk_access_grant_group_operations_group",
                        column: x => x.grant_group_id,
                        principalTable: "access_grant_groups",
                        principalColumn: "grant_group_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "access_grant_group_regions",
                columns: table => new
                {
                    grant_group_id = table.Column<string>(type: "text", nullable: false),
                    region_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_access_grant_group_regions", x => new { x.grant_group_id, x.region_id });
                    table.CheckConstraint("ck_access_grant_group_regions_id_nonempty", "length(btrim(region_id)) > 0");
                    table.ForeignKey(
                        name: "fk_access_grant_group_regions_group",
                        column: x => x.grant_group_id,
                        principalTable: "access_grant_groups",
                        principalColumn: "grant_group_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "case_shares",
                columns: table => new
                {
                    case_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    granted_by_actor_id = table.Column<string>(type: "text", nullable: false),
                    revoked_by_actor_id = table.Column<string>(type: "text", nullable: true),
                    change_reason = table.Column<string>(type: "text", nullable: true),
                    granted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_case_shares", x => new { x.case_id, x.actor_id });
                    table.CheckConstraint("ck_case_shares_expiry", "expires_at_utc IS NULL OR expires_at_utc > granted_at_utc");
                    table.CheckConstraint("ck_case_shares_grantor", "length(btrim(granted_by_actor_id)) > 0");
                    table.CheckConstraint("ck_case_shares_version", "version > 0");
                    table.ForeignKey(
                        name: "fk_case_shares_actor",
                        column: x => x.actor_id,
                        principalTable: "actors",
                        principalColumn: "actor_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_case_shares_case",
                        column: x => x.case_id,
                        principalTable: "managed_cases",
                        principalColumn: "case_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_case_shares_grantor",
                        column: x => x.granted_by_actor_id,
                        principalTable: "actors",
                        principalColumn: "actor_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_case_shares_revoker",
                        column: x => x.revoked_by_actor_id,
                        principalTable: "actors",
                        principalColumn: "actor_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "command_receipts",
                columns: table => new
                {
                    receipt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<string>(type: "text", nullable: false),
                    command_id = table.Column<Guid>(type: "uuid", nullable: false),
                    case_id = table.Column<Guid>(type: "uuid", nullable: false),
                    command_type = table.Column<string>(type: "text", nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    canonical_payload_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    first_aggregate_version = table.Column<long>(type: "bigint", nullable: false),
                    last_aggregate_version = table.Column<long>(type: "bigint", nullable: false),
                    report_revision = table.Column<long>(type: "bigint", nullable: false),
                    accepted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_command_receipts", x => x.receipt_id);
                    table.UniqueConstraint("ak_command_receipts_actor_command_case", x => new { x.actor_id, x.command_id, x.case_id });
                    table.CheckConstraint("ck_command_receipts_hash", "octet_length(canonical_payload_hash) = 32");
                    table.CheckConstraint("ck_command_receipts_ids_nonempty", "length(btrim(actor_id)) > 0 AND length(btrim(command_type)) > 0");
                    table.CheckConstraint("ck_command_receipts_versions", "schema_version > 0 AND first_aggregate_version > 0 AND last_aggregate_version >= first_aggregate_version AND report_revision > 0");
                    table.ForeignKey(
                        name: "fk_command_receipts_case",
                        column: x => x.case_id,
                        principalTable: "managed_cases",
                        principalColumn: "case_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "managed_case_regions",
                columns: table => new
                {
                    case_id = table.Column<Guid>(type: "uuid", nullable: false),
                    region_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_managed_case_regions", x => new { x.case_id, x.region_id });
                    table.CheckConstraint("ck_managed_case_regions_id_nonempty", "length(btrim(region_id)) > 0");
                    table.ForeignKey(
                        name: "fk_managed_case_regions_case",
                        column: x => x.case_id,
                        principalTable: "managed_cases",
                        principalColumn: "case_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "case_events",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    case_id = table.Column<Guid>(type: "uuid", nullable: false),
                    aggregate_version = table.Column<long>(type: "bigint", nullable: false),
                    report_revision = table.Column<long>(type: "bigint", nullable: false),
                    command_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<string>(type: "text", nullable: false),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    recorded_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    payload_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_case_events", x => x.event_id);
                    table.CheckConstraint("ck_case_events_actor", "length(btrim(actor_id)) > 0");
                    table.CheckConstraint("ck_case_events_type", "event_type IN ('CaseReported', 'CaseAssigned', 'CaseReportCorrected', 'CaseActionRequested', 'CaseActionRequestReviewed', 'CaseResolved', 'CaseReopened')");
                    table.CheckConstraint("ck_case_events_versions", "aggregate_version > 0 AND report_revision > 0 AND schema_version > 0");
                    table.ForeignKey(
                        name: "fk_case_events_accepted_receipt",
                        columns: x => new { x.actor_id, x.command_id, x.case_id },
                        principalTable: "command_receipts",
                        principalColumns: new[] { "actor_id", "command_id", "case_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_case_events_case",
                        column: x => x.case_id,
                        principalTable: "managed_cases",
                        principalColumn: "case_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_case_events_payload",
                        column: x => x.payload_id,
                        principalTable: "protected_event_payloads",
                        principalColumn: "payload_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "domain_outbox",
                columns: table => new
                {
                    outbox_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_type = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    available_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    lease_until_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    published_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_domain_outbox", x => x.outbox_id);
                    table.CheckConstraint("ck_domain_outbox_attempt_count", "attempt_count >= 0");
                    table.CheckConstraint("ck_domain_outbox_lease", "published_at_utc IS NULL OR lease_until_utc IS NULL");
                    table.CheckConstraint("ck_domain_outbox_message_type", "length(btrim(message_type)) > 0");
                    table.ForeignKey(
                        name: "fk_domain_outbox_event",
                        column: x => x.event_id,
                        principalTable: "case_events",
                        principalColumn: "event_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_grant_group_operations_value",
                table: "access_grant_group_operations",
                columns: new[] { "operation", "grant_group_id" });

            migrationBuilder.CreateIndex(
                name: "ix_access_grant_group_regions_region",
                table: "access_grant_group_regions",
                columns: new[] { "region_id", "grant_group_id" });

            migrationBuilder.CreateIndex(
                name: "ix_access_grant_groups_actor_status_expiry",
                table: "access_grant_groups",
                columns: new[] { "actor_id", "status", "expires_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_access_grant_groups_granted_by_actor_id",
                table: "access_grant_groups",
                column: "granted_by_actor_id");

            migrationBuilder.CreateIndex(
                name: "IX_access_grant_groups_membership_id_actor_id_organization_id",
                table: "access_grant_groups",
                columns: new[] { "membership_id", "actor_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_access_grant_groups_membership_scope",
                table: "access_grant_groups",
                columns: new[] { "membership_id", "organization_id", "workspace_id" });

            migrationBuilder.CreateIndex(
                name: "IX_access_grant_groups_revoked_by_actor_id",
                table: "access_grant_groups",
                column: "revoked_by_actor_id");

            migrationBuilder.CreateIndex(
                name: "IX_access_grant_groups_workspace_id_organization_id",
                table: "access_grant_groups",
                columns: new[] { "workspace_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "IX_case_events_actor_id_command_id_case_id",
                table: "case_events",
                columns: new[] { "actor_id", "command_id", "case_id" });

            migrationBuilder.CreateIndex(
                name: "ix_case_events_case_command",
                table: "case_events",
                columns: new[] { "case_id", "command_id" });

            migrationBuilder.CreateIndex(
                name: "IX_case_events_payload_id",
                table: "case_events",
                column: "payload_id");

            migrationBuilder.CreateIndex(
                name: "ux_case_events_case_aggregate_version",
                table: "case_events",
                columns: new[] { "case_id", "aggregate_version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_case_shares_actor_status_expiry",
                table: "case_shares",
                columns: new[] { "actor_id", "is_active", "expires_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_case_shares_granted_by_actor_id",
                table: "case_shares",
                column: "granted_by_actor_id");

            migrationBuilder.CreateIndex(
                name: "IX_case_shares_revoked_by_actor_id",
                table: "case_shares",
                column: "revoked_by_actor_id");

            migrationBuilder.CreateIndex(
                name: "ix_command_receipts_case_accepted",
                table: "command_receipts",
                columns: new[] { "case_id", "accepted_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_command_receipts_actor_command",
                table: "command_receipts",
                columns: new[] { "actor_id", "command_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_domain_outbox_pending",
                table: "domain_outbox",
                columns: new[] { "available_at_utc", "created_at_utc" },
                filter: "published_at_utc IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_domain_outbox_event",
                table: "domain_outbox",
                column: "event_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_managed_case_regions_region",
                table: "managed_case_regions",
                columns: new[] { "region_id", "case_id" });

            migrationBuilder.CreateIndex(
                name: "ix_managed_cases_owner",
                table: "managed_cases",
                columns: new[] { "accountable_owner_actor_id", "case_id" });

            migrationBuilder.CreateIndex(
                name: "ix_managed_cases_scope",
                table: "managed_cases",
                columns: new[] { "organization_id", "workspace_id", "country_code", "sector", "sensitivity_code" });

            migrationBuilder.CreateIndex(
                name: "IX_managed_cases_workspace_id_organization_id",
                table: "managed_cases",
                columns: new[] { "workspace_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_memberships_actor_status_expiry",
                table: "organization_memberships",
                columns: new[] { "actor_id", "status", "expires_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_organization_memberships_granted_by_actor_id",
                table: "organization_memberships",
                column: "granted_by_actor_id");

            migrationBuilder.CreateIndex(
                name: "IX_organization_memberships_organization_id",
                table: "organization_memberships",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_organization_memberships_revoked_by_actor_id",
                table: "organization_memberships",
                column: "revoked_by_actor_id");

            migrationBuilder.CreateIndex(
                name: "ux_memberships_actor_organization",
                table: "organization_memberships",
                columns: new[] { "actor_id", "organization_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_security_audit_actor_recorded",
                table: "security_audit_entries",
                columns: new[] { "actor_id", "recorded_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_security_audit_resource_recorded",
                table: "security_audit_entries",
                columns: new[] { "resource_id", "recorded_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_workspaces_organization_id",
                table: "workspaces",
                column: "organization_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "access_grant_group_operations");

            migrationBuilder.DropTable(
                name: "access_grant_group_regions");

            migrationBuilder.DropTable(
                name: "actor_access_states");

            migrationBuilder.DropTable(
                name: "case_shares");

            migrationBuilder.DropTable(
                name: "domain_outbox");

            migrationBuilder.DropTable(
                name: "managed_case_regions");

            migrationBuilder.DropTable(
                name: "security_audit_entries");

            migrationBuilder.DropTable(
                name: "access_grant_groups");

            migrationBuilder.DropTable(
                name: "case_events");

            migrationBuilder.DropTable(
                name: "organization_memberships");

            migrationBuilder.DropTable(
                name: "command_receipts");

            migrationBuilder.DropTable(
                name: "protected_event_payloads");

            migrationBuilder.DropTable(
                name: "managed_cases");

            migrationBuilder.DropTable(
                name: "actors");

            migrationBuilder.DropTable(
                name: "workspaces");

            migrationBuilder.DropTable(
                name: "organizations");
        }
    }
}
