using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SwiftAid.Infrastructure.Persistence.Migrations;

[DbContext(typeof(SwiftAidDbContext))]
[Migration("20261104230000_EllaiOperationalScheduleConfigurationRevision")]
public sealed class EllaiOperationalScheduleConfigurationRevision : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE ellai_operational_schedules
            ADD COLUMN configuration_revision bigint NOT NULL DEFAULT 1 CHECK (configuration_revision > 0);
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM ellai_operational_schedules WHERE configuration_revision <> 1) THEN
                RAISE EXCEPTION 'Cannot remove operational configuration revisions while newer schedules exist.';
            END IF;
        END $$;
        ALTER TABLE ellai_operational_schedules DROP COLUMN configuration_revision;
        """);
}
