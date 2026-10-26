using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SwiftAid.Infrastructure.Persistence;

/// <summary>
/// Design-time construction uses only an explicitly supplied migration connection.
/// The value is never logged, and no repository or local default can target a database.
/// </summary>
public sealed class SwiftAidDbContextFactory : IDesignTimeDbContextFactory<SwiftAidDbContext>
{
    private const string ConnectionStringEnvironmentVariable = "ConnectionStrings__SwiftAid";

    public SwiftAidDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Set {ConnectionStringEnvironmentVariable} explicitly for EF design-time commands. No default database is configured.");
        }

        var options = new DbContextOptionsBuilder<SwiftAidDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new SwiftAidDbContext(options);
    }
}
