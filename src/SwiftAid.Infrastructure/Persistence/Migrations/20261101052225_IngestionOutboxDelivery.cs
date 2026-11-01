using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IngestionOutboxDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_intake_outbox_pending",
                schema: "swiftaid_intake",
                table: "intake_outbox");

            migrationBuilder.DropIndex(
                name: "ix_domain_outbox_pending",
                table: "domain_outbox");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "available_at_utc",
                schema: "swiftaid_intake",
                table: "intake_outbox",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "CURRENT_TIMESTAMP");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "exhausted_at_utc",
                schema: "swiftaid_intake",
                table: "intake_outbox",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_failure_code",
                schema: "swiftaid_intake",
                table: "intake_outbox",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "lease_id",
                schema: "swiftaid_intake",
                table: "intake_outbox",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "lease_until_utc",
                schema: "swiftaid_intake",
                table: "intake_outbox",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "exhausted_at_utc",
                table: "domain_outbox",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_failure_code",
                table: "domain_outbox",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "lease_id",
                table: "domain_outbox",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_intake_outbox_pending",
                schema: "swiftaid_intake",
                table: "intake_outbox",
                columns: new[] { "available_at_utc", "created_at_utc", "outbox_id" },
                filter: "published_at_utc IS NULL AND exhausted_at_utc IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_intake_outbox_lease",
                schema: "swiftaid_intake",
                table: "intake_outbox",
                sql: "(lease_id IS NULL) = (lease_until_utc IS NULL) AND (published_at_utc IS NULL OR lease_id IS NULL) AND (exhausted_at_utc IS NULL OR (published_at_utc IS NULL AND lease_id IS NULL))");

            migrationBuilder.CreateIndex(
                name: "ix_domain_outbox_pending",
                table: "domain_outbox",
                columns: new[] { "available_at_utc", "created_at_utc" },
                filter: "published_at_utc IS NULL AND exhausted_at_utc IS NULL");

            // Legacy leases have no owner token and cannot fence a completion. Keep every row pending.
            // Deploy with dispatch paused; the new publisher will acquire a fresh token.
            migrationBuilder.Sql("UPDATE domain_outbox SET lease_until_utc = NULL WHERE lease_id IS NULL AND lease_until_utc IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_domain_outbox_fence",
                table: "domain_outbox",
                sql: "(lease_id IS NULL) = (lease_until_utc IS NULL) AND (exhausted_at_utc IS NULL OR (published_at_utc IS NULL AND lease_id IS NULL))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_intake_outbox_pending",
                schema: "swiftaid_intake",
                table: "intake_outbox");

            migrationBuilder.DropCheckConstraint(
                name: "ck_intake_outbox_lease",
                schema: "swiftaid_intake",
                table: "intake_outbox");

            migrationBuilder.DropIndex(
                name: "ix_domain_outbox_pending",
                table: "domain_outbox");

            migrationBuilder.DropCheckConstraint(
                name: "ck_domain_outbox_fence",
                table: "domain_outbox");

            migrationBuilder.DropColumn(
                name: "available_at_utc",
                schema: "swiftaid_intake",
                table: "intake_outbox");

            migrationBuilder.DropColumn(
                name: "exhausted_at_utc",
                schema: "swiftaid_intake",
                table: "intake_outbox");

            migrationBuilder.DropColumn(
                name: "last_failure_code",
                schema: "swiftaid_intake",
                table: "intake_outbox");

            migrationBuilder.DropColumn(
                name: "lease_id",
                schema: "swiftaid_intake",
                table: "intake_outbox");

            migrationBuilder.DropColumn(
                name: "lease_until_utc",
                schema: "swiftaid_intake",
                table: "intake_outbox");

            migrationBuilder.DropColumn(
                name: "exhausted_at_utc",
                table: "domain_outbox");

            migrationBuilder.DropColumn(
                name: "last_failure_code",
                table: "domain_outbox");

            migrationBuilder.DropColumn(
                name: "lease_id",
                table: "domain_outbox");

            migrationBuilder.CreateIndex(
                name: "ix_intake_outbox_pending",
                schema: "swiftaid_intake",
                table: "intake_outbox",
                columns: new[] { "created_at_utc", "outbox_id" },
                filter: "published_at_utc IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_domain_outbox_pending",
                table: "domain_outbox",
                columns: new[] { "available_at_utc", "created_at_utc" },
                filter: "published_at_utc IS NULL");
        }
    }
}
