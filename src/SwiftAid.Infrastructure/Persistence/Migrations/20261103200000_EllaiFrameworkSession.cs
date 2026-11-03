using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SwiftAid.Infrastructure.Persistence.Migrations;

[DbContext(typeof(SwiftAidDbContext))]
[Migration("20261103200000_EllaiFrameworkSession")]
public sealed class EllaiFrameworkSession : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "framework_session_json", table: "ellai_utterances", type: "jsonb", nullable: true);
        migrationBuilder.AddCheckConstraint("ck_ellai_utterances_framework_session_size", "ellai_utterances",
            "framework_session_json IS NULL OR octet_length(framework_session_json::text) <= 1048576");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint("ck_ellai_utterances_framework_session_size", "ellai_utterances");
        migrationBuilder.DropColumn("framework_session_json", "ellai_utterances");
    }
}
