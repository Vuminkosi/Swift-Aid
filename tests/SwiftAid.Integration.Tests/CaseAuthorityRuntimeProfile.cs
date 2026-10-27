using System.Diagnostics;
using System.Data;
using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NpgsqlTypes;

namespace SwiftAid.Integration.Tests;

/// <summary>Per-test, restricted PostgreSQL identities provisioned by the repository SQL scripts.</summary>
internal sealed class CaseAuthorityRuntimeProfile : IAsyncDisposable
{
    private const string HelperFunctionIdentityArguments =
        "p_case_id uuid, p_actor_id text, p_actor_hints text[], p_write boolean";
    private const string PublicSchemaOwner = "pg_database_owner";

    private static readonly string[] HelperScriptRuntimeSelectRelations =
    [
        "organizations", "workspaces", "actor_access_states", "actors", "organization_memberships",
        "access_grant_groups", "access_grant_group_regions", "access_grant_group_operations", "case_shares",
        "managed_cases", "managed_case_regions"
    ];

    private static readonly string[] RuntimeScriptSelectRelations =
    ["protected_event_payloads", "case_events", "command_receipts"];

    private static readonly IReadOnlyDictionary<string, string[]> HelperColumns = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["organizations"] = ["organization_id", "lock_token"],
        ["workspaces"] = ["workspace_id", "lock_token"],
        ["actor_access_states"] = ["actor_id", "lock_token"],
        ["actors"] = ["actor_id", "lock_token"],
        ["organization_memberships"] = ["organization_id", "actor_id", "membership_id", "lock_token"],
        ["access_grant_groups"] = ["organization_id", "actor_id", "grant_group_id", "lock_token"],
        ["access_grant_group_regions"] = ["grant_group_id", "region_id", "lock_token"],
        ["access_grant_group_operations"] = ["grant_group_id", "operation", "lock_token"],
        ["case_shares"] = ["case_id", "actor_id", "lock_token"],
        ["managed_cases"] = ["case_id", "organization_id", "workspace_id", "accountable_owner_actor_id", "lock_token"],
        ["managed_case_regions"] = ["case_id", "region_id", "lock_token"]
    };

    private static readonly IReadOnlyDictionary<string, string[]> RuntimeColumns = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["actor_access_states"] = ["access_epoch", "version", "updated_at_utc"],
        ["managed_cases"] = ["aggregate_version", "report_revision", "resource_version", "workflow", "workflow_version", "accountable_owner_actor_id", "updated_at_utc"],
        ["protected_event_payloads"] = ["payload_id", "protected_bytes", "protection_purpose_version", "created_at_utc"],
        ["case_events"] = ["event_id", "case_id", "aggregate_version", "report_revision", "command_id", "actor_id", "event_type", "schema_version", "occurred_at_utc", "recorded_at_utc", "payload_id"],
        ["command_receipts"] = ["receipt_id", "actor_id", "command_id", "case_id", "command_type", "schema_version", "canonical_payload_hash", "first_aggregate_version", "last_aggregate_version", "report_revision", "accepted_at_utc"],
        ["domain_outbox"] = ["outbox_id", "event_id", "message_type", "created_at_utc", "available_at_utc", "attempt_count", "lease_until_utc", "published_at_utc"],
        ["security_audit_entries"] = ["audit_id", "actor_id", "target_actor_id", "resource_id", "operation", "decision_code", "access_epoch", "recorded_at_utc", "reason_code"]
    };

    private readonly CaseAuthorityPostgresFixture _fixture;
    private readonly NpgsqlConnectionStringBuilder _owner;
    private readonly string _runtimePassword;
    private bool _helperRoleCreated;
    private bool _runtimeRoleCreated;
    private bool _provisioned;
    private bool _disposed;
    private RuntimeGrantProfile? _expectedGrantProfile;

    private CaseAuthorityRuntimeProfile(CaseAuthorityPostgresFixture fixture, NpgsqlConnectionStringBuilder owner)
    {
        _fixture = fixture;
        _owner = owner;
        var suffix = Guid.NewGuid().ToString("N");
        HelperRole = $"sae2_helper_{suffix}";
        RuntimeRole = $"sae2_runtime_{suffix}";
        RuntimeApplicationName = $"swiftaid-e2-{suffix}";
        _runtimePassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var builder = new NpgsqlConnectionStringBuilder(owner.ConnectionString)
        {
            Username = RuntimeRole,
            Password = _runtimePassword,
            Pooling = false,
            Timeout = 5,
            CommandTimeout = 10,
            ApplicationName = RuntimeApplicationName
        };
        RuntimeConnectionString = builder.ConnectionString;
    }

    public string HelperRole { get; }

    public string RuntimeRole { get; }

    public string RuntimeApplicationName { get; }

    private string RuntimeConnectionString { get; }

    public static async Task<CaseAuthorityRuntimeProfile> CreateAsync(
        CaseAuthorityPostgresFixture fixture,
        Func<CaseAuthorityRuntimeProfile, Task>? afterHelperScript = null)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        if (!string.Equals(Environment.GetEnvironmentVariable("SWIFTAID_RUN_SQLAUTH_FIXTURES"), "1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The restricted runtime profile requires the SQLAUTH fixture opt-in.");
        }

        var owner = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Pooling = false,
            Timeout = 5,
            CommandTimeout = 10
        };
        var profile = new CaseAuthorityRuntimeProfile(fixture, owner);
        try
        {
            await profile.ProvisionAsync(afterHelperScript).ConfigureAwait(false);
            return profile;
        }
        catch (Exception setupException)
        {
            try
            {
                await profile.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                setupException.Data["RuntimeProfileCleanupFailure"] = cleanupException;
            }

            ExceptionDispatchInfo.Capture(setupException).Throw();
            throw;
        }
    }

    internal SwiftAid.Infrastructure.Persistence.SwiftAidDbContext CreateRuntimeContext(
        CaseAuthorityPostgresFixture fixture,
        DbCommandInterceptor? interceptor = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_provisioned || !ReferenceEquals(fixture, _fixture))
        {
            throw new InvalidOperationException("A runtime context requires this fixture's successfully provisioned profile.");
        }

        var optionsBuilder = new DbContextOptionsBuilder<SwiftAid.Infrastructure.Persistence.SwiftAidDbContext>()
            .UseNpgsql(RuntimeConnectionString);
        if (interceptor is not null)
        {
            optionsBuilder.AddInterceptors(interceptor);
        }

        return new SwiftAid.Infrastructure.Persistence.SwiftAidDbContext(optionsBuilder.Options);
    }

    internal NpgsqlConnection CreateRuntimeConnection()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_provisioned)
        {
            throw new InvalidOperationException("A direct runtime connection requires a successfully provisioned profile.");
        }

        return new NpgsqlConnection(RuntimeConnectionString);
    }

    internal async Task DropHelperForMissingHelperScenarioAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var expected = _expectedGrantProfile
            ?? throw new InvalidOperationException("The runtime grant profile was not captured after successful provisioning.");
        if (!expected.FunctionPresent)
        {
            throw new InvalidOperationException("The generated helper function is already absent.");
        }

        await using (var connection = new NpgsqlConnection(_owner.ConnectionString))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable).ConfigureAwait(false);
            try
            {
                await AssertExpectedGrantProfileAsync(connection, transaction, expected).ConfigureAwait(false);
                await ExecuteAsync(connection, transaction,
                    "DROP FUNCTION swiftaid_internal.lock_case_authority(uuid, text, text[], boolean)").ConfigureAwait(false);
                await transaction.CommitAsync().ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync().ConfigureAwait(false);
                throw;
            }
        }

        var functionObject = expected.Grants.First(grant => grant.ObjectKind == "function");
        var expectedWithoutFunction = expected with
        {
            Grants = expected.Grants
                .Where(grant => grant.ObjectKind != "function")
                .ToImmutableHashSet(),
            Dependencies = expected.Dependencies
                .Where(dependency => dependency.ClassId != functionObject.ClassId ||
                                     dependency.ObjectId != functionObject.ObjectId)
                .ToImmutableHashSet(),
            OwnedObjects = ImmutableHashSet<DependencyTuple>.Empty,
            FunctionPresent = false
        };
        await using (var connection = new NpgsqlConnection(_owner.ConnectionString))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            await AssertExpectedGrantProfileAsync(connection, transaction: null, expectedWithoutFunction).ConfigureAwait(false);
        }

        _expectedGrantProfile = expectedWithoutFunction;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (!_helperRoleCreated && !_runtimeRoleCreated)
        {
            _disposed = true;
            return;
        }

        await using var connection = new NpgsqlConnection(_owner.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable).ConfigureAwait(false);
        try
        {
            var expected = _expectedGrantProfile
                ?? throw new InvalidOperationException("Refusing runtime-profile cleanup without a validated post-provisioning ACL snapshot.");
            if (expected.HelperRolePresent != _helperRoleCreated || expected.RuntimeRolePresent != _runtimeRoleCreated)
            {
                throw new InvalidOperationException("Refusing runtime-profile cleanup because the checkpoint does not match this setup attempt's roles.");
            }

            await AssertExpectedGrantProfileAsync(connection, transaction, expected).ConfigureAwait(false);
            await AssertCleanupPreconditionsAsync(connection, transaction).ConfigureAwait(false);

            if (expected.FunctionPresent)
            {
                await ExecuteAsync(connection, transaction,
                    "DROP FUNCTION swiftaid_internal.lock_case_authority(uuid, text, text[], boolean)").ConfigureAwait(false);
            }

            foreach (var grant in expected.Grants.Where(grant => grant.ObjectKind != "function"))
            {
                await RevokeExpectedGrantAsync(connection, transaction, grant).ConfigureAwait(false);
            }

            if (_runtimeRoleCreated)
            {
                await ExecuteAsync(connection, transaction, $"DROP ROLE {QuoteIdentifier(RuntimeRole)}").ConfigureAwait(false);
            }

            if (_helperRoleCreated)
            {
                await ExecuteAsync(connection, transaction, $"DROP ROLE {QuoteIdentifier(HelperRole)}").ConfigureAwait(false);
            }

            await transaction.CommitAsync().ConfigureAwait(false);
            _disposed = true;
        }
        catch
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task ProvisionAsync(Func<CaseAuthorityRuntimeProfile, Task>? afterHelperScript)
    {
        await using var ownerConnection = new NpgsqlConnection(_owner.ConnectionString);
        await ownerConnection.OpenAsync().ConfigureAwait(false);
        await AssertRoleAbsentAsync(ownerConnection, HelperRole).ConfigureAwait(false);
        await AssertRoleAbsentAsync(ownerConnection, RuntimeRole).ConfigureAwait(false);
        await CreateRoleAsync(ownerConnection, HelperRole, login: false, password: null).ConfigureAwait(false);
        _helperRoleCreated = true;
        await CaptureAndValidateExpectedGrantProfileAsync(
            helperCreated: true,
            runtimeCreated: false,
            helperScriptApplied: false,
            runtimeScriptApplied: false).ConfigureAwait(false);

        await CreateRoleAsync(ownerConnection, RuntimeRole, login: true, password: _runtimePassword).ConfigureAwait(false);
        _runtimeRoleCreated = true;
        await CaptureAndValidateExpectedGrantProfileAsync(
            helperCreated: true,
            runtimeCreated: true,
            helperScriptApplied: false,
            runtimeScriptApplied: false).ConfigureAwait(false);

        var helperScript = FindRepositoryFile("infra/postgres/case-authority-lock-helper.sql");
        AssertPsqlSucceeded(await RunPsqlAsync(helperScript, ("helper_owner", HelperRole), ("runtime_role", RuntimeRole))
            .ConfigureAwait(false), "The checked-in lock-helper script failed for this isolated profile.");
        await CaptureAndValidateExpectedGrantProfileAsync(
            helperCreated: true,
            runtimeCreated: true,
            helperScriptApplied: true,
            runtimeScriptApplied: false).ConfigureAwait(false);
        if (afterHelperScript is not null)
        {
            await afterHelperScript(this).ConfigureAwait(false);
        }

        var runtimeScript = FindRepositoryFile("infra/postgres/case-command-runtime-grants.sql");
        AssertPsqlSucceeded(await RunPsqlAsync(runtimeScript, ("runtime_role", RuntimeRole)).ConfigureAwait(false),
            "The checked-in runtime-grants script failed for this isolated profile.");
        await CaptureAndValidateExpectedGrantProfileAsync(
            helperCreated: true,
            runtimeCreated: true,
            helperScriptApplied: true,
            runtimeScriptApplied: true).ConfigureAwait(false);
        _provisioned = true;
    }

    private async Task CaptureAndValidateExpectedGrantProfileAsync(
        bool helperCreated,
        bool runtimeCreated,
        bool helperScriptApplied,
        bool runtimeScriptApplied)
    {
        if (helperCreated != _helperRoleCreated || runtimeCreated != _runtimeRoleCreated)
        {
            throw new InvalidOperationException("A runtime-profile checkpoint must reflect the exact roles created by this setup attempt.");
        }

        var expected = await BuildExpectedGrantProfileAsync(
            helperCreated, runtimeCreated, helperScriptApplied, runtimeScriptApplied).ConfigureAwait(false);
        await using var connection = new NpgsqlConnection(_owner.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await AssertExpectedGrantProfileAsync(connection, transaction: null, expected).ConfigureAwait(false);
        _expectedGrantProfile = expected;
    }

    private async Task<RuntimeGrantProfile> BuildExpectedGrantProfileAsync(
        bool helperCreated,
        bool runtimeCreated,
        bool helperScriptApplied,
        bool runtimeScriptApplied)
    {
        if (!helperCreated || (helperScriptApplied && (!runtimeCreated || !helperCreated)) ||
            (runtimeScriptApplied && !helperScriptApplied))
        {
            throw new InvalidOperationException("The requested runtime-profile checkpoint is not a valid provisioning stage.");
        }

        await using var connection = new NpgsqlConnection(_owner.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        var classIds = new Dictionary<string, uint>(StringComparer.Ordinal)
        {
            ["pg_catalog.pg_namespace"] = await ReadRegClassOidAsync(connection, null, "pg_catalog.pg_namespace").ConfigureAwait(false),
            ["pg_catalog.pg_class"] = await ReadRegClassOidAsync(connection, null, "pg_catalog.pg_class").ConfigureAwait(false),
            ["pg_catalog.pg_proc"] = await ReadRegClassOidAsync(connection, null, "pg_catalog.pg_proc").ConfigureAwait(false)
        };
        var ownerRole = _owner.Username
            ?? throw new InvalidOperationException("The fixture owner role is required.");
        var roleIds = new Dictionary<string, uint>(StringComparer.Ordinal)
        {
            [ownerRole] = await ReadRoleOidAsync(connection, null, ownerRole).ConfigureAwait(false)
        };
        if (helperScriptApplied)
        {
            roleIds[HelperRole] = await ReadRoleOidAsync(connection, null, HelperRole).ConfigureAwait(false);
            roleIds[PublicSchemaOwner] = await ReadRoleOidAsync(connection, null, PublicSchemaOwner).ConfigureAwait(false);
            if (!string.Equals(await ReadSchemaOwnerAsync(connection, null, "public").ConfigureAwait(false), PublicSchemaOwner, StringComparison.Ordinal) ||
                !string.Equals(await ReadSchemaOwnerAsync(connection, null, "swiftaid_internal").ConfigureAwait(false), ownerRole, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The runtime-profile schema owners differ from the approved fixture profile.");
            }
        }
        var profileRoles = new List<string>();
        if (helperCreated)
        {
            roleIds[HelperRole] = await ReadRoleOidAsync(connection, null, HelperRole).ConfigureAwait(false);
            profileRoles.Add(HelperRole);
        }
        if (runtimeCreated)
        {
            roleIds[RuntimeRole] = await ReadRoleOidAsync(connection, null, RuntimeRole).ConfigureAwait(false);
            profileRoles.Add(RuntimeRole);
        }

        var expected = new HashSet<GrantTuple>();
        if (helperScriptApplied)
        {
            foreach (var schema in new[] { "public", "swiftaid_internal" })
            {
                var objectId = await ReadSchemaOidAsync(connection, null, schema).ConfigureAwait(false);
                var schemaGrantor = schema == "public" ? PublicSchemaOwner : ownerRole;
                AddExpectedGrant(expected, "schema", schema, string.Empty, string.Empty,
                    classIds["pg_catalog.pg_namespace"], objectId, 0,
                    HelperRole, "USAGE", schemaGrantor, classIds, roleIds);
                AddExpectedGrant(expected, "schema", schema, string.Empty, string.Empty,
                    classIds["pg_catalog.pg_namespace"], objectId, 0,
                    RuntimeRole, "USAGE", schemaGrantor, classIds, roleIds);
            }

            foreach (var (relation, columns) in HelperColumns)
            {
                var relationOid = await ReadRelationOidAsync(connection, null, relation).ConfigureAwait(false);
                foreach (var column in columns)
                {
                    var attributeNumber = await ReadAttributeNumberAsync(connection, null, relationOid, column).ConfigureAwait(false);
                    AddExpectedGrant(expected, "column", "public", relation, column,
                        classIds["pg_catalog.pg_class"], relationOid, attributeNumber,
                        HelperRole, string.Equals(column, "lock_token", StringComparison.Ordinal) ? "UPDATE" : "SELECT",
                        ownerRole, classIds, roleIds);
                }
            }

            foreach (var relation in HelperScriptRuntimeSelectRelations)
            {
                var relationOid = await ReadRelationOidAsync(connection, null, relation).ConfigureAwait(false);
                AddExpectedGrant(expected, "relation", "public", relation, string.Empty,
                    classIds["pg_catalog.pg_class"], relationOid, 0,
                    RuntimeRole, "SELECT", ownerRole, classIds, roleIds);
            }

            var helperFunctionOid = await ReadHelperFunctionOidAsync(connection, null).ConfigureAwait(false);
            AddExpectedGrant(expected, "function", "swiftaid_internal", "lock_case_authority",
                HelperFunctionIdentityArguments,
                classIds["pg_catalog.pg_proc"], helperFunctionOid, 0,
                HelperRole, "EXECUTE", HelperRole, classIds, roleIds);
            AddExpectedGrant(expected, "function", "swiftaid_internal", "lock_case_authority",
                HelperFunctionIdentityArguments,
                classIds["pg_catalog.pg_proc"], helperFunctionOid, 0,
                RuntimeRole, "EXECUTE", HelperRole, classIds, roleIds);
        }

        if (runtimeScriptApplied)
        {
            foreach (var relation in RuntimeScriptSelectRelations)
            {
                var relationOid = await ReadRelationOidAsync(connection, null, relation).ConfigureAwait(false);
                AddExpectedGrant(expected, "relation", "public", relation, string.Empty,
                    classIds["pg_catalog.pg_class"], relationOid, 0,
                    RuntimeRole, "SELECT", ownerRole, classIds, roleIds);
            }

            foreach (var (relation, columns) in RuntimeColumns)
            {
                var relationOid = await ReadRelationOidAsync(connection, null, relation).ConfigureAwait(false);
                foreach (var column in columns)
                {
                    var attributeNumber = await ReadAttributeNumberAsync(connection, null, relationOid, column).ConfigureAwait(false);
                    var privilege = relation switch
                    {
                        "actor_access_states" or "managed_cases" => "UPDATE",
                        _ => "INSERT"
                    };
                    AddExpectedGrant(expected, "column", "public", relation, column,
                        classIds["pg_catalog.pg_class"], relationOid, attributeNumber,
                        RuntimeRole, privilege, ownerRole, classIds, roleIds);
                }
            }
        }

        var expectedDependencies = expected
            .Where(grant => !(grant.ObjectKind == "function" && grant.Grantee == HelperRole))
            .Select(grant => new DependencyTuple(
                grant.Grantee, grant.ClassId, grant.ObjectId, grant.SubObjectId, 'a'))
            .ToHashSet();
        var expectedOwnedObjects = helperScriptApplied
            ? new HashSet<DependencyTuple>
            {
                new(HelperRole, classIds["pg_catalog.pg_proc"],
                    await ReadHelperFunctionOidAsync(connection, null).ConfigureAwait(false), 0, 'o')
            }
            : new HashSet<DependencyTuple>();

        return new RuntimeGrantProfile(expected.ToImmutableHashSet(),
            expectedDependencies.ToImmutableHashSet(), expectedOwnedObjects.ToImmutableHashSet(),
            helperCreated, runtimeCreated, helperScriptApplied, runtimeScriptApplied,
            helperScriptApplied, profileRoles.ToImmutableArray(),
            profileRoles.ToImmutableDictionary(role => role, role => roleIds[role], StringComparer.Ordinal));
    }

    private static void AddExpectedGrant(
        ISet<GrantTuple> expected,
        string objectKind,
        string schemaName,
        string objectName,
        string subObjectName,
        uint classId,
        uint objectId,
        int subObjectId,
        string grantee,
        string privilege,
        string grantor,
        IReadOnlyDictionary<string, uint> classIds,
        IReadOnlyDictionary<string, uint> roleIds)
    {
        expected.Add(new GrantTuple(objectKind, schemaName, objectName, subObjectName,
            classId, objectId, subObjectId, grantee, roleIds[grantee], privilege,
            grantor, roleIds[grantor], IsGrantable: false));
    }

    private async Task AssertExpectedGrantProfileAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        RuntimeGrantProfile expected)
    {
        var actualGrants = await ReadGrantProfileAsync(connection, transaction, expected.ProfileRoles).ConfigureAwait(false);
        if (!actualGrants.SetEquals(expected.Grants))
        {
            throw new InvalidOperationException("Refusing runtime-profile changes because generated-role ACL tuples differ from the approved profile.");
        }

        var actualDependencies = await ReadDependencyProfileAsync(connection, transaction, expected.ProfileRoles).ConfigureAwait(false);
        if (!actualDependencies.SetEquals(expected.Dependencies.Concat(expected.OwnedObjects)))
        {
            throw new InvalidOperationException("Refusing runtime-profile changes because generated-role dependency tuples differ from the approved profile.");
        }

        var rolesValid = await ScalarAsync<bool>(connection, transaction,
            "SELECT ((@helper_present AND (SELECT count(*) = 1 FROM pg_catalog.pg_roles WHERE rolname = @helper AND NOT rolcanlogin AND NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolreplication AND NOT rolbypassrls AND NOT rolinherit)) OR (NOT @helper_present AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = @helper))) AND ((@runtime_present AND (SELECT count(*) = 1 FROM pg_catalog.pg_roles WHERE rolname = @runtime AND rolcanlogin AND NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolreplication AND NOT rolbypassrls AND NOT rolinherit)) OR (NOT @runtime_present AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = @runtime))) AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_auth_members m WHERE m.roleid IN (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = ANY(@roles)) OR m.member IN (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = ANY(@roles)))",
            command =>
            {
                command.Parameters.AddWithValue("helper", HelperRole);
                command.Parameters.AddWithValue("runtime", RuntimeRole);
                command.Parameters.AddWithValue("helper_present", expected.HelperRolePresent);
                command.Parameters.AddWithValue("runtime_present", expected.RuntimeRolePresent);
                command.Parameters.AddWithValue("roles", expected.ProfileRoles.ToArray());
            }).ConfigureAwait(false);
        if (!rolesValid)
        {
            throw new InvalidOperationException("Refusing runtime-profile changes because generated-role flags or memberships differ from the approved profile.");
        }

        foreach (var (role, expectedOid) in expected.RoleIds)
        {
            var actualOid = await ReadRoleOidAsync(connection, transaction, role).ConfigureAwait(false);
            if (actualOid != expectedOid)
            {
                throw new InvalidOperationException("Refusing runtime-profile changes because a generated role identity changed.");
            }
        }

        var functionValid = await ScalarAsync<bool>(connection, transaction,
            "SELECT CASE WHEN @present THEN EXISTS (SELECT 1 FROM pg_catalog.pg_proc p WHERE p.oid = pg_catalog.to_regprocedure('swiftaid_internal.lock_case_authority(uuid,text,text[],boolean)') AND p.proargnames = ARRAY['p_case_id','p_actor_id','p_actor_hints','p_write']::text[] AND p.proowner = (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = @helper) AND p.prosecdef AND p.proconfig = ARRAY['search_path=pg_catalog']::text[]) ELSE pg_catalog.to_regprocedure('swiftaid_internal.lock_case_authority(uuid,text,text[],boolean)') IS NULL END",
            command =>
            {
                command.Parameters.AddWithValue("present", expected.FunctionPresent);
                command.Parameters.AddWithValue("helper", HelperRole);
            }).ConfigureAwait(false);
        if (!functionValid)
        {
            var stage = expected.HelperScriptApplied
                ? "after the helper-script checkpoint"
                : "before the helper-script checkpoint";
            throw new InvalidOperationException(
                $"Refusing runtime-profile changes because helper-function signature, owner, or security configuration differs from the approved profile {stage}.");
        }
    }

    private async Task<ImmutableHashSet<GrantTuple>> ReadGrantProfileAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        ImmutableArray<string> roles)
    {
        const string sql = """
            SELECT 'schema'::text AS object_kind, n.nspname AS schema_name, ''::text AS object_name,
                   ''::text AS sub_object_name, 'pg_catalog.pg_namespace'::regclass::oid AS class_id,
                   n.oid AS object_id, 0::integer AS sub_object_id, grantee.rolname AS grantee,
                   acl.grantee AS grantee_id, acl.privilege_type, grantor.rolname AS grantor,
                   acl.grantor AS grantor_id, acl.is_grantable
              FROM pg_catalog.pg_namespace n CROSS JOIN LATERAL pg_catalog.aclexplode(n.nspacl) acl
              JOIN pg_catalog.pg_roles grantee ON grantee.oid = acl.grantee
              JOIN pg_catalog.pg_roles grantor ON grantor.oid = acl.grantor
             WHERE grantee.rolname = ANY(@roles)
            UNION ALL
            SELECT CASE WHEN c.relkind = 'S' THEN 'sequence' ELSE 'relation' END,
                   n.nspname, c.relname, ''::text, 'pg_catalog.pg_class'::regclass::oid,
                   c.oid, 0::integer, grantee.rolname, acl.grantee, acl.privilege_type,
                   grantor.rolname, acl.grantor, acl.is_grantable
              FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
              CROSS JOIN LATERAL pg_catalog.aclexplode(c.relacl) acl
              JOIN pg_catalog.pg_roles grantee ON grantee.oid = acl.grantee
              JOIN pg_catalog.pg_roles grantor ON grantor.oid = acl.grantor
             WHERE grantee.rolname = ANY(@roles)
            UNION ALL
            SELECT 'column'::text, n.nspname, c.relname, a.attname,
                   'pg_catalog.pg_class'::regclass::oid, c.oid, a.attnum::integer,
                   grantee.rolname, acl.grantee, acl.privilege_type, grantor.rolname,
                   acl.grantor, acl.is_grantable
              FROM pg_catalog.pg_attribute a JOIN pg_catalog.pg_class c ON c.oid = a.attrelid
              JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
              CROSS JOIN LATERAL pg_catalog.aclexplode(a.attacl) acl
              JOIN pg_catalog.pg_roles grantee ON grantee.oid = acl.grantee
              JOIN pg_catalog.pg_roles grantor ON grantor.oid = acl.grantor
             WHERE a.attnum > 0 AND NOT a.attisdropped
               AND grantee.rolname = ANY(@roles)
            UNION ALL
            SELECT 'function'::text, n.nspname, p.proname,
                   pg_catalog.pg_get_function_identity_arguments(p.oid),
                   'pg_catalog.pg_proc'::regclass::oid, p.oid, 0::integer,
                   grantee.rolname, acl.grantee, acl.privilege_type, grantor.rolname,
                   acl.grantor, acl.is_grantable
              FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
              CROSS JOIN LATERAL pg_catalog.aclexplode(p.proacl) acl
              JOIN pg_catalog.pg_roles grantee ON grantee.oid = acl.grantee
              JOIN pg_catalog.pg_roles grantor ON grantor.oid = acl.grantor
             WHERE grantee.rolname = ANY(@roles)
            """;
        var result = ImmutableHashSet.CreateBuilder<GrantTuple>();
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("roles", roles.ToArray());
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            result.Add(new GrantTuple(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetFieldValue<uint>(4), reader.GetFieldValue<uint>(5), reader.GetInt32(6),
                reader.GetString(7), reader.GetFieldValue<uint>(8), reader.GetString(9),
                reader.GetString(10), reader.GetFieldValue<uint>(11), reader.GetBoolean(12)));
        }

        return result.ToImmutable();
    }

    private async Task<ImmutableHashSet<DependencyTuple>> ReadDependencyProfileAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        ImmutableArray<string> roles)
    {
        const string sql = """
            SELECT role.rolname, dependency.classid, dependency.objid, dependency.objsubid,
                   dependency.deptype
              FROM pg_catalog.pg_shdepend dependency
              JOIN pg_catalog.pg_roles role ON role.oid = dependency.refobjid
             WHERE dependency.refclassid = 'pg_catalog.pg_authid'::regclass
               AND role.rolname = ANY(@roles)
            """;
        var result = ImmutableHashSet.CreateBuilder<DependencyTuple>();
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("roles", roles.ToArray());
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            result.Add(new DependencyTuple(
                reader.GetString(0), reader.GetFieldValue<uint>(1), reader.GetFieldValue<uint>(2),
                reader.GetInt32(3), reader.GetChar(4)));
        }

        return result.ToImmutable();
    }

    private static async Task<uint> ReadRoleOidAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string role)
    {
        return await ScalarAsync<uint>(connection, transaction,
            "SELECT oid FROM pg_catalog.pg_roles WHERE rolname = @role",
            command => command.Parameters.AddWithValue("role", role)).ConfigureAwait(false);
    }

    private static Task<uint> ReadRegClassOidAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string className) =>
        ScalarAsync<uint>(connection, transaction, "SELECT @class_name::regclass::oid",
            command => command.Parameters.AddWithValue("class_name", className));

    private static Task<uint> ReadSchemaOidAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string schema) =>
        ScalarAsync<uint>(connection, transaction, "SELECT oid FROM pg_catalog.pg_namespace WHERE nspname = @name",
            command => command.Parameters.AddWithValue("name", schema));

    private static Task<string> ReadSchemaOwnerAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string schema) =>
        ScalarAsync<string>(connection, transaction,
            "SELECT pg_catalog.pg_get_userbyid(nspowner) FROM pg_catalog.pg_namespace WHERE nspname = @name",
            command => command.Parameters.AddWithValue("name", schema));

    private static Task<uint> ReadRelationOidAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string relation) =>
        ScalarAsync<uint>(connection, transaction,
            "SELECT c.oid FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'public' AND c.relname = @name AND c.relkind IN ('r','p')",
            command => command.Parameters.AddWithValue("name", relation));

    private static Task<int> ReadAttributeNumberAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, uint relationOid, string column) =>
        ScalarAsync<int>(connection, transaction,
            "SELECT attnum::integer FROM pg_catalog.pg_attribute WHERE attrelid = @relation_oid AND attname = @name AND attnum > 0 AND NOT attisdropped",
            command =>
            {
                command.Parameters.AddWithValue("relation_oid", NpgsqlDbType.Oid, relationOid);
                command.Parameters.AddWithValue("name", column);
            });

    private static Task<uint> ReadHelperFunctionOidAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction) =>
        ScalarAsync<uint>(connection, transaction,
            "SELECT pg_catalog.to_regprocedure('swiftaid_internal.lock_case_authority(uuid,text,text[],boolean)')::oid",
            null);

    private async Task AssertCleanupPreconditionsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        var activeSessions = await ScalarAsync<long>(connection, transaction,
            "SELECT count(*) FROM pg_catalog.pg_stat_activity WHERE usename = ANY(@roles)",
            command => command.Parameters.AddWithValue("roles", new[] { HelperRole, RuntimeRole }))
            .ConfigureAwait(false);
        if (activeSessions != 0)
        {
            throw new InvalidOperationException("Refusing runtime-profile cleanup while generated role sessions remain active.");
        }

        var memberships = await ScalarAsync<long>(connection, transaction,
            "SELECT count(*) FROM pg_catalog.pg_auth_members m WHERE m.roleid IN (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = ANY(@roles)) OR m.member IN (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = ANY(@roles))",
            command => command.Parameters.AddWithValue("roles", new[] { HelperRole, RuntimeRole })).ConfigureAwait(false);
        if (memberships != 0)
        {
            throw new InvalidOperationException("Refusing runtime-profile cleanup because a generated role has a membership edge.");
        }

        foreach (var (role, expectedLogin, created) in new[]
                 {
                     (HelperRole, false, _helperRoleCreated),
                     (RuntimeRole, true, _runtimeRoleCreated)
                 })
        {
            if (!created)
            {
                continue;
            }

            var roleCount = await ScalarAsync<long>(connection, transaction,
                "SELECT count(*) FROM pg_catalog.pg_roles WHERE rolname = @role AND rolcanlogin = @login AND NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolreplication AND NOT rolbypassrls AND NOT rolinherit",
                command =>
                {
                    command.Parameters.AddWithValue("role", role);
                    command.Parameters.AddWithValue("login", expectedLogin);
                }).ConfigureAwait(false);
            if (roleCount != 1)
            {
                throw new InvalidOperationException("Refusing runtime-profile cleanup because a generated role's identity or flags changed.");
            }
        }

        var helperOwnedObjects = await ScalarAsync<long>(connection, transaction,
            "SELECT count(*) FROM pg_catalog.pg_shdepend d WHERE d.refclassid = 'pg_catalog.pg_authid'::regclass AND d.refobjid = (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = @role) AND d.deptype = 'o'",
            command => command.Parameters.AddWithValue("role", HelperRole)).ConfigureAwait(false);
        var runtimeOwnedObjects = await ScalarAsync<long>(connection, transaction,
            "SELECT count(*) FROM pg_catalog.pg_shdepend d WHERE d.refclassid = 'pg_catalog.pg_authid'::regclass AND d.refobjid = (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = @role) AND d.deptype = 'o'",
            command => command.Parameters.AddWithValue("role", RuntimeRole)).ConfigureAwait(false);
        var expectedHelperOwnedObjects = await HelperFunctionExistsAsync(connection, transaction).ConfigureAwait(false) ? 1L : 0L;
        if (helperOwnedObjects != expectedHelperOwnedObjects || runtimeOwnedObjects != 0)
        {
            throw new InvalidOperationException("Refusing runtime-profile cleanup because generated roles own unexpected objects.");
        }

        if (expectedHelperOwnedObjects == 1)
        {
            var validFunction = await ScalarAsync<bool>(connection, transaction,
                "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_proc p WHERE p.oid = pg_catalog.to_regprocedure('swiftaid_internal.lock_case_authority(uuid,text,text[],boolean)') AND p.proargnames = ARRAY['p_case_id','p_actor_id','p_actor_hints','p_write']::text[] AND p.proowner = (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = @helper) AND p.prosecdef AND p.proconfig = ARRAY['search_path=pg_catalog']::text[] AND (SELECT count(*) = 2 AND coalesce(bool_and(acl.grantee <> 0 AND grantee.rolname IN (@helper, @runtime) AND acl.privilege_type = 'EXECUTE' AND NOT acl.is_grantable), false) FROM pg_catalog.aclexplode(p.proacl) acl LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid = acl.grantee))",
                command =>
                {
                    command.Parameters.AddWithValue("helper", HelperRole);
                    command.Parameters.AddWithValue("runtime", RuntimeRole);
                }).ConfigureAwait(false);
            if (!validFunction)
            {
                throw new InvalidOperationException("Refusing runtime-profile cleanup because the helper function owner or ACL differs from the generated profile.");
            }
        }
    }

    private static async Task RevokeExpectedGrantAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        GrantTuple grant)
    {
        if (grant.Privilege is not ("USAGE" or "SELECT" or "INSERT" or "UPDATE"))
        {
            throw new InvalidOperationException("Refusing to revoke a privilege outside the validated runtime profile.");
        }

        var role = QuoteIdentifier(grant.Grantee);
        var sql = grant.ObjectKind switch
        {
            "schema" => $"REVOKE {grant.Privilege} ON SCHEMA {QuoteIdentifier(grant.SchemaName)} FROM {role}",
            "relation" => $"REVOKE {grant.Privilege} ON TABLE {QuoteIdentifier(grant.SchemaName)}.{QuoteIdentifier(grant.ObjectName)} FROM {role}",
            "sequence" => $"REVOKE {grant.Privilege} ON SEQUENCE {QuoteIdentifier(grant.SchemaName)}.{QuoteIdentifier(grant.ObjectName)} FROM {role}",
            "column" => $"REVOKE {grant.Privilege} ({QuoteIdentifier(grant.SubObjectName)}) ON TABLE {QuoteIdentifier(grant.SchemaName)}.{QuoteIdentifier(grant.ObjectName)} FROM {role}",
            _ => throw new InvalidOperationException("Refusing to revoke an unsupported runtime-profile object.")
        };
        await ExecuteAsync(connection, transaction,
            sql).ConfigureAwait(false);
    }

    private async Task<PsqlResult> RunPsqlAsync(string scriptPath, params (string Name, string Value)[] variables)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "psql",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-X", "-w", "-v", "ON_ERROR_STOP=1" })
        {
            startInfo.ArgumentList.Add(argument);
        }
        foreach (var variable in variables)
        {
            startInfo.ArgumentList.Add("-v");
            startInfo.ArgumentList.Add($"{variable.Name}={variable.Value}");
        }
        foreach (var argument in new[]
        {
            "-h", _owner.Host!, "-p", _owner.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-U", _owner.Username!, "-d", _owner.Database!, "-f", scriptPath
        })
        {
            startInfo.ArgumentList.Add(argument);
        }
        startInfo.Environment["PGPASSWORD"] = _owner.Password;
        startInfo.Environment["PGCONNECT_TIMEOUT"] = "5";
        startInfo.Environment["PGSSLMODE"] = _owner.SslMode.ToString().ToLowerInvariant();

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Could not start psql for the isolated runtime profile.");
        }

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        return new PsqlResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private static async Task AssertRoleAbsentAsync(NpgsqlConnection connection, string role)
    {
        if (!await ScalarAsync<bool>(connection, null,
                "SELECT NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = @role)",
                command => command.Parameters.AddWithValue("role", role)).ConfigureAwait(false))
        {
            throw new InvalidOperationException("A generated runtime-profile role name already exists.");
        }
    }

    private static async Task CreateRoleAsync(NpgsqlConnection connection, string role, bool login, string? password)
    {
        var passwordClause = password is null ? string.Empty : $" PASSWORD '{password}'";
        var loginClause = login ? "LOGIN" : "NOLOGIN";
        await using var command = new NpgsqlCommand(
            $"CREATE ROLE {QuoteIdentifier(role)} {loginClause}{passwordClause} NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS",
            connection);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<bool> HelperFunctionExistsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction) =>
        await ScalarAsync<bool>(connection, transaction,
            "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_proc p WHERE p.oid = pg_catalog.to_regprocedure('swiftaid_internal.lock_case_authority(uuid,text,text[],boolean)') AND p.proargnames = ARRAY['p_case_id','p_actor_id','p_actor_hints','p_write']::text[])",
            null)
            .ConfigureAwait(false);

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        Action<NpgsqlCommand>? bind = null)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        bind?.Invoke(command);

        var value = await command.ExecuteScalarAsync().ConfigureAwait(false);
        if (value is T typed)
        {
            return typed;
        }
        throw new InvalidOperationException("A runtime-profile catalog guard returned an unexpected value.");
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static string FindRepositoryFile(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new FileNotFoundException("The checked-in PostgreSQL profile script was not found.", Path.GetFileName(relativePath));
    }

    private static void AssertPsqlSucceeded(PsqlResult result, string message)
    {
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{message} psql exit code {result.ExitCode}; {result.StandardError}");
        }
    }

    private static string QuoteIdentifier(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private sealed record RuntimeGrantProfile(
        ImmutableHashSet<GrantTuple> Grants,
        ImmutableHashSet<DependencyTuple> Dependencies,
        ImmutableHashSet<DependencyTuple> OwnedObjects,
        bool HelperRolePresent,
        bool RuntimeRolePresent,
        bool HelperScriptApplied,
        bool RuntimeScriptApplied,
        bool FunctionPresent,
        ImmutableArray<string> ProfileRoles,
        ImmutableDictionary<string, uint> RoleIds);

    private sealed record GrantTuple(
        string ObjectKind,
        string SchemaName,
        string ObjectName,
        string SubObjectName,
        uint ClassId,
        uint ObjectId,
        int SubObjectId,
        string Grantee,
        uint GranteeId,
        string Privilege,
        string Grantor,
        uint GrantorId,
        bool IsGrantable);

    private sealed record DependencyTuple(
        string Role,
        uint ClassId,
        uint ObjectId,
        int SubObjectId,
        char DependencyType);

    private sealed record PsqlResult(int ExitCode, string StandardOutput, string StandardError);
}
