using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrganizationOwner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "owner_actor_id",
                table: "organizations",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_organizations_owner_actor_id",
                table: "organizations",
                column: "owner_actor_id");

            migrationBuilder.AddForeignKey(
                name: "fk_organizations_owner_actor",
                table: "organizations",
                column: "owner_actor_id",
                principalTable: "actors",
                principalColumn: "actor_id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_organizations_owner_actor",
                table: "organizations");

            migrationBuilder.DropIndex(
                name: "IX_organizations_owner_actor_id",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "owner_actor_id",
                table: "organizations");
        }
    }
}
