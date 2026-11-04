using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ImmutableSurveillanceEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TRIGGER immutable_surveillance_assessment BEFORE UPDATE OR DELETE ON swiftaid_surveillance.assessments
                    FOR EACH ROW EXECUTE FUNCTION swiftaid_classification.reject_artifact_change();
                CREATE TRIGGER immutable_surveillance_evidence BEFORE UPDATE OR DELETE ON swiftaid_surveillance.evidence
                    FOR EACH ROW EXECUTE FUNCTION swiftaid_classification.reject_artifact_change();
                CREATE TRIGGER immutable_surveillance_review BEFORE UPDATE OR DELETE ON swiftaid_surveillance.reviews
                    FOR EACH ROW EXECUTE FUNCTION swiftaid_classification.reject_artifact_change();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER immutable_surveillance_assessment ON swiftaid_surveillance.assessments;
                DROP TRIGGER immutable_surveillance_evidence ON swiftaid_surveillance.evidence;
                DROP TRIGGER immutable_surveillance_review ON swiftaid_surveillance.reviews;
                """);
        }
    }
}
