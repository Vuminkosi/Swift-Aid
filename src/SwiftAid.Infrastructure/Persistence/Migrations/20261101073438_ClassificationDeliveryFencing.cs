using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ClassificationDeliveryFencing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE FUNCTION swiftaid_classification.preserve_artifact() RETURNS trigger
                LANGUAGE plpgsql SET search_path = pg_catalog AS $$
                BEGIN
                    IF TG_TABLE_NAME <> 'results' OR TG_OP = 'DELETE' OR OLD."State" = 'Completed' THEN
                        RAISE EXCEPTION 'Classification artifacts are immutable';
                    END IF;
                    IF (NEW."Id", NEW."Fingerprint", NEW."OrganizationId", NEW."WorkspaceId", NEW."ConfigurationId", NEW."ContentHash")
                        IS DISTINCT FROM (OLD."Id", OLD."Fingerprint", OLD."OrganizationId", OLD."WorkspaceId", OLD."ConfigurationId", OLD."ContentHash") THEN
                        RAISE EXCEPTION 'Classification identity is immutable';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER immutable_classification_result BEFORE UPDATE OR DELETE ON swiftaid_classification.results
                    FOR EACH ROW EXECUTE FUNCTION swiftaid_classification.preserve_artifact();
                CREATE FUNCTION swiftaid_classification.reject_artifact_change() RETURNS trigger
                LANGUAGE plpgsql SET search_path = pg_catalog AS $$
                BEGIN RAISE EXCEPTION 'Classification provenance is immutable'; END $$;
                CREATE TRIGGER immutable_classification_configuration BEFORE UPDATE OR DELETE ON swiftaid_classification.configurations
                    FOR EACH ROW EXECUTE FUNCTION swiftaid_classification.reject_artifact_change();
                CREATE TRIGGER immutable_classification_association BEFORE UPDATE OR DELETE ON swiftaid_classification.associations
                    FOR EACH ROW EXECUTE FUNCTION swiftaid_classification.reject_artifact_change();
                """);
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AttemptStartedAtUtc",
                schema: "swiftaid_classification",
                table: "results",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Sequence",
                schema: "swiftaid_classification",
                table: "outbox",
                type: "bigint",
                nullable: false,
                defaultValue: 0L)
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.CreateTable(
                name: "quarantine",
                schema: "swiftaid_classification",
                columns: table => new
                {
                    WorkerId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    StreamSequence = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    PayloadSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quarantine", x => new { x.WorkerId, x.StreamSequence });
                });

            migrationBuilder.CreateIndex(
                name: "IX_outbox_Sequence",
                schema: "swiftaid_classification",
                table: "outbox",
                column: "Sequence",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER immutable_classification_association ON swiftaid_classification.associations;
                DROP TRIGGER immutable_classification_configuration ON swiftaid_classification.configurations;
                DROP TRIGGER immutable_classification_result ON swiftaid_classification.results;
                DROP FUNCTION swiftaid_classification.reject_artifact_change();
                DROP FUNCTION swiftaid_classification.preserve_artifact();
                """);
            migrationBuilder.DropTable(
                name: "quarantine",
                schema: "swiftaid_classification");

            migrationBuilder.DropIndex(
                name: "IX_outbox_Sequence",
                schema: "swiftaid_classification",
                table: "outbox");

            migrationBuilder.DropColumn(
                name: "AttemptStartedAtUtc",
                schema: "swiftaid_classification",
                table: "results");

            migrationBuilder.DropColumn(
                name: "Sequence",
                schema: "swiftaid_classification",
                table: "outbox");
        }
    }
}
