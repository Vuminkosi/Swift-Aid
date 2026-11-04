using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SwiftAid.Infrastructure.Persistence.Migrations;

[DbContext(typeof(SwiftAidDbContext))]
[Migration("20261104140000_HealthOfficerDesignations")]
public sealed class HealthOfficerDesignations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE health_officer_designations (
                designation_id uuid PRIMARY KEY,
                actor_id text NOT NULL REFERENCES actors(actor_id) ON DELETE RESTRICT,
                organization_id text NOT NULL REFERENCES organizations(organization_id) ON DELETE RESTRICT,
                country_code varchar(2) NOT NULL CHECK (country_code ~ '^[A-Z]{2}$'),
                sector text NOT NULL CHECK (sector IN ('HumanHealth', 'AnimalHealth')),
                status text NOT NULL CHECK (status IN ('Active', 'Suspended', 'Revoked')),
                valid_from_utc timestamptz NOT NULL,
                expires_at_utc timestamptz NOT NULL CHECK (expires_at_utc > valid_from_utc),
                designated_by_actor_id text NOT NULL REFERENCES actors(actor_id) ON DELETE RESTRICT,
                version bigint NOT NULL CHECK (version > 0),
                created_at_utc timestamptz NOT NULL,
                updated_at_utc timestamptz NOT NULL
            );
            CREATE UNIQUE INDEX ux_health_officer_designation_version
                ON health_officer_designations (actor_id, organization_id, country_code, sector, version);
            CREATE TRIGGER immutable_health_officer_designations BEFORE UPDATE OR DELETE ON health_officer_designations
                FOR EACH ROW EXECUTE FUNCTION swiftaid_classification.reject_artifact_change();
            CREATE INDEX ix_health_officer_designation_scope
                ON health_officer_designations (organization_id, country_code, sector, status, actor_id);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM health_officer_designations) THEN
                    RAISE EXCEPTION 'Cannot remove health officer designations.';
                END IF;
            END $$;
            DROP TABLE health_officer_designations;
            """);
    }
}
