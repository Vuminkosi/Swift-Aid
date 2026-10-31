using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SwiftAid.Infrastructure.Persistence;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    [DbContext(typeof(SwiftAidDbContext))]
    [Migration("20261031180000_RemoveEllaiGuestCycleLimit")]
    public sealed class RemoveEllaiGuestCycleLimit : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_ellai_sessions_cycles",
                table: "ellai_sessions");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ellai_sessions_cycles",
                table: "ellai_sessions",
                sql: "cycles_used >= 0 AND cycles_allowed >= 1");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_ellai_sessions_cycles",
                table: "ellai_sessions");

            migrationBuilder.Sql(
                "UPDATE ellai_sessions SET cycles_allowed = 12 WHERE cycles_allowed > 12");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ellai_sessions_cycles",
                table: "ellai_sessions",
                sql: "cycles_used >= 0 AND cycles_allowed BETWEEN 1 AND 12");
        }
    }
}
