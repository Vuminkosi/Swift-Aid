using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EllaiDurableConversationOutcomes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "assistant_text",
                table: "ellai_utterances",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "outcome",
                table: "ellai_utterances",
                type: "text",
                nullable: false,
                defaultValue: "Queued");

            migrationBuilder.AddColumn<long>(
                name: "sequence",
                table: "ellai_utterances",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "user_text",
                table: "ellai_utterances",
                type: "text",
                nullable: true);

            // Old acknowledgements did not prove delivery and may have erased the prompt.
            // Preserve what remains; never manufacture successful history.
            migrationBuilder.Sql("""
                WITH ordered AS (
                    SELECT session_id, client_utterance_id,
                           row_number() OVER (PARTITION BY session_id ORDER BY accepted_at_utc, client_utterance_id) AS position
                    FROM ellai_utterances)
                UPDATE ellai_utterances AS turn
                SET sequence = ordered.position,
                    user_text = turn.work_payload_json ->> 'utterance',
                    outcome = CASE WHEN turn.completed_at_utc IS NOT NULL OR turn.work_payload_json IS NULL THEN 'LegacyUnknown'
                                   WHEN turn.attempt_count > 0 THEN 'RetryableFailure' ELSE 'Queued' END
                FROM ordered
                WHERE turn.session_id = ordered.session_id AND turn.client_utterance_id = ordered.client_utterance_id;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "ck_ellai_utterances_completed_response",
                table: "ellai_utterances",
                sql: "outcome <> 'Completed' OR (assistant_text IS NOT NULL AND length(btrim(assistant_text)) > 0 AND completed_at_utc IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ellai_utterances_outcome",
                table: "ellai_utterances",
                sql: "outcome IN ('Queued', 'Processing', 'RetryableFailure', 'Completed', 'TerminalFailure', 'Cancelled', 'LegacyUnknown')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_ellai_utterances_completed_response",
                table: "ellai_utterances");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ellai_utterances_outcome",
                table: "ellai_utterances");

            migrationBuilder.DropColumn(
                name: "assistant_text",
                table: "ellai_utterances");

            migrationBuilder.DropColumn(
                name: "outcome",
                table: "ellai_utterances");

            migrationBuilder.DropColumn(
                name: "sequence",
                table: "ellai_utterances");

            migrationBuilder.DropColumn(
                name: "user_text",
                table: "ellai_utterances");
        }
    }
}
