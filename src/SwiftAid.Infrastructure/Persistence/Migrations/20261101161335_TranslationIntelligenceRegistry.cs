using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TranslationIntelligenceRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "swiftaid_translation");

            migrationBuilder.CreateTable(
                name: "artifacts",
                schema: "swiftaid_translation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<string>(type: "text", nullable: false),
                    WorkspaceId = table.Column<string>(type: "text", nullable: false),
                    Fingerprint = table.Column<string>(type: "text", nullable: false),
                    ContentHash = table.Column<string>(type: "text", nullable: false),
                    SourceLanguage = table.Column<string>(type: "text", nullable: false),
                    ModelRevision = table.Column<string>(type: "text", nullable: false),
                    Configuration = table.Column<string>(type: "text", nullable: false),
                    ProtectedSource = table.Column<byte[]>(type: "bytea", nullable: false),
                    ProtectedTranslation = table.Column<byte[]>(type: "bytea", nullable: true),
                    State = table.Column<string>(type: "text", nullable: false),
                    ReviewStatus = table.Column<string>(type: "text", nullable: false),
                    ReviewVersion = table.Column<long>(type: "bigint", nullable: false),
                    FindingsJson = table.Column<string>(type: "jsonb", nullable: false),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseUntilUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    FailureCode = table.Column<string>(type: "text", nullable: true),
                    AvailableAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedLeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    ResultHash = table.Column<string>(type: "text", nullable: true),
                    InferenceMilliseconds = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_translation_artifacts", x => x.Id);
                    table.CheckConstraint("ck_translation_attempts", "\"Attempts\" BETWEEN 0 AND 5");
                    table.CheckConstraint("ck_translation_lease", "(\"State\" = 'Leased') = (\"LeaseId\" IS NOT NULL AND \"LeaseUntilUtc\" IS NOT NULL)");
                    table.CheckConstraint("ck_translation_review", "\"ReviewStatus\" IN ('Pending','Unreviewed','Approved','Rejected')");
                    table.CheckConstraint("ck_translation_state", "\"State\" IN ('Pending','Leased','RetryScheduled','Completed','Failed','Cancelled')");
                    table.ForeignKey(
                        name: "FK_translation_workspace",
                        columns: x => new { x.WorkspaceId, x.OrganizationId },
                        principalTable: "workspace_organizations",
                        principalColumns: new[] { "workspace_id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "associations",
                schema: "swiftaid_translation",
                columns: table => new
                {
                    SourceEventId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReportRevision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_translation_associations", x => new { x.SourceEventId, x.ArtifactId });
                    table.ForeignKey(
                        name: "FK_translation_association_artifact",
                        column: x => x.ArtifactId,
                        principalSchema: "swiftaid_translation",
                        principalTable: "artifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_translation_association_case",
                        column: x => x.CaseId,
                        principalTable: "managed_cases",
                        principalColumn: "case_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_translation_association_event",
                        column: x => x.SourceEventId,
                        principalTable: "case_events",
                        principalColumn: "event_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "outbox",
                schema: "swiftaid_translation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceEventId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReviewVersion = table.Column<long>(type: "bigint", nullable: false),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseUntilUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AvailableAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PublishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    FailureCode = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_translation_outbox", x => x.Id);
                    table.ForeignKey(
                        name: "FK_translation_outbox_artifact",
                        column: x => x.ArtifactId,
                        principalSchema: "swiftaid_translation",
                        principalTable: "artifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_translation_outbox_event",
                        column: x => x.SourceEventId,
                        principalTable: "case_events",
                        principalColumn: "event_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "reviews",
                schema: "swiftaid_translation",
                columns: table => new
                {
                    DecisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<string>(type: "text", nullable: false),
                    Approved = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_translation_reviews", x => x.DecisionId);
                    table.ForeignKey(
                        name: "FK_translation_review_artifact",
                        column: x => x.ArtifactId,
                        principalSchema: "swiftaid_translation",
                        principalTable: "artifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_artifacts_WorkspaceId_OrganizationId",
                schema: "swiftaid_translation",
                table: "artifacts",
                columns: new[] { "WorkspaceId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_translation_fingerprint",
                schema: "swiftaid_translation",
                table: "artifacts",
                column: "Fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_translation_work",
                schema: "swiftaid_translation",
                table: "artifacts",
                columns: new[] { "State", "AvailableAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_associations_ArtifactId",
                schema: "swiftaid_translation",
                table: "associations",
                column: "ArtifactId");

            migrationBuilder.CreateIndex(
                name: "IX_translation_source_revision",
                schema: "swiftaid_translation",
                table: "associations",
                columns: new[] { "CaseId", "ReportRevision" });

            migrationBuilder.CreateIndex(
                name: "IX_outbox_SourceEventId",
                schema: "swiftaid_translation",
                table: "outbox",
                column: "SourceEventId");

            migrationBuilder.CreateIndex(
                name: "IX_translation_outbox_available",
                schema: "swiftaid_translation",
                table: "outbox",
                column: "AvailableAtUtc",
                filter: "\"PublishedAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_translation_replay_identity",
                schema: "swiftaid_translation",
                table: "outbox",
                columns: new[] { "ArtifactId", "SourceEventId", "ReviewVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_translation_review_version",
                schema: "swiftaid_translation",
                table: "reviews",
                columns: new[] { "ArtifactId", "Version" },
                unique: true);

            migrationBuilder.Sql("""
                CREATE FUNCTION swiftaid_translation.protect_artifact() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'Translation artifacts are immutable'; END IF;
                    IF ROW(NEW."Id", NEW."OrganizationId", NEW."WorkspaceId", NEW."Fingerprint", NEW."ContentHash",
                           NEW."SourceLanguage", NEW."ModelRevision", NEW."Configuration", NEW."ProtectedSource", NEW."CreatedAtUtc")
                       IS DISTINCT FROM
                       ROW(OLD."Id", OLD."OrganizationId", OLD."WorkspaceId", OLD."Fingerprint", OLD."ContentHash",
                           OLD."SourceLanguage", OLD."ModelRevision", OLD."Configuration", OLD."ProtectedSource", OLD."CreatedAtUtc")
                    THEN RAISE EXCEPTION 'Translation provenance is immutable'; END IF;
                    IF OLD."State" = 'Completed' AND
                       (to_jsonb(NEW) - ARRAY['ReviewStatus','ReviewVersion']) IS DISTINCT FROM
                       (to_jsonb(OLD) - ARRAY['ReviewStatus','ReviewVersion'])
                    THEN RAISE EXCEPTION 'Completed translations are immutable'; END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER translation_artifact_immutable BEFORE UPDATE OR DELETE ON swiftaid_translation.artifacts
                    FOR EACH ROW EXECUTE FUNCTION swiftaid_translation.protect_artifact();
                CREATE FUNCTION swiftaid_translation.append_only() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'Translation evidence is append only'; END $$;
                CREATE TRIGGER translation_association_immutable BEFORE UPDATE OR DELETE ON swiftaid_translation.associations
                    FOR EACH ROW EXECUTE FUNCTION swiftaid_translation.append_only();
                CREATE TRIGGER translation_review_immutable BEFORE UPDATE OR DELETE ON swiftaid_translation.reviews
                    FOR EACH ROW EXECUTE FUNCTION swiftaid_translation.append_only();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "associations",
                schema: "swiftaid_translation");

            migrationBuilder.DropTable(
                name: "outbox",
                schema: "swiftaid_translation");

            migrationBuilder.DropTable(
                name: "reviews",
                schema: "swiftaid_translation");

            migrationBuilder.DropTable(
                name: "artifacts",
                schema: "swiftaid_translation");
            migrationBuilder.Sql("DROP FUNCTION swiftaid_translation.protect_artifact(); DROP FUNCTION swiftaid_translation.append_only();");
        }
    }
}
