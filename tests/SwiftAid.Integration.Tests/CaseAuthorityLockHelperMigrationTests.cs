using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NpgsqlTypes;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Integration.Tests;

[Collection(CaseAuthorityPostgresCollection.Name)]
public sealed class CaseAuthorityLockHelperMigrationTests
{
    private const string InitialMigration = "20261026220110_P21InitialPersistence";
    private const string LockHelperMigration = "20261027000112_P21BLockTokenAndCaseOperations";

    private static readonly (string Table, string Constraint)[] LockTokenTables =
    [
        ("organizations", "ck_organizations_lock_token_zero"),
        ("workspaces", "ck_workspaces_lock_token_zero"),
        ("actor_access_states", "ck_actor_access_states_lock_token_zero"),
        ("actors", "ck_actors_lock_token_zero"),
        ("organization_memberships", "ck_organization_memberships_lock_token_zero"),
        ("access_grant_groups", "ck_access_grant_groups_lock_token_zero"),
        ("access_grant_group_regions", "ck_access_grant_group_regions_lock_token_zero"),
        ("access_grant_group_operations", "ck_access_grant_group_operations_lock_token_zero"),
        ("case_shares", "ck_case_shares_lock_token_zero"),
        ("managed_cases", "ck_managed_cases_lock_token_zero"),
        ("managed_case_regions", "ck_managed_case_regions_lock_token_zero")
    ];

    private static readonly string[] PreviouslyAllowedOperations =
    [
        "ReadCase", "ReadAttachment", "Synchronize", "Search", "Export", "Count",
        "RequestProtectedAction", "ExecuteProtectedAction", "ReadLinkedSignalEvidence"
    ];

    private static readonly string[] NewlyAllowedOperations =
    ["CorrectReport", "ReviewProtectedAction", "AssignCase", "ResolveCase", "ReopenCase"];

    [CaseAuthorityPostgresFact]
    public async Task Migration_preserves_preexisting_internal_schema_and_validates_lock_contract()
    {
        var connectionString = GetValidatedConnectionString();
        var options = new DbContextOptionsBuilder<SwiftAidDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        await using var context = new SwiftAidDbContext(options);
        var migrator = context.GetService<IMigrator>();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await RequirePostgreSql17Async(connection);

        await migrator.MigrateAsync(InitialMigration);
        var schemaOid = await EnsurePreexistingEmptyInternalSchemaAsync(connection);

        await migrator.MigrateAsync(LockHelperMigration);
        Assert.Equal(schemaOid, await GetInternalSchemaOidAsync(connection));
        await AssertLockTokenSchemaAsync(connection);
        await AssertOperationConstraintAsync(connection, PreviouslyAllowedOperations, NewlyAllowedOperations);
        await AssertUnknownOperationRejectedAsync(connection);

        await migrator.MigrateAsync(InitialMigration);
        Assert.Equal(schemaOid, await GetInternalSchemaOidAsync(connection));
        await AssertLockTokenColumnsAbsentAsync(connection);

        await migrator.MigrateAsync(LockHelperMigration);
        Assert.Equal(schemaOid, await GetInternalSchemaOidAsync(connection));
        await AssertLockTokenSchemaAsync(connection);
        await AssertHelperLeastPrivilegeAsync(connectionString, schemaOid);
    }

    [CaseAuthorityPostgresFact]
    public async Task Down_guard_aborts_without_mutation_for_new_grants_or_installed_helper()
    {
        var connectionString = GetValidatedConnectionString();
        var options = new DbContextOptionsBuilder<SwiftAidDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        await using var context = new SwiftAidDbContext(options);
        var migrator = context.GetService<IMigrator>();
        var downScript = migrator.GenerateScript(
            LockHelperMigration,
            InitialMigration,
            MigrationsSqlGenerationOptions.Default);
        var guardSql = ExtractDownGuard(downScript);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await RequirePostgreSql17Async(connection);

        var schemaOid = await GetInternalSchemaOidAsync(connection);
        Assert.NotNull(schemaOid);

        await AssertGuardRejectsNewOperationGrantAsync(connection, guardSql);
        await AssertGuardRejectsInstalledHelperAsync(connection, guardSql);

        var applied = await context.Database.GetAppliedMigrationsAsync();
        Assert.Contains(LockHelperMigration, applied);
        Assert.Equal(schemaOid, await GetInternalSchemaOidAsync(connection));
        await AssertLockTokenSchemaAsync(connection);
    }

    private static string GetValidatedConnectionString()
    {
        var configured = Environment.GetEnvironmentVariable("SWIFTAID_TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException("The opt-in migration fixture requires its private PostgreSQL connection setting.");
        }

        var builder = new NpgsqlConnectionStringBuilder(configured);
        if (builder.Host is not ("127.0.0.1" or "localhost" or "::1")
            || builder.Port != 32703
            || builder.Database != "swiftaid_p21a"
            || builder.Username != "p21_migration_owner"
            || string.IsNullOrEmpty(builder.Password))
        {
            throw new InvalidOperationException("The migration fixture accepts only the designated loopback disposable PostgreSQL database.");
        }

        builder.Timeout = Math.Clamp(builder.Timeout, 1, 5);
        builder.CommandTimeout = Math.Clamp(builder.CommandTimeout, 1, 10);
        return builder.ConnectionString;
    }

    private static async Task RequirePostgreSql17Async(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("SELECT current_setting('server_version_num')::integer", connection);
        var versionNumber = (int)(await command.ExecuteScalarAsync() ?? 0);
        Assert.True(
            versionNumber / 10_000 == 17,
            "PostgreSQL 17 is required; major 18+ lock semantics are not yet supported.");
    }

    private static async Task<uint> EnsurePreexistingEmptyInternalSchemaAsync(NpgsqlConnection connection)
    {
        await using (var inspect = new NpgsqlCommand(
            """
            SELECT namespace.oid::oid,
                   owner.rolname,
                   EXISTS (
                       SELECT 1
                         FROM pg_catalog.pg_depend AS dependency
                        WHERE dependency.refclassid = 'pg_catalog.pg_namespace'::pg_catalog.regclass
                          AND dependency.refobjid = namespace.oid
                          AND dependency.deptype <> 'p'
                   ),
                   EXISTS (
                       SELECT 1
                         FROM pg_catalog.aclexplode(namespace.nspacl) AS privilege
                        WHERE privilege.grantee <> namespace.nspowner
                   )
              FROM pg_catalog.pg_namespace AS namespace
              JOIN pg_catalog.pg_roles AS owner ON owner.oid = namespace.nspowner
             WHERE namespace.nspname = 'swiftaid_internal'
            """,
            connection))
        await using (var reader = await inspect.ExecuteReaderAsync())
        {
            if (await reader.ReadAsync())
            {
                var oid = reader.GetFieldValue<uint>(0);
                Assert.Equal("p21_migration_owner", reader.GetString(1));
                Assert.False(reader.GetBoolean(2), "The internal schema must be empty before the migration test starts.");
                Assert.False(reader.GetBoolean(3), "The internal schema must not contain non-owner grants.");
                return oid;
            }
        }

        await using var create = new NpgsqlCommand("CREATE SCHEMA swiftaid_internal AUTHORIZATION CURRENT_USER", connection);
        await create.ExecuteNonQueryAsync();
        return (await GetInternalSchemaOidAsync(connection))
            ?? throw new InvalidOperationException("The disposable database did not retain the newly created internal schema.");
    }

    private static async Task<uint?> GetInternalSchemaOidAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(
            "SELECT oid::oid FROM pg_catalog.pg_namespace WHERE nspname = 'swiftaid_internal'",
            connection);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : (uint)value;
    }

    private static async Task AssertLockTokenSchemaAsync(NpgsqlConnection connection)
    {
        foreach (var (table, constraint) in LockTokenTables)
        {
            await using var command = new NpgsqlCommand(
                $"""
                SELECT attribute.atttypid = 'pg_catalog.int2'::pg_catalog.regtype,
                       attribute.attnotnull,
                       pg_catalog.pg_get_expr(default_value.adbin, default_value.adrelid),
                       check_constraint.convalidated,
                       check_constraint.conislocal,
                       pg_catalog.pg_get_expr(check_constraint.conbin, check_constraint.conrelid),
                       (SELECT count(*) FROM public."{table}" WHERE lock_token <> 0)
                  FROM pg_catalog.pg_attribute AS attribute
                  JOIN pg_catalog.pg_class AS relation ON relation.oid = attribute.attrelid
                  JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = relation.relnamespace
                  LEFT JOIN pg_catalog.pg_attrdef AS default_value
                    ON default_value.adrelid = relation.oid AND default_value.adnum = attribute.attnum
                  LEFT JOIN pg_catalog.pg_constraint AS check_constraint
                    ON check_constraint.conrelid = relation.oid
                   AND check_constraint.conname = @constraint
                   AND check_constraint.contype = 'c'
                 WHERE namespace.nspname = 'public'
                   AND relation.relname = @table
                   AND attribute.attname = 'lock_token'
                   AND NOT attribute.attisdropped
                """,
                connection);
            command.Parameters.AddWithValue("constraint", constraint);
            command.Parameters.AddWithValue("table", table);
            await using var reader = await command.ExecuteReaderAsync();

            Assert.True(await reader.ReadAsync(), $"Missing {table}.lock_token or its exact constraint.");
            Assert.True(reader.GetBoolean(0), $"{table}.lock_token must be smallint.");
            Assert.True(reader.GetBoolean(1), $"{table}.lock_token must be NOT NULL.");
            Assert.Equal("0", NormalizeSqlExpression(reader.GetString(2)));
            Assert.True(reader.GetBoolean(3), $"{constraint} must be validated.");
            Assert.True(reader.GetBoolean(4), $"{constraint} must be a local table constraint.");
            Assert.Equal("lock_token=0", NormalizeSqlExpression(reader.GetString(5)));
            Assert.Equal(0L, reader.GetInt64(6));
            Assert.False(await reader.ReadAsync(), $"Unexpected duplicate lock_token metadata for {table}.");
        }
    }

    private static async Task AssertLockTokenColumnsAbsentAsync(NpgsqlConnection connection)
    {
        foreach (var (table, _) in LockTokenTables)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT count(*)
                  FROM pg_catalog.pg_attribute AS attribute
                  JOIN pg_catalog.pg_class AS relation ON relation.oid = attribute.attrelid
                  JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = relation.relnamespace
                 WHERE namespace.nspname = 'public'
                   AND relation.relname = @table
                   AND attribute.attname = 'lock_token'
                   AND NOT attribute.attisdropped
                """,
                connection);
            command.Parameters.AddWithValue("table", table);
            Assert.Equal(0L, (long)(await command.ExecuteScalarAsync() ?? -1L));
        }
    }

    private static async Task AssertOperationConstraintAsync(
        NpgsqlConnection connection,
        IEnumerable<string> oldOperations,
        IEnumerable<string> newOperations)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        var grant = await SeedGrantAsync(connection, transaction, "migration-contract");

        foreach (var operation in oldOperations.Concat(newOperations))
        {
            await ExecuteAsync(
                connection,
                transaction,
                "INSERT INTO public.access_grant_group_operations (grant_group_id, operation) VALUES (@grant, @operation)",
                ("grant", grant),
                ("operation", operation));
        }

        await transaction.RollbackAsync();
    }

    private static async Task AssertUnknownOperationRejectedAsync(NpgsqlConnection connection)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        var grant = await SeedGrantAsync(connection, transaction, "unknown-operation");
        await using (var createSavepoint = new NpgsqlCommand("SAVEPOINT unknown_operation", connection, transaction))
        {
            await createSavepoint.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            connection,
            transaction,
            "INSERT INTO public.access_grant_group_operations (grant_group_id, operation) VALUES (@grant, 'UnknownCaseOperation')",
            ("grant", grant)));
        Assert.Equal("23514", exception.SqlState);

        await using (var rollbackSavepoint = new NpgsqlCommand("ROLLBACK TO SAVEPOINT unknown_operation", connection, transaction))
        {
            await rollbackSavepoint.ExecuteNonQueryAsync();
        }

        await transaction.RollbackAsync();
    }

    private static async Task AssertGuardRejectsNewOperationGrantAsync(NpgsqlConnection connection, string guardSql)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        var grant = await SeedGrantAsync(connection, transaction, "down-guard");
        await ExecuteAsync(connection, transaction,
            "INSERT INTO public.access_grant_group_operations (grant_group_id, operation) VALUES (@grant, 'CorrectReport')",
            ("grant", grant));

        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, transaction, guardSql));
        Assert.Equal("P0001", exception.SqlState);
        Assert.Contains("grant uses a newly added case operation", exception.Message, StringComparison.OrdinalIgnoreCase);
        await transaction.RollbackAsync();
    }

    private static async Task AssertGuardRejectsInstalledHelperAsync(NpgsqlConnection connection, string guardSql)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        var helperExists = await FunctionExistsAsync(connection, transaction);
        if (!helperExists)
        {
            await using var install = new NpgsqlCommand(
                "CREATE FUNCTION swiftaid_internal.lock_case_authority(uuid, text, text[], boolean) RETURNS smallint LANGUAGE sql AS 'SELECT 1::smallint'",
                connection,
                transaction);
            await install.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, transaction, guardSql));
        Assert.Equal("P0001", exception.SqlState);
        Assert.Contains("case authority lock helper is installed", exception.Message, StringComparison.OrdinalIgnoreCase);
        await transaction.RollbackAsync();
    }

    private static async Task<bool> FunctionExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_catalog.to_regprocedure('swiftaid_internal.lock_case_authority(uuid,text,text[],boolean)') IS NOT NULL",
            connection,
            transaction);
        return (bool)(await command.ExecuteScalarAsync() ?? false);
    }

    private static async Task AssertHelperLeastPrivilegeAsync(string ownerConnectionString, uint expectedSchemaOid)
    {
        var ownerBuilder = new NpgsqlConnectionStringBuilder(ownerConnectionString);
        var suffix = Guid.NewGuid().ToString("N");
        var helperRole = $"sa_lock_helper_{suffix}";
        var runtimeRole = $"sa_lock_runtime_{suffix}";
        var runtimePassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        await using (var connection = new NpgsqlConnection(ownerConnectionString))
        {
            await connection.OpenAsync();
            var publicProbeHelper = $"sa_lock_public_probe_{suffix}";
            var publicProbeRuntime = $"sa_lock_public_runtime_{suffix}";
            var columnProbeHelper = $"sa_lock_column_probe_{suffix}";
            var columnProbeRuntime = $"sa_lock_column_runtime_{suffix}";
            try
            {
                await CreateRoleAsync(connection, helperRole, login: false);
                await CreateRoleAsync(connection, runtimeRole, login: true, runtimePassword);
                await CreateRoleAsync(connection, publicProbeHelper, login: false);
                await CreateRoleAsync(connection, publicProbeRuntime, login: true, NewRandomPassword());
                await AssertProvisioningPreflightRollsBackAsync(
                    ownerBuilder,
                    publicProbeHelper,
                    publicProbeRuntime,
                    "BEGIN; GRANT UPDATE ON public.actors TO PUBLIC;\n",
                    connection,
                    "SELECT EXISTS (SELECT 1 FROM pg_catalog.aclexplode(relation.relacl) AS privilege WHERE privilege.grantee = 0 AND privilege.privilege_type = 'UPDATE') FROM pg_catalog.pg_class AS relation JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = relation.relnamespace WHERE namespace.nspname = 'public' AND relation.relname = 'actors'",
                    "helper_owner has effective privileges beyond the lock-helper profile");

                await CreateRoleAsync(connection, columnProbeHelper, login: false);
                await CreateRoleAsync(connection, columnProbeRuntime, login: true, NewRandomPassword());
                await AssertProvisioningPreflightRollsBackAsync(
                    ownerBuilder,
                    columnProbeHelper,
                    columnProbeRuntime,
                    $"BEGIN; GRANT UPDATE (status) ON public.actors TO {QuoteIdentifier(columnProbeHelper)};\n",
                    connection,
                    "SELECT pg_catalog.has_column_privilege(@role, 'public.actors', 'status', 'UPDATE')",
                    "helper_owner has effective privileges beyond the lock-helper profile");

                await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync();
                var scriptPath = FindRepositoryFile("infra/postgres/case-authority-lock-helper.sql");
                var provisionExitCode = await RunPsqlAsync(ownerBuilder, helperRole, runtimeRole, scriptPath, prefixSql: null);
                Assert.True(provisionExitCode.ExitCode == 0, FormatPsqlFailure(provisionExitCode));

                var runtimeBuilder = new NpgsqlConnectionStringBuilder(ownerConnectionString)
                {
                    Username = runtimeRole,
                    Password = runtimePassword,
                    Pooling = false,
                    Timeout = 5,
                    CommandTimeout = 10
                };

                await using (var runtimeConnection = new NpgsqlConnection(runtimeBuilder.ConnectionString))
                {
                    await runtimeConnection.OpenAsync();
                    await AssertRuntimeAuthorizationAsync(runtimeConnection, fixture);
                }
            }
            finally
            {
                await CleanupTaskArtifactsAsync(
                    connection,
                    suffix,
                    helperRole,
                    runtimeRole,
                    publicProbeHelper,
                    publicProbeRuntime,
                    columnProbeHelper,
                    columnProbeRuntime,
                    expectedSchemaOid);
                await AssertHelperFunctionAndGrantsRemovedAsync(connection, expectedSchemaOid);
            }
        }
    }

    private static string NewRandomPassword() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    private static async Task CreateRoleAsync(
        NpgsqlConnection connection,
        string role,
        bool login,
        string? password = null)
    {
        var rolePassword = login
            ? password ?? throw new InvalidOperationException("A random password is required for a task-owned LOGIN role.")
            : null;
        var createSql = await GetFormattedRoleSqlAsync(connection, role, rolePassword);

        await using var command = new NpgsqlCommand(createSql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> GetFormattedRoleSqlAsync(
        NpgsqlConnection connection,
        string role,
        string? password)
    {
        await using var command = new NpgsqlCommand(
            password is null
                ? "SELECT pg_catalog.format('CREATE ROLE %I NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS', @role)"
                : "SELECT pg_catalog.format('CREATE ROLE %I LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS', @role, @password)",
            connection);
        command.Parameters.AddWithValue("role", role);
        if (password is not null)
        {
            command.Parameters.AddWithValue("password", password);
        }

        return (string)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("PostgreSQL did not produce the role-creation command."));
    }

    private static async Task CleanupTaskArtifactsAsync(
        NpgsqlConnection connection,
        string suffix,
        string helperRole,
        string runtimeRole,
        string publicProbeHelper,
        string publicProbeRuntime,
        string columnProbeHelper,
        string columnProbeRuntime,
        uint expectedSchemaOid)
    {
        var expectedSuffix = $"_{suffix}";
        var roles = new[]
        {
            helperRole,
            runtimeRole,
            publicProbeHelper,
            publicProbeRuntime,
            columnProbeHelper,
            columnProbeRuntime
        };
        var expectedNames = new[]
        {
            $"sa_lock_helper{expectedSuffix}",
            $"sa_lock_runtime{expectedSuffix}",
            $"sa_lock_public_probe{expectedSuffix}",
            $"sa_lock_public_runtime{expectedSuffix}",
            $"sa_lock_column_probe{expectedSuffix}",
            $"sa_lock_column_runtime{expectedSuffix}"
        };

        Assert.Equal(expectedNames, roles);
        Assert.Matches("^[0-9a-f]{32}$", suffix);

        await WaitForTaskRoleSessionsToCloseAsync(connection, roles);

        await using var transaction = await connection.BeginTransactionAsync();
        await AssertTaskCleanupPreconditionsAsync(connection, transaction, roles, helperRole, runtimeRole,
            publicProbeHelper, publicProbeRuntime, columnProbeHelper, columnProbeRuntime, expectedSchemaOid);

        if (await FunctionExistsAsync(connection, transaction))
        {
            await ExecuteAsync(connection, transaction,
                "DROP FUNCTION swiftaid_internal.lock_case_authority(uuid, text, text[], boolean)");
        }

        if (await TaskRoleExistsAsync(connection, transaction, helperRole))
        {
            var quotedHelper = QuoteIdentifier(helperRole);
            await ExecuteAsync(connection, transaction, $"REVOKE SELECT (organization_id) ON public.organizations FROM {quotedHelper}");
            await ExecuteAsync(connection, transaction, $"REVOKE UPDATE (lock_token) ON public.organizations FROM {quotedHelper}");
            await ExecuteAsync(connection, transaction, $"REVOKE SELECT (workspace_id) ON public.workspaces FROM {quotedHelper}");
            await ExecuteAsync(connection, transaction, $"REVOKE UPDATE (lock_token) ON public.workspaces FROM {quotedHelper}");
            await ExecuteAsync(connection, transaction, $"REVOKE SELECT (actor_id) ON public.actor_access_states FROM {quotedHelper}");
            await ExecuteAsync(connection, transaction, $"REVOKE UPDATE (lock_token) ON public.actor_access_states FROM {quotedHelper}");
            await ExecuteAsync(connection, transaction, $"REVOKE SELECT (actor_id) ON public.actors FROM {quotedHelper}");
            await ExecuteAsync(connection, transaction, $"REVOKE UPDATE (lock_token) ON public.actors FROM {quotedHelper}");
            await ExecuteAsync(connection, transaction, $"REVOKE SELECT (organization_id, actor_id, membership_id) ON public.organization_memberships FROM {quotedHelper}");
            await ExecuteAsync(connection, transaction, $"REVOKE UPDATE (lock_token) ON public.organization_memberships FROM {quotedHelper}");
            await ExecuteAsync(connection, transaction, $"REVOKE SELECT (organization_id, actor_id, grant_group_id) ON public.access_grant_groups FROM {quotedHelper}");
            await ExecuteAsync(connection, transaction, $"REVOKE UPDATE (lock_token) ON public.access_grant_groups FROM {quotedHelper}");
            await ExecuteAsync(connection, transaction, $"REVOKE SELECT (grant_group_id, region_id) ON public.access_grant_group_regions FROM {quotedHelper}");
            await ExecuteAsync(connection, transaction, $"REVOKE UPDATE (lock_token) ON public.access_grant_group_regions FROM {quotedHelper}");
            await ExecuteAsync(connection, transaction, $"REVOKE SELECT (grant_group_id, operation) ON public.access_grant_group_operations FROM {quotedHelper}");
            await ExecuteAsync(connection, transaction, $"REVOKE UPDATE (lock_token) ON public.access_grant_group_operations FROM {quotedHelper}");
            await ExecuteAsync(connection, transaction, $"REVOKE SELECT (case_id, actor_id) ON public.case_shares FROM {quotedHelper}");
            await ExecuteAsync(connection, transaction, $"REVOKE UPDATE (lock_token) ON public.case_shares FROM {quotedHelper}");
            await ExecuteAsync(connection, transaction, $"REVOKE SELECT (case_id, organization_id, workspace_id, accountable_owner_actor_id) ON public.managed_cases FROM {quotedHelper}");
            await ExecuteAsync(connection, transaction, $"REVOKE UPDATE (lock_token) ON public.managed_cases FROM {quotedHelper}");
            await ExecuteAsync(connection, transaction, $"REVOKE SELECT (case_id, region_id) ON public.managed_case_regions FROM {quotedHelper}");
            await ExecuteAsync(connection, transaction, $"REVOKE UPDATE (lock_token) ON public.managed_case_regions FROM {quotedHelper}");
            await ExecuteAsync(connection, transaction, $"REVOKE USAGE ON SCHEMA public FROM {quotedHelper}");
            await ExecuteAsync(connection, transaction, $"REVOKE USAGE ON SCHEMA swiftaid_internal FROM {quotedHelper}");
        }

        if (await TaskRoleExistsAsync(connection, transaction, runtimeRole))
        {
            var quotedRuntime = QuoteIdentifier(runtimeRole);
            foreach (var table in new[]
            {
                "organizations", "workspaces", "actor_access_states", "actors", "organization_memberships",
                "access_grant_groups", "access_grant_group_regions", "access_grant_group_operations",
                "case_shares", "managed_cases", "managed_case_regions"
            })
            {
                await ExecuteAsync(connection, transaction, $"REVOKE SELECT ON public.{QuoteIdentifier(table)} FROM {quotedRuntime}");
            }

            await ExecuteAsync(connection, transaction, $"REVOKE USAGE ON SCHEMA public FROM {quotedRuntime}");
            await ExecuteAsync(connection, transaction, $"REVOKE USAGE ON SCHEMA swiftaid_internal FROM {quotedRuntime}");
        }

        foreach (var role in roles)
        {
            await ExecuteAsync(connection, transaction, $"DROP ROLE IF EXISTS {QuoteIdentifier(role)}");
        }

        await transaction.CommitAsync();
    }

    private static async Task WaitForTaskRoleSessionsToCloseAsync(
        NpgsqlConnection connection,
        string[] roles)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var activeSessionCount = await GetTaskRoleSessionCountAsync(connection, transaction: null, roles: roles);
        while (activeSessionCount > 0 && stopwatch.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            activeSessionCount = await GetTaskRoleSessionCountAsync(connection, transaction: null, roles: roles);
        }

        if (activeSessionCount > 0)
        {
            throw new InvalidOperationException(
                $"Task-role cleanup refused; guard=active-task-role-sessions; active_session_count={activeSessionCount}.");
        }
    }

    private static async Task<int> GetTaskRoleSessionCountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string[] roles)
    {
        await using var command = new NpgsqlCommand(
            "SELECT count(*)::integer FROM pg_catalog.pg_stat_activity WHERE usename = ANY (@roles)",
            connection,
            transaction);
        command.Parameters.AddWithValue("roles", roles);
        return (int)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("Task-role session count query returned no result."));
    }

    private static async Task AssertTaskCleanupPreconditionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string[] roles,
        string helperRole,
        string runtimeRole,
        string publicProbeHelper,
        string publicProbeRuntime,
        string columnProbeHelper,
        string columnProbeRuntime,
        uint expectedSchemaOid)
    {
        await using var command = new NpgsqlCommand(
            """
            WITH expected(role_name, can_login) AS (
                VALUES
                    (@helper_role, false),
                    (@runtime_role, true),
                    (@public_probe_helper, false),
                    (@public_probe_runtime, true),
                    (@column_probe_helper, false),
                    (@column_probe_runtime, true)
            ),
            existing AS (
                SELECT role.*
                  FROM pg_catalog.pg_roles AS role
                 WHERE role.rolname = ANY (@roles)
            ),
            helper_function AS (
                SELECT procedure.oid, procedure.proowner, procedure.prosecdef, procedure.proconfig, procedure.proacl
                  FROM pg_catalog.pg_proc AS procedure
                  JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = procedure.pronamespace
                 WHERE namespace.nspname = 'swiftaid_internal'
                   AND procedure.proname = 'lock_case_authority'
                   AND procedure.proargtypes = '2950 25 1009 16'::oidvector
            )
            SELECT current_database() = 'swiftaid_p21a'
               AND current_user = 'p21_migration_owner'
               AND session_user = 'p21_migration_owner'
               AND current_user = session_user
               AND EXISTS (
                    SELECT 1 FROM pg_catalog.pg_roles AS operator
                     WHERE operator.rolname = current_user AND operator.rolsuper
               )
               AND EXISTS (
                    SELECT 1
                      FROM pg_catalog.pg_namespace AS internal_schema
                      JOIN pg_catalog.pg_roles AS schema_owner ON schema_owner.oid = internal_schema.nspowner
                     WHERE internal_schema.nspname = 'swiftaid_internal'
                       AND internal_schema.oid = @expected_schema_oid
                       AND schema_owner.rolname = current_user
               )
               AND NOT EXISTS (
                    SELECT 1
                      FROM existing AS role
                      LEFT JOIN expected ON expected.role_name = role.rolname
                     WHERE expected.role_name IS NULL
                        OR role.rolcanlogin <> expected.can_login
                        OR role.rolsuper OR role.rolcreatedb OR role.rolcreaterole
                        OR role.rolreplication OR role.rolinherit OR role.rolbypassrls
               )
               AND NOT EXISTS (
                    SELECT 1
                      FROM pg_catalog.pg_auth_members AS membership
                     WHERE membership.roleid IN (SELECT oid FROM existing)
                        OR membership.member IN (SELECT oid FROM existing)
               )
               AND NOT EXISTS (
                    SELECT 1 FROM pg_catalog.pg_stat_activity AS activity
                     WHERE activity.usename = ANY (@roles)
               )
               AND (
                    NOT EXISTS (SELECT 1 FROM helper_function)
                    OR EXISTS (
                        SELECT 1 FROM helper_function AS procedure
                        JOIN pg_catalog.pg_roles AS owner_role ON owner_role.oid = procedure.proowner
                        JOIN pg_catalog.pg_roles AS helper ON helper.rolname = @helper_role
                        WHERE owner_role.oid = helper.oid
                          AND procedure.prosecdef
                          AND procedure.proconfig = ARRAY['search_path=pg_catalog']
                          AND (SELECT count(*) = 2 AND bool_and(privilege.privilege_type = 'EXECUTE' AND NOT privilege.is_grantable)
                                 FROM pg_catalog.aclexplode(procedure.proacl) AS privilege)
                          AND (SELECT count(*) = 2
                                 FROM pg_catalog.aclexplode(procedure.proacl) AS privilege
                                 JOIN pg_catalog.pg_roles AS grantee ON grantee.oid = privilege.grantee
                                WHERE grantee.rolname IN (@helper_role, @runtime_role))
                          AND NOT EXISTS (
                                SELECT 1 FROM pg_catalog.aclexplode(procedure.proacl) AS privilege
                                LEFT JOIN pg_catalog.pg_roles AS grantee ON grantee.oid = privilege.grantee
                                WHERE grantee.rolname IS NULL OR grantee.rolname NOT IN (@helper_role, @runtime_role)
                          )
                    )
               )
               AND (
                    EXISTS (SELECT 1 FROM helper_function)
                    AND (SELECT count(*) = 35 FROM pg_catalog.pg_shdepend AS dependency
                          JOIN pg_catalog.pg_roles AS role ON role.oid = dependency.refobjid
                         WHERE role.rolname = @helper_role
                           AND dependency.dbid = (SELECT oid FROM pg_catalog.pg_database WHERE datname = current_database())
                           AND dependency.refclassid = 'pg_catalog.pg_authid'::regclass
                           AND dependency.deptype = 'a')
                    AND (SELECT count(*) = 1 FROM pg_catalog.pg_shdepend AS dependency
                          JOIN pg_catalog.pg_roles AS role ON role.oid = dependency.refobjid
                         WHERE role.rolname = @helper_role
                           AND dependency.dbid = (SELECT oid FROM pg_catalog.pg_database WHERE datname = current_database())
                           AND dependency.refclassid = 'pg_catalog.pg_authid'::regclass
                           AND dependency.deptype = 'o'
                           AND dependency.classid = 'pg_catalog.pg_proc'::regclass
                           AND dependency.objid = (SELECT oid FROM helper_function))
                    AND (SELECT count(*) = 14 FROM pg_catalog.pg_shdepend AS dependency
                          JOIN pg_catalog.pg_roles AS role ON role.oid = dependency.refobjid
                         WHERE role.rolname = @runtime_role
                           AND dependency.dbid = (SELECT oid FROM pg_catalog.pg_database WHERE datname = current_database())
                           AND dependency.refclassid = 'pg_catalog.pg_authid'::regclass
                           AND dependency.deptype = 'a')
                    AND NOT EXISTS (
                         SELECT 1 FROM pg_catalog.pg_shdepend AS dependency
                          JOIN pg_catalog.pg_roles AS role ON role.oid = dependency.refobjid
                         WHERE role.rolname IN (@public_probe_helper, @public_probe_runtime, @column_probe_helper, @column_probe_runtime)
                           AND dependency.dbid = (SELECT oid FROM pg_catalog.pg_database WHERE datname = current_database())
                           AND dependency.refclassid = 'pg_catalog.pg_authid'::regclass
                    )
                    AND (SELECT count(*) = 1
                           FROM pg_catalog.pg_shdepend AS dependency
                           JOIN pg_catalog.pg_roles AS role ON role.oid = dependency.refobjid
                          WHERE role.rolname = @helper_role
                            AND dependency.dbid = (SELECT oid FROM pg_catalog.pg_database WHERE datname = current_database())
                            AND dependency.refclassid = 'pg_catalog.pg_authid'::regclass
                            AND dependency.deptype = 'o')
                    AND NOT EXISTS (
                         SELECT 1 FROM pg_catalog.pg_shdepend AS dependency
                          JOIN pg_catalog.pg_roles AS role ON role.oid = dependency.refobjid
                         WHERE role.rolname = ANY (@roles)
                           AND dependency.refclassid = 'pg_catalog.pg_authid'::regclass
                           AND dependency.deptype = 'o'
                           AND NOT (role.rolname = @helper_role
                                    AND dependency.dbid = (SELECT oid FROM pg_catalog.pg_database WHERE datname = current_database())
                                    AND dependency.classid = 'pg_catalog.pg_proc'::regclass
                                    AND dependency.objid = (SELECT oid FROM helper_function))
                    )
                    OR NOT EXISTS (SELECT 1 FROM helper_function)
                    AND NOT EXISTS (
                         SELECT 1 FROM pg_catalog.pg_shdepend AS dependency
                          JOIN pg_catalog.pg_roles AS role ON role.oid = dependency.refobjid
                         WHERE role.rolname = ANY (@roles)
                           AND dependency.dbid = (SELECT oid FROM pg_catalog.pg_database WHERE datname = current_database())
                           AND dependency.refclassid = 'pg_catalog.pg_authid'::regclass
                    )
                    AND NOT EXISTS (
                         SELECT 1 FROM pg_catalog.pg_shdepend AS dependency
                          JOIN pg_catalog.pg_roles AS role ON role.oid = dependency.refobjid
                         WHERE role.rolname = ANY (@roles)
                           AND dependency.dbid = (SELECT oid FROM pg_catalog.pg_database WHERE datname = current_database())
                           AND dependency.refclassid = 'pg_catalog.pg_authid'::regclass
                           AND dependency.deptype = 'o'
                    )
               )
               AND NOT EXISTS (
                    SELECT 1 FROM pg_catalog.pg_class AS relation
                     WHERE relation.relowner IN (SELECT oid FROM existing)
               )
               AND NOT EXISTS (
                    SELECT 1 FROM pg_catalog.pg_proc AS procedure
                     WHERE procedure.proowner IN (SELECT oid FROM existing)
                       AND procedure.oid <> COALESCE((SELECT oid FROM helper_function), 0)
               )
               AND NOT EXISTS (
                    SELECT 1 FROM pg_catalog.pg_namespace AS namespace
                     WHERE namespace.nspowner IN (SELECT oid FROM existing)
               )
               AND NOT EXISTS (
                    SELECT 1 FROM pg_catalog.pg_database AS database
                     WHERE database.datdba IN (SELECT oid FROM existing)
               )
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("roles", roles);
        command.Parameters.AddWithValue("helper_role", helperRole);
        command.Parameters.AddWithValue("runtime_role", runtimeRole);
        command.Parameters.AddWithValue("public_probe_helper", publicProbeHelper);
        command.Parameters.AddWithValue("public_probe_runtime", publicProbeRuntime);
        command.Parameters.AddWithValue("column_probe_helper", columnProbeHelper);
        command.Parameters.AddWithValue("column_probe_runtime", columnProbeRuntime);
        command.Parameters.Add("expected_schema_oid", NpgsqlDbType.Oid).Value = expectedSchemaOid;

        var safeToClean = (bool)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("Task-role cleanup precondition query returned no result."));
        var activeSessionCount = await GetTaskRoleSessionCountAsync(connection, transaction, roles);
        Assert.True(
            safeToClean,
            $"Task-role cleanup refused; guard=fixture-preconditions; active_task_role_session_count={activeSessionCount}.");
    }

    private static async Task<bool> TaskRoleExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string role)
    {
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = @role)",
            connection,
            transaction);
        command.Parameters.AddWithValue("role", role);
        return (bool)(await command.ExecuteScalarAsync() ?? false);
    }

    private static async Task AssertHelperFunctionAndGrantsRemovedAsync(
        NpgsqlConnection connection,
        uint expectedSchemaOid)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT pg_catalog.to_regprocedure('swiftaid_internal.lock_case_authority(uuid,text,text[],boolean)') IS NULL
               AND EXISTS (
                   SELECT 1
                     FROM pg_catalog.pg_namespace AS namespace
                    WHERE namespace.nspname = 'swiftaid_internal'
                      AND namespace.oid = @expected_schema_oid
               )
               AND NOT EXISTS (
                   SELECT 1
                     FROM pg_catalog.pg_namespace AS namespace
                     CROSS JOIN LATERAL pg_catalog.aclexplode(namespace.nspacl) AS privilege
                    WHERE namespace.nspname = 'swiftaid_internal'
                      AND privilege.grantee <> namespace.nspowner
               )
            """,
            connection);
        command.Parameters.Add("expected_schema_oid", NpgsqlDbType.Oid).Value = expectedSchemaOid;
        Assert.True((bool)(await command.ExecuteScalarAsync() ?? false), "Task-owned helper object and schema grants must be removed after the ACL proof.");
    }

    private static async Task AssertProvisioningPreflightRollsBackAsync(
        NpgsqlConnectionStringBuilder ownerBuilder,
        string helperRole,
        string runtimeRole,
        string prefixSql,
        NpgsqlConnection verificationConnection,
        string verificationSql,
        string expectedDiagnostic)
    {
        var result = await RunPsqlAsync(
            ownerBuilder,
            helperRole,
            runtimeRole,
            scriptPath: null,
            prefixSql: prefixSql);
        Assert.True(result.ExitCode == 3, FormatPsqlFailure(result));
        Assert.Contains(expectedDiagnostic, result.StandardOutput + result.StandardError, StringComparison.Ordinal);

        await using var verify = new NpgsqlCommand(verificationSql, verificationConnection);
        if (verificationSql.Contains("@role", StringComparison.Ordinal))
        {
            verify.Parameters.AddWithValue("role", helperRole);
        }

        Assert.False((bool)(await verify.ExecuteScalarAsync() ?? false));
    }

    private static async Task<PsqlResult> RunPsqlAsync(
        NpgsqlConnectionStringBuilder connection,
        string helperRole,
        string runtimeRole,
        string? scriptPath,
        string? prefixSql)
    {
        string? temporaryScriptPath = null;
        try
        {
            var effectiveScriptPath = scriptPath;
            if (prefixSql is not null)
            {
                temporaryScriptPath = Path.Combine(
                    Path.GetTempPath(),
                    $"swiftaid-lock-helper-preflight-{Guid.NewGuid():N}.sql");
                var helperScript = File.ReadAllText(FindRepositoryFile("infra/postgres/case-authority-lock-helper.sql"));
                using (File.Create(temporaryScriptPath))
                {
                }

                RestrictTemporaryScriptAccess(temporaryScriptPath);
                await File.WriteAllTextAsync(
                    temporaryScriptPath,
                    prefixSql + helperScript,
                    new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                effectiveScriptPath = temporaryScriptPath;
            }

            var host = connection.Host
                ?? throw new InvalidOperationException("The disposable PostgreSQL host is required for the psql fixture.");
            var username = connection.Username
                ?? throw new InvalidOperationException("The disposable PostgreSQL user is required for the psql fixture.");
            var database = connection.Database
                ?? throw new InvalidOperationException("The disposable PostgreSQL database is required for the psql fixture.");
            var password = connection.Password
                ?? throw new InvalidOperationException("The disposable PostgreSQL credential is required for the psql fixture.");

            var startInfo = new ProcessStartInfo
            {
                FileName = "psql",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-X");
            startInfo.ArgumentList.Add("-w");
            startInfo.ArgumentList.Add("-v");
            startInfo.ArgumentList.Add("ON_ERROR_STOP=1");
            startInfo.ArgumentList.Add("-v");
            startInfo.ArgumentList.Add($"helper_owner={helperRole}");
            startInfo.ArgumentList.Add("-v");
            startInfo.ArgumentList.Add($"runtime_role={runtimeRole}");
            startInfo.ArgumentList.Add("-h");
            startInfo.ArgumentList.Add(host);
            startInfo.ArgumentList.Add("-p");
            startInfo.ArgumentList.Add(connection.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-U");
            startInfo.ArgumentList.Add(username);
            startInfo.ArgumentList.Add("-d");
            startInfo.ArgumentList.Add(database);
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add(effectiveScriptPath
                ?? throw new InvalidOperationException("The psql helper script path is required for provisioning."));
            startInfo.Environment["PGPASSWORD"] = password;
            startInfo.Environment["PGCONNECT_TIMEOUT"] = "5";
            startInfo.Environment["PGSSLMODE"] = connection.SslMode.ToString().ToLowerInvariant();

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                throw new InvalidOperationException("Could not start the local psql client for the isolated database fixture.");
            }

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return new PsqlResult(process.ExitCode, await stdout, await stderr);
        }
        finally
        {
            if (temporaryScriptPath is not null && File.Exists(temporaryScriptPath))
            {
                File.Delete(temporaryScriptPath);
            }
        }
    }

    private static void RestrictTemporaryScriptAccess(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var userSid = identity.User
                ?? throw new InvalidOperationException("The current user SID is required to secure the temporary SQL script.");
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(userSid, FileSystemRights.FullControl, AccessControlType.Allow));
            FileSystemAclExtensions.SetAccessControl(new FileInfo(path), security);
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static string FormatPsqlFailure(PsqlResult result) =>
        $"psql exited with code {result.ExitCode}. stdout: {result.StandardOutput}; stderr: {result.StandardError}";

    private sealed record PsqlResult(int ExitCode, string StandardOutput, string StandardError);

    private static async Task AssertRuntimeAuthorizationAsync(
        NpgsqlConnection runtimeConnection,
        CaseAuthorityPostgresFixture fixture)
    {
        await using (var read = new NpgsqlCommand(
            "SELECT actor_id FROM public.actors WHERE actor_id = @actor",
            runtimeConnection))
        {
            read.Parameters.AddWithValue("actor", fixture.ActorId);
            Assert.Equal(fixture.ActorId, await read.ExecuteScalarAsync());
        }

        await AssertPermissionDeniedAsync(
            runtimeConnection,
            "UPDATE public.actors SET status = 'Suspended' WHERE actor_id = @actor",
            fixture.ActorId);
        await AssertPermissionDeniedAsync(
            runtimeConnection,
            "UPDATE public.managed_cases SET aggregate_version = aggregate_version + 1 WHERE case_id = '00000000-0000-0000-0000-000000000000'");
        await AssertPermissionDeniedAsync(runtimeConnection, "CREATE TABLE swiftaid_internal.denied_table (id integer)");
        await AssertPermissionDeniedAsync(
            runtimeConnection,
            "UPDATE public.\"__EFMigrationsHistory\" SET \"MigrationId\" = \"MigrationId\" WHERE false");
        await AssertPermissionDeniedAsync(
            runtimeConnection,
            "DELETE FROM public.\"__EFMigrationsHistory\" WHERE false");

        var helperOwner = await GetFunctionOwnerAsync(runtimeConnection);
        await AssertPermissionDeniedAsync(runtimeConnection, $"SET ROLE {QuoteIdentifier(helperOwner)}");

        await AssertHelperCallAndLockLifetimeAsync(runtimeConnection, fixture);
    }

    private static async Task AssertPermissionDeniedAsync(
        NpgsqlConnection connection,
        string sql,
        string? actorId = null)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        if (actorId is not null)
        {
            command.Parameters.AddWithValue("actor", actorId);
        }

        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal("42501", exception.SqlState);
    }

    private static async Task<string> GetFunctionOwnerAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_catalog.pg_get_userbyid(procedure.proowner) FROM pg_catalog.pg_proc AS procedure JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = procedure.pronamespace WHERE namespace.nspname = 'swiftaid_internal' AND procedure.proname = 'lock_case_authority'",
            connection);
        return (string)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("The provisioned lock helper function was not found."));
    }

    private static async Task AssertHelperCallAndLockLifetimeAsync(
        NpgsqlConnection runtimeConnection,
        CaseAuthorityPostgresFixture fixture)
    {
        var actorHints = new[] { fixture.OwnerActorId, fixture.ActorId };
        await using var runtimeTransaction = await runtimeConnection.BeginTransactionAsync();
        await using (var call = new NpgsqlCommand(
            "SELECT swiftaid_internal.lock_case_authority(@case, @actor, @hints, false)",
            runtimeConnection,
            runtimeTransaction))
        {
            call.Parameters.AddWithValue("case", fixture.CaseId);
            call.Parameters.AddWithValue("actor", fixture.ActorId);
            call.Parameters.AddWithValue("hints", actorHints);
            Assert.Equal((short)0, (short)(await call.ExecuteScalarAsync() ?? (short)-1));
        }

        await using (var ownerConnection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await ownerConnection.OpenAsync();
            await using var conflict = await ownerConnection.BeginTransactionAsync();
            await using var lockedRead = new NpgsqlCommand(
                "SELECT actor_id FROM public.actor_access_states WHERE actor_id = @actor FOR UPDATE NOWAIT",
                ownerConnection,
                conflict);
            lockedRead.Parameters.AddWithValue("actor", fixture.ActorId);
            var lockException = await Assert.ThrowsAsync<PostgresException>(() => lockedRead.ExecuteScalarAsync());
            Assert.Equal("55P03", lockException.SqlState);
            await conflict.RollbackAsync();
        }

        await runtimeTransaction.CommitAsync();

        await using (var writeTransaction = await runtimeConnection.BeginTransactionAsync())
        {
            await using (var writeCall = new NpgsqlCommand(
                "SELECT swiftaid_internal.lock_case_authority(@case, @actor, @hints, true)",
                runtimeConnection,
                writeTransaction))
            {
                writeCall.Parameters.AddWithValue("case", fixture.CaseId);
                writeCall.Parameters.AddWithValue("actor", fixture.ActorId);
                writeCall.Parameters.AddWithValue("hints", actorHints);
                Assert.Equal((short)0, (short)(await writeCall.ExecuteScalarAsync() ?? (short)-1));
            }

            await using var ownerConnection = new NpgsqlConnection(fixture.ConnectionString);
            await ownerConnection.OpenAsync();
            await using var conflict = await ownerConnection.BeginTransactionAsync();
            await using var lockedState = new NpgsqlCommand(
                "SELECT actor_id FROM public.actor_access_states WHERE actor_id = @actor FOR SHARE NOWAIT",
                ownerConnection,
                conflict);
            lockedState.Parameters.AddWithValue("actor", fixture.ActorId);
            var stateException = await Assert.ThrowsAsync<PostgresException>(() => lockedState.ExecuteScalarAsync());
            Assert.Equal("55P03", stateException.SqlState);
            await conflict.RollbackAsync();

            await using var lockedCase = new NpgsqlCommand(
                "SELECT case_id FROM public.managed_cases WHERE case_id = @case FOR SHARE NOWAIT",
                ownerConnection);
            lockedCase.Parameters.AddWithValue("case", fixture.CaseId);
            var caseException = await Assert.ThrowsAsync<PostgresException>(() => lockedCase.ExecuteScalarAsync());
            Assert.Equal("55P03", caseException.SqlState);

            await writeTransaction.CommitAsync();
        }

        await using (var ownerConnection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await ownerConnection.OpenAsync();
            await using var afterWriteCommit = new NpgsqlCommand(
                "SELECT case_id FROM public.managed_cases WHERE case_id = @case FOR SHARE NOWAIT",
                ownerConnection);
            afterWriteCommit.Parameters.AddWithValue("case", fixture.CaseId);
            Assert.Equal(fixture.CaseId, await afterWriteCommit.ExecuteScalarAsync());
        }

        await using (var ownerConnection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await ownerConnection.OpenAsync();
            await using var afterCommit = new NpgsqlCommand(
                "SELECT actor_id FROM public.actor_access_states WHERE actor_id = @actor FOR UPDATE NOWAIT",
                ownerConnection);
            afterCommit.Parameters.AddWithValue("actor", fixture.ActorId);
            Assert.Equal(fixture.ActorId, await afterCommit.ExecuteScalarAsync());
        }

        await using var discovery = new NpgsqlCommand(
            "SELECT swiftaid_internal.lock_case_authority(@case, @actor, @hints, false)",
            runtimeConnection);
        discovery.Parameters.AddWithValue("case", fixture.CaseId);
        discovery.Parameters.AddWithValue("actor", fixture.NewOwnerActorId);
        discovery.Parameters.AddWithValue("hints", new[] { fixture.NewOwnerActorId });
        Assert.Equal((short)1, (short)(await discovery.ExecuteScalarAsync() ?? (short)-1));
    }

    private static string FindRepositoryFile(string relativePath)
    {
        foreach (var candidate in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = new DirectoryInfo(candidate);
            while (directory is not null)
            {
                var file = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(file))
                {
                    return file;
                }

                directory = directory.Parent;
            }
        }

        throw new FileNotFoundException("The repository-root helper provisioning script was not found.");
    }

    private static string QuoteIdentifier(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static async Task<string> SeedGrantAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string prefix)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var actor = $"{prefix}-actor-{suffix}";
        var organization = $"{prefix}-org-{suffix}";
        var workspace = $"{prefix}-workspace-{suffix}";
        var grant = $"{prefix}-grant-{suffix}";
        var membership = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await ExecuteAsync(connection, transaction,
            "INSERT INTO public.actors (actor_id, status, created_at_utc, updated_at_utc) VALUES (@actor, 'Active', @now, @now)",
            ("actor", actor), ("now", now));
        await ExecuteAsync(connection, transaction,
            "INSERT INTO public.organizations (organization_id, status, created_at_utc, updated_at_utc) VALUES (@organization, 'Active', @now, @now)",
            ("organization", organization), ("now", now));
        await ExecuteAsync(connection, transaction,
            "INSERT INTO public.workspaces (workspace_id, organization_id, status) VALUES (@workspace, @organization, 'Active')",
            ("workspace", workspace), ("organization", organization));
        await ExecuteAsync(connection, transaction,
            "INSERT INTO public.organization_memberships (membership_id, actor_id, organization_id, status, valid_from_utc, granted_by_actor_id, version) VALUES (@membership, @actor, @organization, 'Active', @now, @actor, 1)",
            ("membership", membership), ("actor", actor), ("organization", organization), ("now", now));
        await ExecuteAsync(connection, transaction,
            """
            INSERT INTO public.access_grant_groups
                (grant_group_id, membership_id, actor_id, organization_id, workspace_id, country_code, sector,
                 sensitivity_code, can_access_jurisdictional_cases, status, valid_from_utc, expires_at_utc,
                 granted_by_actor_id, version)
            VALUES (@grant, @membership, @actor, @organization, @workspace, 'SZ', 'HumanHealth', 'Standard',
                    false, 'Active', @now, @expiry, @actor, 1)
            """,
            ("grant", grant), ("membership", membership), ("actor", actor),
            ("organization", organization), ("workspace", workspace),
            ("now", now), ("expiry", now.AddHours(1)));

        return grant;
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string commandText,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(commandText, connection, transaction);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static string ExtractDownGuard(string migrationScript)
    {
        const string start = "DO $p21b_down_guard$";
        const string end = "$p21b_down_guard$;";
        var startIndex = migrationScript.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, "The generated down script must begin its safety guard before schema mutation.");
        var endIndex = migrationScript.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.True(endIndex >= 0, "The generated down script must close its safety guard.");
        return migrationScript[startIndex..(endIndex + end.Length)];
    }

    private static string NormalizeSqlExpression(string expression) =>
        expression.Replace("(", string.Empty, StringComparison.Ordinal)
            .Replace(")", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("\"", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
}
