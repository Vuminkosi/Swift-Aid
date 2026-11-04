using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EllaiUtteranceOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "attempt_count",
                table: "ellai_utterances",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "completed_at_utc",
                table: "ellai_utterances",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "lease_expires_at_utc",
                table: "ellai_utterances",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "work_payload_json",
                table: "ellai_utterances",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_ellai_utterances_outbox_claim",
                table: "ellai_utterances",
                columns: new[] { "completed_at_utc", "lease_expires_at_utc", "accepted_at_utc" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_ellai_utterances_attempt_count",
                table: "ellai_utterances",
                sql: "attempt_count >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ellai_utterances_payload_size",
                table: "ellai_utterances",
                sql: "work_payload_json IS NULL OR octet_length(work_payload_json::text) <= 16384");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_ellai_utterances_outbox_claim",
                table: "ellai_utterances");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ellai_utterances_attempt_count",
                table: "ellai_utterances");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ellai_utterances_payload_size",
                table: "ellai_utterances");

            migrationBuilder.DropColumn(
                name: "attempt_count",
                table: "ellai_utterances");

            migrationBuilder.DropColumn(
                name: "completed_at_utc",
                table: "ellai_utterances");

            migrationBuilder.DropColumn(
                name: "lease_expires_at_utc",
                table: "ellai_utterances");

            migrationBuilder.DropColumn(
                name: "work_payload_json",
                table: "ellai_utterances");
        }
    }
}
