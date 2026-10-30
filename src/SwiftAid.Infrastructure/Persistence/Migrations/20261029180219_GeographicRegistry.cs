using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GeographicRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "swiftaid_geo");

            migrationBuilder.CreateTable(
                name: "locations",
                schema: "swiftaid_geo",
                columns: table => new
                {
                    location_id = table.Column<string>(type: "text", nullable: false),
                    country_code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    location_kind = table.Column<string>(type: "text", nullable: false),
                    parent_id = table.Column<string>(type: "text", nullable: true),
                    latitude = table.Column<double>(type: "double precision", nullable: true),
                    longitude = table.Column<double>(type: "double precision", nullable: true),
                    source = table.Column<string>(type: "text", nullable: false),
                    verification = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    imported_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_geo_locations", x => x.location_id);
                    table.CheckConstraint("ck_geo_locations_ids_nonempty", "length(btrim(location_id)) > 0 AND length(btrim(name)) > 0 AND length(btrim(country_code)) = 2");
                    table.CheckConstraint("ck_geo_locations_kind", "location_kind IN ('Country', 'Region', 'Province', 'Settlement')");
                    table.ForeignKey(
                        name: "fk_geo_locations_parent",
                        column: x => x.parent_id,
                        principalSchema: "swiftaid_geo",
                        principalTable: "locations",
                        principalColumn: "location_id",
                        onDelete: ReferentialAction.Restrict);
                });

            // NOTE: organization_invitations is created by the earlier
            // OrganizationInvitations migration; it must not be recreated
            // here (duplicate CreateTable broke fresh-database migration).

            migrationBuilder.CreateIndex(
                name: "ix_geo_locations_country_name",
                schema: "swiftaid_geo",
                table: "locations",
                columns: new[] { "country_code", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_geo_locations_parent",
                schema: "swiftaid_geo",
                table: "locations",
                column: "parent_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "locations",
                schema: "swiftaid_geo");
        }
    }
}
