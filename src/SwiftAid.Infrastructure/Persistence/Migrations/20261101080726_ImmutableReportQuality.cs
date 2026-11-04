using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SwiftAid.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ImmutableReportQuality : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TRIGGER immutable_report_quality BEFORE UPDATE OR DELETE ON swiftaid_classification.quality_assessments
                    FOR EACH ROW EXECUTE FUNCTION swiftaid_classification.reject_artifact_change();
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER immutable_report_quality ON swiftaid_classification.quality_assessments;");

        }
    }
}
