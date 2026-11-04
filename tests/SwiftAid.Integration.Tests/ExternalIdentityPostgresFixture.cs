using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using OpenIddict.EntityFrameworkCore;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Integration.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class IdentityPostgresFactAttribute : FactAttribute
{
    public IdentityPostgresFactAttribute() => SetSkipIfNotEnabled();

    internal static void SetSkipIfNotEnabled(Action<string?> setSkip)
    {
        if (ExternalIdentityPostgresFixture.HasLegacyFixtureConfiguration)
        {
            return;
        }

        if (!ExternalIdentityPostgresFixture.IsEnabled)
        {
            setSkip("Set SWIFTAID_RUN_IDENTITY_POSTGRES_FIXTURES=1 and provide the isolated identity PostgreSQL connection.");
        }
    }

    private void SetSkipIfNotEnabled() => SetSkipIfNotEnabled(value => Skip = value);
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class IdentityPostgresTheoryAttribute : TheoryAttribute
{
    public IdentityPostgresTheoryAttribute() => IdentityPostgresFactAttribute.SetSkipIfNotEnabled(value => Skip = value);
}

/// <summary>
/// Connects only to the disposable task-owned identity PostgreSQL container. The verification
/// lane owns creation and removal of its isolated network/container; this fixture never starts
/// Docker, uses a volume, or falls back to the shared SQLAUTH fixture.
/// </summary>
internal sealed class ExternalIdentityPostgresFixture : IAsyncDisposable
{
    public const string ConnectionEnvironmentVariable = "SWIFTAID_TEST_IDENTITY_POSTGRES_CONNECTION";
    public const string RunEnvironmentVariable = "SWIFTAID_RUN_IDENTITY_POSTGRES_FIXTURES";

    private const string ExpectedHost = "swiftaid-identity-postgres";
    private const int ExpectedPort = 5432;
    private const string ExpectedDatabase = "swiftaid_identity_test";
    private const string ExpectedUsername = "swiftaid_identity_test";

    private static readonly SemaphoreSlim MigrationGate = new(1, 1);

    private ExternalIdentityPostgresFixture(string connectionString)
    {
        ConnectionString = connectionString;
    }

    public string ConnectionString { get; }

    public static bool IsEnabled =>
        string.Equals(Environment.GetEnvironmentVariable(RunEnvironmentVariable), "1", StringComparison.Ordinal);

    public static bool HasLegacyFixtureConfiguration =>
        Environment.GetEnvironmentVariable("SWIFTAID_TEST_POSTGRES_CONNECTION") is not null ||
        string.Equals(Environment.GetEnvironmentVariable("SWIFTAID_RUN_SQLAUTH_FIXTURES"), "1", StringComparison.Ordinal);

    public static async Task<ExternalIdentityPostgresFixture> CreateAsync(
        CancellationToken cancellationToken = default)
    {
        if (HasLegacyFixtureConfiguration)
        {
            throw new InvalidOperationException(
                "Identity mapping tests refuse the legacy SQLAUTH fixture connection and run flag.");
        }

        if (!IsEnabled)
        {
            throw new InvalidOperationException(
                $"Set {RunEnvironmentVariable}=1 to run against the isolated identity PostgreSQL container.");
        }

        var configuredConnection = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredConnection))
        {
            throw new InvalidOperationException(
                $"The isolated identity test connection must be provided through {ConnectionEnvironmentVariable}.");
        }

        var connection = ValidateConnection(configuredConnection);
        var fixture = new ExternalIdentityPostgresFixture(connection.ConnectionString);
        await fixture.AssertTargetAsync(cancellationToken).ConfigureAwait(false);

        await MigrationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var dbContext = fixture.CreateDbContext();
            await dbContext.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            MigrationGate.Release();
        }

        return fixture;
    }

    public SwiftAidDbContext CreateDbContext(DbCommandInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<SwiftAidDbContext>()
            .UseNpgsql(ConnectionString)
            .UseOpenIddict();

        if (interceptor is not null)
        {
            options.AddInterceptors(interceptor);
        }

        return new SwiftAidDbContext(options.Options);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task AssertTargetAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT current_database(), current_user";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            !string.Equals(reader.GetString(0), ExpectedDatabase, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(1), ExpectedUsername, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The identity test connection did not resolve to its dedicated database and role.");
        }
    }

    private static NpgsqlConnectionStringBuilder ValidateConnection(string value)
    {
        var connection = new NpgsqlConnectionStringBuilder(value);
        if (!string.Equals(connection.Host, ExpectedHost, StringComparison.Ordinal) ||
            connection.Port != ExpectedPort ||
            !string.Equals(connection.Database, ExpectedDatabase, StringComparison.Ordinal) ||
            !string.Equals(connection.Username, ExpectedUsername, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(connection.Password))
        {
            throw new InvalidOperationException(
                $"The identity test database must target {ExpectedHost}:{ExpectedPort}/{ExpectedDatabase} as {ExpectedUsername} and include a password.");
        }

        connection.Pooling = false;
        connection.Timeout = Math.Clamp(connection.Timeout, 1, 10);
        connection.CommandTimeout = Math.Clamp(connection.CommandTimeout, 1, 30);
        connection.ApplicationName = "swiftaid-identity-policy-tests";
        return connection;
    }
}
