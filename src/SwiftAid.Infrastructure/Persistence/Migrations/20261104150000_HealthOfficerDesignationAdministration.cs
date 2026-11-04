using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace SwiftAid.Infrastructure.Persistence.Migrations;

/// <summary>Explicit, scoped authority and immutable command receipts for officer designation.</summary>
[DbContext(typeof(SwiftAidDbContext))]
[Migration("20261104150000_HealthOfficerDesignationAdministration")]
public sealed class HealthOfficerDesignationAdministration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE health_officer_designation_administrators (
                administrator_event_id uuid PRIMARY KEY,
                actor_id text NOT NULL REFERENCES actors(actor_id) ON DELETE RESTRICT,
                organization_id text NOT NULL REFERENCES organizations(organization_id) ON DELETE RESTRICT,
                country_code varchar(2) NOT NULL CHECK (country_code ~ '^[A-Z]{2}$'),
                sector text NOT NULL CHECK (sector IN ('HumanHealth', 'AnimalHealth')),
                status text NOT NULL CHECK (status IN ('Active', 'Suspended', 'Revoked')),
                valid_from_utc timestamptz NOT NULL,
                expires_at_utc timestamptz NOT NULL CHECK (expires_at_utc > valid_from_utc),
                granted_by_actor_id text NOT NULL REFERENCES actors(actor_id) ON DELETE RESTRICT,
                version bigint NOT NULL CHECK (version > 0),
                recorded_at_utc timestamptz NOT NULL,
                CHECK (actor_id <> granted_by_actor_id),
                UNIQUE (actor_id, organization_id, country_code, sector, version)
            );
            CREATE TRIGGER immutable_officer_designation_administrators BEFORE UPDATE OR DELETE ON health_officer_designation_administrators
                FOR EACH ROW EXECUTE FUNCTION swiftaid_classification.reject_artifact_change();

            CREATE FUNCTION swiftaid_classification.guard_officer_designation_administrator() RETURNS trigger
            LANGUAGE plpgsql AS $$
            DECLARE latest bigint;
            BEGIN
                PERFORM pg_advisory_xact_lock(hashtextextended('officer-admin:' ||
                    jsonb_build_array(NEW.actor_id, NEW.organization_id, NEW.country_code, NEW.sector)::text, 0));
                SELECT COALESCE(MAX(version), 0) INTO latest FROM health_officer_designation_administrators
                    WHERE actor_id = NEW.actor_id AND organization_id = NEW.organization_id
                        AND country_code = NEW.country_code AND sector = NEW.sector;
                IF NEW.version <> latest + 1 THEN RAISE EXCEPTION 'Invalid officer designation administrator version.'; END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER guard_officer_designation_administrator BEFORE INSERT ON health_officer_designation_administrators
                FOR EACH ROW EXECUTE FUNCTION swiftaid_classification.guard_officer_designation_administrator();

            CREATE FUNCTION swiftaid_classification.guard_officer_designation_version() RETURNS trigger
            LANGUAGE plpgsql AS $$
            DECLARE latest bigint;
            BEGIN
                PERFORM pg_advisory_xact_lock(hashtextextended('officer-designation:' ||
                    jsonb_build_array(NEW.actor_id, NEW.organization_id, NEW.country_code, NEW.sector)::text, 0));
                SELECT COALESCE(MAX(version), 0) INTO latest FROM health_officer_designations
                    WHERE actor_id = NEW.actor_id AND organization_id = NEW.organization_id
                        AND country_code = NEW.country_code AND sector = NEW.sector;
                IF NEW.version <> latest + 1 THEN RAISE EXCEPTION 'Invalid officer designation version.'; END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER guard_officer_designation_version BEFORE INSERT ON health_officer_designations
                FOR EACH ROW EXECUTE FUNCTION swiftaid_classification.guard_officer_designation_version();

            CREATE TABLE health_officer_designation_commands (
                command_id uuid PRIMARY KEY,
                administrator_actor_id text NOT NULL REFERENCES actors(actor_id) ON DELETE RESTRICT,
                target_actor_id text NOT NULL REFERENCES actors(actor_id) ON DELETE RESTRICT,
                organization_id text NOT NULL REFERENCES organizations(organization_id) ON DELETE RESTRICT,
                country_code varchar(2) NOT NULL,
                sector text NOT NULL,
                status text NOT NULL,
                valid_from_utc timestamptz NOT NULL,
                expires_at_utc timestamptz NOT NULL,
                expected_version bigint NOT NULL CHECK (expected_version >= 0),
                designation_id uuid NOT NULL REFERENCES health_officer_designations(designation_id) ON DELETE RESTRICT,
                designation_version bigint NOT NULL CHECK (designation_version > 0),
                recorded_at_utc timestamptz NOT NULL
            );
            CREATE TRIGGER immutable_officer_designation_commands BEFORE UPDATE OR DELETE ON health_officer_designation_commands
                FOR EACH ROW EXECUTE FUNCTION swiftaid_classification.reject_artifact_change();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM health_officer_designation_commands) OR EXISTS (SELECT 1 FROM health_officer_designation_administrators) THEN
                    RAISE EXCEPTION 'Cannot remove officer designation administration records.';
                END IF;
            END $$;
            DROP TABLE health_officer_designation_commands;
            DROP TABLE health_officer_designation_administrators;
            DROP FUNCTION swiftaid_classification.guard_officer_designation_administrator();
            DROP TRIGGER guard_officer_designation_version ON health_officer_designations;
            DROP FUNCTION swiftaid_classification.guard_officer_designation_version();
            """);
    }
}
