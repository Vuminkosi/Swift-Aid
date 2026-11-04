using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DurableCaseImports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "swiftaid_ingestion");

            migrationBuilder.CreateTable(
                name: "case_imports",
                schema: "swiftaid_ingestion",
                columns: table => new
                {
                    ImportId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<string>(type: "text", maxLength: 200, nullable: false),
                    WorkspaceId = table.Column<string>(type: "text", maxLength: 200, nullable: false),
                    CountryCode = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    ContextHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    NextRow = table.Column<long>(type: "bigint", nullable: false),
                    FinalRow = table.Column<long>(type: "bigint", nullable: true),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseUntilUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_case_imports", x => x.ImportId);
                    table.CheckConstraint("ck_case_import_cursor", "\"NextRow\" > 0 AND (\"FinalRow\" IS NULL OR \"FinalRow\" >= 0)");
                    table.CheckConstraint("ck_case_import_hash", "octet_length(\"ContextHash\") = 32");
                    table.CheckConstraint("ck_case_import_lease", "(\"LeaseId\" IS NULL) = (\"LeaseUntilUtc\" IS NULL)");
                    table.CheckConstraint("ck_case_import_state", "\"State\" IN ('Running','Paused','Completed','Failed')");
                    table.ForeignKey(
                        name: "FK_case_imports_actors_ActorId",
                        column: x => x.ActorId,
                        principalTable: "actors",
                        principalColumn: "actor_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_case_imports_workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "workspaces",
                        principalColumn: "workspace_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "case_import_rows",
                schema: "swiftaid_ingestion",
                columns: table => new
                {
                    ImportId = table.Column<Guid>(type: "uuid", nullable: false),
                    RowNumber = table.Column<long>(type: "bigint", nullable: false),
                    CommandId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    ProtectedSource = table.Column<byte[]>(type: "bytea", nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    CaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_case_import_rows", x => new { x.ImportId, x.RowNumber });
                    table.CheckConstraint("ck_case_import_row_bounds", "\"RowNumber\" > 0 AND \"Attempts\" >= 0 AND octet_length(\"SourceHash\") = 32 AND octet_length(\"ProtectedSource\") BETWEEN 1 AND 524288");
                    table.CheckConstraint("ck_case_import_row_state", "\"State\" IN ('Processing','Accepted','Replayed','Invalid','Denied','Conflict','RetryableFailure','Failed')");
                    table.ForeignKey(
                        name: "FK_case_import_rows_case_imports_ImportId",
                        column: x => x.ImportId,
                        principalSchema: "swiftaid_ingestion",
                        principalTable: "case_imports",
                        principalColumn: "ImportId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_case_import_rows_ImportId_State",
                schema: "swiftaid_ingestion",
                table: "case_import_rows",
                columns: new[] { "ImportId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_case_imports_ActorId_UpdatedAtUtc",
                schema: "swiftaid_ingestion",
                table: "case_imports",
                columns: new[] { "ActorId", "UpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_case_imports_WorkspaceId",
                schema: "swiftaid_ingestion",
                table: "case_imports",
                column: "WorkspaceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "case_import_rows",
                schema: "swiftaid_ingestion");

            migrationBuilder.DropTable(
                name: "case_imports",
                schema: "swiftaid_ingestion");
        }
    }
}
