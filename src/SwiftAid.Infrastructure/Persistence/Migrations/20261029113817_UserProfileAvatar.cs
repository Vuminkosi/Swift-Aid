using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UserProfileAvatar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "avatar_content_type",
                schema: "swiftaid_identity",
                table: "user_profiles",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "avatar_object_id",
                schema: "swiftaid_identity",
                table: "user_profiles",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "avatar_content_type",
                schema: "swiftaid_identity",
                table: "user_profiles");

            migrationBuilder.DropColumn(
                name: "avatar_object_id",
                schema: "swiftaid_identity",
                table: "user_profiles");
        }
    }
}
