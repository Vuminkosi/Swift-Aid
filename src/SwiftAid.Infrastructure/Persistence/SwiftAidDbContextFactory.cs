using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using OpenIddict.EntityFrameworkCore;

namespace SwiftAid.Infrastructure.Persistence;

/// <summary>
/// Provides the same single-context model to EF tooling without starting the Gateway,
/// reading deployment secrets, or connecting to a database.
/// </summary>
public sealed class SwiftAidDbContextFactory : IDesignTimeDbContextFactory<SwiftAidDbContext>
{
    public SwiftAidDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SwiftAidDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=swiftaid_design;Username=swiftaid_design")
            .UseOpenIddict()
            .Options;
        return new SwiftAidDbContext(options);
    }
}
