using System.Diagnostics;
using System.Security.Cryptography;
using Npgsql;

namespace SwiftAid.Integration.Tests;

[Collection(CaseAuthorityPostgresCollection.Name)]
public sealed class CaseCommandRuntimeGrantsPostgresTests
{
    private static readonly string[] SelectableRelations =
    [
        "organizations",
        "workspaces",
        "workspace_organizations",
        "actor_access_states",
        "actors",
        "organization_memberships",
        "access_grant_groups",
        "access_grant_group_regions",
        "access_grant_group_operations",
        "case_shares",
        "managed_cases",
        "managed_case_regions",
        "protected_event_payloads",
        "case_events",
        "command_receipts"
    ];

    private static readonly string[] AllPersistenceRelations =
    [
        .. SelectableRelations,
        "domain_outbox",
        "security_audit_entries"
    ];

    [CaseAuthorityPostgresFact]
    public async Task Runtime_grants_are_minimal_effective_and_reject_acl_pollution()
    {
        await using var fixture = await CaseAuthorityPostgresFixture.CreateAsync().ConfigureAwait(false);
        var owner = new NpgsqlConnectionStringBuilder(fixture.ConnectionString);
        var suffix = Guid.NewGuid().ToString("N");
        var helperRole = $"sae1_helper_{suffix}";
        var runtimeRole = $"sae1_runtime_{suffix}";
        var runtimePassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var createdRoles = new HashSet<string>(StringComparer.Ordinal);
        var createdPublicRelations = new Dictionary<string, (string Owner, bool PublicSelectGranted)>(StringComparer.Ordinal);
        var createdInternalRelations = new Dictionary<string, (string Owner, bool PublicSelectGranted)>(StringComparer.Ordinal);
        var createdSequences = new Dictionary<string, (string Owner, string GrantedRole)>(StringComparer.Ordinal);
        var roleMemberships = new List<(string GrantedRole, string MemberRole)>();
        await using var ownerConnection = new NpgsqlConnection(owner.ConnectionString);
        await ownerConnection.OpenAsync().ConfigureAwait(false);
        try
        {
            Assert.Null(await GetHelperFunctionOwnerAsync(ownerConnection).ConfigureAwait(false));

            await CreateRoleIfAbsentAsync(owner, helperRole, login: false, password: null).ConfigureAwait(false);
            createdRoles.Add(helperRole);
            await CreateRoleIfAbsentAsync(owner, runtimeRole, login: true, password: runtimePassword).ConfigureAwait(false);
            createdRoles.Add(runtimeRole);

            var helperResult = await RunPsqlAsync(
                owner,
                FindRepositoryFile("infra/postgres/case-authority-lock-helper.sql"),
                ("helper_owner", helperRole),
                ("runtime_role", runtimeRole)).ConfigureAwait(false);
            AssertPsqlSucceeded(helperResult, "The repository lock-helper SQL did not provision the isolated test roles.");
            Assert.Equal(helperRole, await GetHelperFunctionOwnerAsync(ownerConnection).ConfigureAwait(false));
            var grantsPath = FindRepositoryFile("infra/postgres/case-command-runtime-grants.sql");
            var firstGrantResult = await RunPsqlAsync(owner, grantsPath, ("runtime_role", runtimeRole)).ConfigureAwait(false);
            AssertPsqlSucceeded(firstGrantResult, "The repository runtime-grants SQL failed its first application.");
            var secondGrantResult = await RunPsqlAsync(owner, grantsPath, ("runtime_role", runtimeRole)).ConfigureAwait(false);
            AssertPsqlSucceeded(secondGrantResult, "The repository runtime-grants SQL was not safely repeatable.");

            await AssertHelperFunctionAclAsync(ownerConnection, helperRole, runtimeRole).ConfigureAwait(false);
            await AssertRuntimeProfileAsync(
                owner,
                ownerConnection,
                runtimeRole,
                runtimePassword,
                helperRole,
                fixture,
                createdPublicRelations,
                createdInternalRelations).ConfigureAwait(false);
            await AssertPreflightRejectsPollutionAsync(
                owner,
                grantsPath,
                helperRole,
                runtimeRole,
                suffix,
                createdRoles,
                createdPublicRelations,
                createdSequences,
                roleMemberships).ConfigureAwait(false);
        }
        finally
        {
            await CleanupOwnedArtifactsAsync(
                ownerConnection,
                helperRole,
                createdRoles,
                createdPublicRelations,
                createdInternalRelations,
                createdSequences,
                roleMemberships).ConfigureAwait(false);
        }
    }

    private static async Task AssertRuntimeProfileAsync(
        NpgsqlConnectionStringBuilder owner,
        NpgsqlConnection ownerConnection,
        string runtimeRole,
        string runtimePassword,
        string helperRole,
        CaseAuthorityPostgresFixture fixture,
        Dictionary<string, (string Owner, bool PublicSelectGranted)> createdPublicRelations,
        Dictionary<string, (string Owner, bool PublicSelectGranted)> createdInternalRelations)
    {
        var runtimeBuilder = new NpgsqlConnectionStringBuilder(owner.ConnectionString)
        {
            Username = runtimeRole,
            Password = runtimePassword,
            Timeout = 5,
            CommandTimeout = 10
        };
        await using var connection = new NpgsqlConnection(runtimeBuilder.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        foreach (var relation in SelectableRelations)
        {
            await AssertQuerySucceedsAsync(connection, $"SELECT * FROM public.{QuoteIdentifier(relation)} LIMIT 0").ConfigureAwait(false);
        }

        Assert.False(await HasTablePrivilegeAsync(connection, "domain_outbox", "SELECT").ConfigureAwait(false));
        Assert.False(await HasTablePrivilegeAsync(connection, "security_audit_entries", "SELECT").ConfigureAwait(false));
        await AssertPermissionDeniedAsync(connection, "SELECT outbox_id FROM public.domain_outbox LIMIT 0").ConfigureAwait(false);
        await AssertPermissionDeniedAsync(connection, "SELECT audit_id FROM public.security_audit_entries LIMIT 0").ConfigureAwait(false);

        foreach (var privilege in new[] { "SELECT", "USAGE", "UPDATE" })
        {
            Assert.True(await NoPublicSequencePrivilegeAsync(connection, privilege).ConfigureAwait(false),
                $"The runtime role unexpectedly has {privilege} on a public non-extension sequence.");
        }

        Assert.Equal((short)0, await InvokeLockHelperAsync(connection, fixture.CaseId, fixture.ActorId).ConfigureAwait(false));
        await AssertPermissionDeniedAsync(connection, $"SET ROLE {QuoteIdentifier(helperRole)}").ConfigureAwait(false);
        var publicCreateProbe = $"sae1_runtime_create_{Guid.NewGuid():N}";
        await AssertRelationAbsentAsync(ownerConnection, "public", publicCreateProbe).ConfigureAwait(false);
        await AssertCreateDeniedAndTrackUnexpectedSuccessAsync(
            connection,
            "public",
            publicCreateProbe,
            runtimeRole,
            createdPublicRelations,
            $"CREATE TABLE public.{QuoteIdentifier(publicCreateProbe)} (probe_id integer)").ConfigureAwait(false);

        var internalCreateProbe = $"sae1_runtime_internal_{Guid.NewGuid():N}";
        await AssertRelationAbsentAsync(ownerConnection, "swiftaid_internal", internalCreateProbe).ConfigureAwait(false);
        await AssertCreateDeniedAndTrackUnexpectedSuccessAsync(
            connection,
            "swiftaid_internal",
            internalCreateProbe,
            runtimeRole,
            createdInternalRelations,
            $"CREATE TABLE swiftaid_internal.{QuoteIdentifier(internalCreateProbe)} (probe_id integer)").ConfigureAwait(false);

        await AssertPermissionDeniedAsync(
            connection,
            "UPDATE public.actors SET status = status WHERE actor_id = '__sae1_no_matching_actor__'").ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connection,
            "UPDATE public.organization_memberships SET status = status WHERE actor_id = '__sae1_no_matching_actor__'").ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connection,
            "UPDATE public.managed_cases SET classification = classification WHERE case_id = '00000000-0000-0000-0000-000000000000'").ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connection,
            "UPDATE public.case_events SET event_type = event_type WHERE event_id = '00000000-0000-0000-0000-000000000000'").ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connection,
            "DELETE FROM public.case_events WHERE event_id = '00000000-0000-0000-0000-000000000000'").ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connection,
            "DELETE FROM public.command_receipts WHERE receipt_id = '00000000-0000-0000-0000-000000000000'").ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connection,
            "UPDATE public.domain_outbox SET published_at_utc = published_at_utc WHERE outbox_id = '00000000-0000-0000-0000-000000000000'").ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connection,
            "DELETE FROM public.domain_outbox WHERE outbox_id = '00000000-0000-0000-0000-000000000000'").ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connection,
            "UPDATE public.security_audit_entries SET reason_code = reason_code WHERE audit_id = '00000000-0000-0000-0000-000000000000'").ConfigureAwait(false);
        await AssertPermissionDeniedAsync(
            connection,
            "DELETE FROM public.security_audit_entries WHERE audit_id = '00000000-0000-0000-0000-000000000000'").ConfigureAwait(false);

        await AssertApprovedWritesRollbackAsync(connection, fixture).ConfigureAwait(false);
    }

    private static async Task AssertApprovedWritesRollbackAsync(
        NpgsqlConnection connection,
        CaseAuthorityPostgresFixture fixture)
    {
        await using var transaction = await connection.BeginTransactionAsync().ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var commandId = Guid.NewGuid();
        var payloadId = Guid.NewGuid();
        var eventId = Guid.NewGuid();

        await using (var update = new NpgsqlCommand(
            "UPDATE public.actor_access_states SET access_epoch = access_epoch + 1, version = version + 1, updated_at_utc = @now WHERE actor_id = @actor_id",
            connection,
            transaction))
        {
            update.Parameters.AddWithValue("now", now);
            update.Parameters.AddWithValue("actor_id", fixture.ActorId);
            Assert.Equal(1, await update.ExecuteNonQueryAsync().ConfigureAwait(false));
        }

        await using (var update = new NpgsqlCommand(
            "UPDATE public.managed_cases SET aggregate_version = aggregate_version + 1, report_revision = report_revision + 1, resource_version = resource_version + 1, workflow = workflow, workflow_version = workflow_version + 1, accountable_owner_actor_id = accountable_owner_actor_id, updated_at_utc = @now WHERE case_id = @case_id",
            connection,
            transaction))
        {
            update.Parameters.AddWithValue("now", now);
            update.Parameters.AddWithValue("case_id", fixture.CaseId);
            Assert.Equal(1, await update.ExecuteNonQueryAsync().ConfigureAwait(false));
        }

        await using (var insert = new NpgsqlCommand(
            "INSERT INTO public.protected_event_payloads (payload_id, protected_bytes, protection_purpose_version, created_at_utc) VALUES (@payload_id, @bytes, @purpose, @now)",
            connection,
            transaction))
        {
            insert.Parameters.AddWithValue("payload_id", payloadId);
            insert.Parameters.AddWithValue("bytes", new byte[] { 0x53, 0x41, 0x45, 0x31 });
            insert.Parameters.AddWithValue("purpose", "case-event/test-v1");
            insert.Parameters.AddWithValue("now", now);
            Assert.Equal(1, await insert.ExecuteNonQueryAsync().ConfigureAwait(false));
        }

        await using (var insert = new NpgsqlCommand(
            "INSERT INTO public.command_receipts (receipt_id, actor_id, command_id, case_id, command_type, schema_version, canonical_payload_hash, first_aggregate_version, last_aggregate_version, report_revision, accepted_at_utc) VALUES (@receipt_id, @actor_id, @command_id, @case_id, @command_type, 1, @payload_hash, 9001, 9001, 1, @now)",
            connection,
            transaction))
        {
            insert.Parameters.AddWithValue("receipt_id", Guid.NewGuid());
            insert.Parameters.AddWithValue("actor_id", fixture.ActorId);
            insert.Parameters.AddWithValue("command_id", commandId);
            insert.Parameters.AddWithValue("case_id", fixture.CaseId);
            insert.Parameters.AddWithValue("command_type", "runtime-grant-integration-probe");
            insert.Parameters.AddWithValue("payload_hash", RandomNumberGenerator.GetBytes(32));
            insert.Parameters.AddWithValue("now", now);
            Assert.Equal(1, await insert.ExecuteNonQueryAsync().ConfigureAwait(false));
        }

        await using (var insert = new NpgsqlCommand(
            "INSERT INTO public.case_events (event_id, case_id, aggregate_version, report_revision, command_id, actor_id, event_type, schema_version, occurred_at_utc, recorded_at_utc, payload_id) VALUES (@event_id, @case_id, 9001, 1, @command_id, @actor_id, 'CaseReported', 1, @now, @now, @payload_id)",
            connection,
            transaction))
        {
            insert.Parameters.AddWithValue("event_id", eventId);
            insert.Parameters.AddWithValue("case_id", fixture.CaseId);
            insert.Parameters.AddWithValue("command_id", commandId);
            insert.Parameters.AddWithValue("actor_id", fixture.ActorId);
            insert.Parameters.AddWithValue("now", now);
            insert.Parameters.AddWithValue("payload_id", payloadId);
            Assert.Equal(1, await insert.ExecuteNonQueryAsync().ConfigureAwait(false));
        }

        await using (var insert = new NpgsqlCommand(
            "INSERT INTO public.domain_outbox (outbox_id, event_id, message_type, created_at_utc, available_at_utc, attempt_count, lease_until_utc, published_at_utc) VALUES (@outbox_id, @event_id, 'case-event/runtime-grant-probe', @now, @now, 0, NULL, NULL)",
            connection,
            transaction))
        {
            insert.Parameters.AddWithValue("outbox_id", Guid.NewGuid());
            insert.Parameters.AddWithValue("event_id", eventId);
            insert.Parameters.AddWithValue("now", now);
            Assert.Equal(1, await insert.ExecuteNonQueryAsync().ConfigureAwait(false));
        }

        await using (var insert = new NpgsqlCommand(
            "INSERT INTO public.security_audit_entries (audit_id, actor_id, target_actor_id, resource_id, operation, decision_code, access_epoch, recorded_at_utc, reason_code) VALUES (@audit_id, @actor_id, NULL, @resource_id, 'runtime-grant-probe', 'allowed', 17, @now, NULL)",
            connection,
            transaction))
        {
            insert.Parameters.AddWithValue("audit_id", Guid.NewGuid());
            insert.Parameters.AddWithValue("actor_id", fixture.ActorId);
            insert.Parameters.AddWithValue("resource_id", fixture.CaseId);
            insert.Parameters.AddWithValue("now", now);
            Assert.Equal(1, await insert.ExecuteNonQueryAsync().ConfigureAwait(false));
        }

        await transaction.RollbackAsync().ConfigureAwait(false);
    }

    private static async Task AssertPreflightRejectsPollutionAsync(
        NpgsqlConnectionStringBuilder owner,
        string grantsPath,
        string helperRole,
        string runtimeRole,
        string suffix,
        HashSet<string> createdRoles,
        Dictionary<string, (string Owner, bool PublicSelectGranted)> createdPublicRelations,
        Dictionary<string, (string Owner, string GrantedRole)> createdSequences,
        List<(string GrantedRole, string MemberRole)> roleMemberships)
    {
        await using var ownerConnection = new NpgsqlConnection(owner.ConnectionString);
        await ownerConnection.OpenAsync().ConfigureAwait(false);

        var directRole = $"sae1_direct_{suffix}";
        await CreateRoleIfAbsentAsync(owner, directRole, login: true, password: Convert.ToHexString(RandomNumberGenerator.GetBytes(32))).ConfigureAwait(false);
        createdRoles.Add(directRole);
        await ExecuteOwnerAsync(owner, $"GRANT SELECT ON TABLE public.domain_outbox TO {QuoteIdentifier(directRole)}").ConfigureAwait(false);
        await AssertPreflightRejectedAsync(owner, grantsPath, directRole, "effective SELECT outside the approved SwiftAid relation allowlist").ConfigureAwait(false);

        var inheritedRole = $"sae1_inherited_{suffix}";
        var inheritedGrant = $"sae1_grant_{suffix}";
        await CreateRoleIfAbsentAsync(owner, inheritedRole, login: true, password: Convert.ToHexString(RandomNumberGenerator.GetBytes(32))).ConfigureAwait(false);
        createdRoles.Add(inheritedRole);
        await CreateRoleIfAbsentAsync(owner, inheritedGrant, login: false, password: null).ConfigureAwait(false);
        createdRoles.Add(inheritedGrant);
        await ExecuteOwnerAsync(owner, $"GRANT SELECT ON TABLE public.domain_outbox TO {QuoteIdentifier(inheritedGrant)}").ConfigureAwait(false);
        await ExecuteOwnerAsync(owner, $"GRANT {QuoteIdentifier(inheritedGrant)} TO {QuoteIdentifier(inheritedRole)}").ConfigureAwait(false);
        roleMemberships.Add((inheritedGrant, inheritedRole));
        await AssertPreflightRejectedAsync(owner, grantsPath, inheritedRole, "runtime_role is missing or has an unsafe login, owner, membership, or privilege configuration").ConfigureAwait(false);

        var sequenceRole = $"sae1_sequence_{suffix}";
        var sequence = $"sae1_sequence_probe_{suffix}";
        var sequenceOwner = owner.Username
            ?? throw new InvalidOperationException("The approved disposable PostgreSQL owner is required.");
        await CreateRoleIfAbsentAsync(owner, sequenceRole, login: true, password: Convert.ToHexString(RandomNumberGenerator.GetBytes(32))).ConfigureAwait(false);
        createdRoles.Add(sequenceRole);
        await AssertRelationAbsentAsync(ownerConnection, "public", sequence).ConfigureAwait(false);
        await ExecuteOwnerAsync(owner, $"CREATE SEQUENCE public.{QuoteIdentifier(sequence)}").ConfigureAwait(false);
        createdSequences.Add(sequence, (sequenceOwner, sequenceRole));
        await ExecuteOwnerAsync(owner, $"GRANT USAGE ON SEQUENCE public.{QuoteIdentifier(sequence)} TO {QuoteIdentifier(sequenceRole)}").ConfigureAwait(false);
        await AssertPreflightRejectedAsync(owner, grantsPath, sequenceRole, "effective SELECT outside the approved SwiftAid relation allowlist").ConfigureAwait(false);

        var publicRole = $"sae1_public_{suffix}";
        var publicRelation = $"sae1_public_probe_{suffix}";
        var publicProbeOwner = owner.Username
            ?? throw new InvalidOperationException("The approved disposable PostgreSQL owner is required.");
        await CreateRoleIfAbsentAsync(owner, publicRole, login: true, password: Convert.ToHexString(RandomNumberGenerator.GetBytes(32))).ConfigureAwait(false);
        createdRoles.Add(publicRole);
        await AssertRelationAbsentAsync(ownerConnection, "public", publicRelation).ConfigureAwait(false);
        await ExecuteOwnerAsync(owner, $"CREATE TABLE public.{QuoteIdentifier(publicRelation)} (probe_id integer)").ConfigureAwait(false);
        createdPublicRelations.Add(publicRelation, (publicProbeOwner, PublicSelectGranted: false));
        await ExecuteOwnerAsync(owner, $"GRANT SELECT ON TABLE public.{QuoteIdentifier(publicRelation)} TO PUBLIC").ConfigureAwait(false);
        createdPublicRelations[publicRelation] = (publicProbeOwner, PublicSelectGranted: true);
        await AssertPreflightRejectedAsync(owner, grantsPath, publicRole, "effective SELECT outside the approved SwiftAid relation allowlist").ConfigureAwait(false);

        Assert.True(await HelperFunctionHasNoPublicOrUnexpectedExecuteAsync(owner, helperRole, runtimeRole).ConfigureAwait(false),
            "The SECURITY DEFINER lock helper has a PUBLIC or unexpected EXECUTE grant.");
    }

    private static async Task AssertPreflightRejectedAsync(
        NpgsqlConnectionStringBuilder owner,
        string grantsPath,
        string runtimeRole,
        string expectedMessage)
    {
        var result = await RunPsqlAsync(owner, grantsPath, ("runtime_role", runtimeRole)).ConfigureAwait(false);
        Assert.Equal(3, result.ExitCode);
        Assert.Contains(expectedMessage, result.StandardOutput + result.StandardError, StringComparison.Ordinal);
    }

    private static async Task AssertHelperFunctionAclAsync(
        NpgsqlConnection connection,
        string helperRole,
        string runtimeRole)
    {
        Assert.True(await HelperFunctionHasNoPublicOrUnexpectedExecuteAsync(connection, helperRole, runtimeRole).ConfigureAwait(false),
            "The SECURITY DEFINER lock helper must be executable only by its owner and the configured runtime role.");
    }

    private static async Task<bool> HelperFunctionHasNoPublicOrUnexpectedExecuteAsync(
        NpgsqlConnectionStringBuilder connection,
        string helperRole,
        string runtimeRole)
    {
        await using var database = new NpgsqlConnection(connection.ConnectionString);
        await database.OpenAsync().ConfigureAwait(false);
        return await HelperFunctionHasNoPublicOrUnexpectedExecuteAsync(database, helperRole, runtimeRole).ConfigureAwait(false);
    }

    private static async Task<bool> HelperFunctionHasNoPublicOrUnexpectedExecuteAsync(
        NpgsqlConnection connection,
        string helperRole,
        string runtimeRole)
    {
        const string sql = """
            SELECT count(*) = 2
               AND coalesce(bool_and(
                   privilege.grantee <> 0
                   AND grantee.rolname IN (@helper_role, @runtime_role)
                   AND privilege.privilege_type = 'EXECUTE'
               ), false)
              FROM pg_catalog.pg_proc AS proc
              CROSS JOIN LATERAL pg_catalog.aclexplode(proc.proacl) AS privilege
              LEFT JOIN pg_catalog.pg_roles AS grantee ON grantee.oid = privilege.grantee
             WHERE proc.oid = pg_catalog.to_regprocedure('swiftaid_internal.lock_case_authority(uuid,text,text[],boolean)')
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("helper_role", helperRole);
        command.Parameters.AddWithValue("runtime_role", runtimeRole);
        return (bool)(await command.ExecuteScalarAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("The helper-function ACL query returned no value."));
    }

    private static async Task<string?> GetHelperFunctionOwnerAsync(NpgsqlConnection connection)
    {
        const string sql = """
            SELECT pg_catalog.pg_get_userbyid(function.proowner)
              FROM pg_catalog.pg_proc AS function
             WHERE function.oid = pg_catalog.to_regprocedure('swiftaid_internal.lock_case_authority(uuid,text,text[],boolean)')
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        return (string?)await command.ExecuteScalarAsync().ConfigureAwait(false);
    }

    private static async Task<short> InvokeLockHelperAsync(NpgsqlConnection connection, Guid caseId, string actorId)
    {
        await using var command = new NpgsqlCommand(
            "SELECT swiftaid_internal.lock_case_authority(@case_id, @actor_id, ARRAY[@actor_id]::text[], false)",
            connection);
        command.Parameters.AddWithValue("case_id", caseId);
        command.Parameters.AddWithValue("actor_id", actorId);
        return Convert.ToInt16(await command.ExecuteScalarAsync().ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<bool> HasTablePrivilegeAsync(NpgsqlConnection connection, string relation, string privilege)
    {
        await using var command = new NpgsqlCommand("SELECT pg_catalog.has_table_privilege(current_user, @relation, @privilege)", connection);
        command.Parameters.AddWithValue("relation", $"public.{relation}");
        command.Parameters.AddWithValue("privilege", privilege);
        return (bool)(await command.ExecuteScalarAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("The table-privilege query returned no value."));
    }

    private static async Task<bool> NoPublicSequencePrivilegeAsync(NpgsqlConnection connection, string privilege)
    {
        const string sql = """
            SELECT NOT EXISTS (
                SELECT 1
                  FROM pg_catalog.pg_class AS sequence
                  JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = sequence.relnamespace
                 WHERE namespace.nspname = 'public'
                   AND sequence.relkind = 'S'
                   AND NOT EXISTS (
                       SELECT 1
                         FROM pg_catalog.pg_depend AS extension_membership
                        WHERE extension_membership.classid = 'pg_catalog.pg_class'::regclass
                          AND extension_membership.objid = sequence.oid
                          AND extension_membership.refclassid = 'pg_catalog.pg_extension'::regclass
                          AND extension_membership.deptype = 'e'
                   )
                   AND pg_catalog.has_sequence_privilege(current_user, sequence.oid, @privilege)
            )
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("privilege", privilege);
        return (bool)(await command.ExecuteScalarAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("The sequence-privilege query returned no value."));
    }

    private static async Task AssertQuerySucceedsAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        Assert.False(await reader.ReadAsync().ConfigureAwait(false));
    }

    private static async Task AssertRelationAbsentAsync(NpgsqlConnection connection, string schema, string relation)
    {
        await using var command = new NpgsqlCommand("SELECT pg_catalog.to_regclass(@qualified_name) IS NULL", connection);
        command.Parameters.AddWithValue("qualified_name", $"{QuoteIdentifier(schema)}.{QuoteIdentifier(relation)}");
        Assert.True((bool)(await command.ExecuteScalarAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("The probe-relation absence check returned no value.")),
            "A generated runtime CREATE probe name unexpectedly exists before the test attempts creation.");
    }

    private static async Task AssertRoleAbsentAsync(NpgsqlConnectionStringBuilder connection, string role)
    {
        await using var database = new NpgsqlConnection(connection.ConnectionString);
        await database.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "SELECT NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = @role)",
            database);
        command.Parameters.AddWithValue("role", role);
        Assert.True((bool)(await command.ExecuteScalarAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("The generated-role absence check returned no value.")),
            "A generated task role name unexpectedly exists before role creation.");
    }

    private static async Task CreateRoleIfAbsentAsync(
        NpgsqlConnectionStringBuilder connection,
        string role,
        bool login,
        string? password)
    {
        await AssertRoleAbsentAsync(connection, role).ConfigureAwait(false);
        await CreateRoleAsync(connection, role, login, password).ConfigureAwait(false);
    }

    private static async Task AssertCreateDeniedAndTrackUnexpectedSuccessAsync(
        NpgsqlConnection connection,
        string schema,
        string relation,
        string expectedOwner,
        Dictionary<string, (string Owner, bool PublicSelectGranted)> createdRelations,
        string sql)
    {
        try
        {
            await ExecuteOwnerAsync(connection, sql).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == "42501")
        {
            return;
        }

        createdRelations.Add(relation, (expectedOwner, PublicSelectGranted: false));
        Assert.Fail($"The runtime role unexpectedly created {schema}.{relation}.");
    }

    private static async Task DropOwnedProbeTableAsync(
        NpgsqlConnection connection,
        string schema,
        string relation,
        string expectedOwner,
        bool publicSelectGranted)
    {
        var isExactTaskTable = await IsExpectedProbeTableAsync(connection, schema, relation, expectedOwner, publicSelectGranted).ConfigureAwait(false);
        if (isExactTaskTable is null)
        {
            return;
        }

        if (!isExactTaskTable.Value)
        {
            throw new InvalidOperationException(
                $"Refusing to remove {schema}.{relation}; its owner, type, or dependency provenance differs from this task's probe table.");
        }

        if (publicSelectGranted)
        {
            await ExecuteOwnerAsync(connection,
                $"REVOKE SELECT ON TABLE {QuoteIdentifier(schema)}.{QuoteIdentifier(relation)} FROM PUBLIC").ConfigureAwait(false);
        }

        await ExecuteOwnerAsync(connection,
            $"DROP TABLE {QuoteIdentifier(schema)}.{QuoteIdentifier(relation)}").ConfigureAwait(false);
    }

    private static async Task DropOwnedProbeSequenceAsync(
        NpgsqlConnection connection,
        string sequence,
        string expectedOwner,
        string grantedRole)
    {
        var isExactTaskSequence = await IsExpectedProbeSequenceAsync(
            connection,
            sequence,
            expectedOwner,
            grantedRole).ConfigureAwait(false);
        if (isExactTaskSequence is null)
        {
            return;
        }

        if (!isExactTaskSequence.Value)
        {
            throw new InvalidOperationException(
                $"Refusing to remove public.{sequence}; its owner, type, ACL, or dependency provenance differs from this task's sequence probe.");
        }

        await ExecuteOwnerAsync(connection,
            $"REVOKE ALL PRIVILEGES ON SEQUENCE public.{QuoteIdentifier(sequence)} FROM {QuoteIdentifier(grantedRole)}").ConfigureAwait(false);
        await ExecuteOwnerAsync(connection,
            $"DROP SEQUENCE public.{QuoteIdentifier(sequence)}").ConfigureAwait(false);
    }

    private static async Task<bool?> IsExpectedProbeSequenceAsync(
        NpgsqlConnection connection,
        string sequence,
        string expectedOwner,
        string grantedRole)
    {
        const string sql = """
            SELECT CASE
                     WHEN target.oid IS NULL THEN NULL
                     ELSE coalesce(
                         owner.oid IS NOT NULL
                         AND target.relowner = owner.oid
                         AND target.relkind = 'S'
                         AND target.relpersistence = 'p'
                         AND granted_role.oid IS NOT NULL
                         AND NOT EXISTS (
                             SELECT 1
                               FROM pg_catalog.pg_depend AS dependency
                              WHERE (dependency.classid = 'pg_catalog.pg_class'::regclass
                                     AND dependency.objid = target.oid)
                                 AND NOT (
                                     dependency.refclassid = 'pg_catalog.pg_namespace'::regclass
                                     AND dependency.refobjid = target.relnamespace
                                     AND dependency.objsubid = 0
                                     AND dependency.refobjsubid = 0
                                     AND dependency.deptype = 'n'
                                 )
                                 OR (dependency.refclassid = 'pg_catalog.pg_class'::regclass
                                     AND dependency.refobjid = target.oid)
                         )
                         AND NOT EXISTS (
                             SELECT 1
                               FROM pg_catalog.aclexplode(target.relacl) AS acl
                              WHERE acl.grantee <> target.relowner
                                AND NOT (
                                    acl.grantee = granted_role.oid
                                    AND acl.privilege_type = 'USAGE'
                                    AND NOT acl.is_grantable
                                )
                         )
                         AND (
                             SELECT count(*)
                               FROM pg_catalog.aclexplode(target.relacl) AS acl
                              WHERE acl.grantee = granted_role.oid
                         ) <= 1,
                         false
                     )
                   END
              FROM pg_catalog.pg_class AS target
              RIGHT JOIN (SELECT pg_catalog.to_regclass(@qualified_name) AS oid) AS resolved
                ON target.oid = resolved.oid
              LEFT JOIN pg_catalog.pg_roles AS owner ON owner.rolname = @expected_owner
              LEFT JOIN pg_catalog.pg_roles AS granted_role ON granted_role.rolname = @granted_role
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("qualified_name", $"public.{QuoteIdentifier(sequence)}");
        command.Parameters.AddWithValue("expected_owner", expectedOwner);
        command.Parameters.AddWithValue("granted_role", grantedRole);
        var result = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return result is null or DBNull ? null : (bool)result;
    }

    private static async Task<bool?> IsExpectedProbeTableAsync(
        NpgsqlConnection connection,
        string schema,
        string relation,
        string expectedOwner,
        bool publicSelectGranted)
    {
        const string sql = """
            SELECT CASE
                     WHEN target.oid IS NULL THEN NULL
                     ELSE coalesce(
                         owner.oid IS NOT NULL
                         AND target.relowner = owner.oid
                         AND target.relkind = 'r'
                         AND target.relpersistence = 'p'
                         AND target.relnatts = 1
                         AND (
                             SELECT count(*)
                               FROM pg_catalog.pg_attribute AS attribute
                              WHERE attribute.attrelid = target.oid
                                AND attribute.attnum > 0
                                AND NOT attribute.attisdropped
                         ) = 1
                         AND EXISTS (
                             SELECT 1
                               FROM pg_catalog.pg_attribute AS attribute
                              WHERE attribute.attrelid = target.oid
                                AND attribute.attnum = 1
                                AND attribute.attname = 'probe_id'
                                AND attribute.atttypid = 'pg_catalog.int4'::regtype
                                AND NOT attribute.attnotnull
                         )
                         AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_index WHERE indrelid = target.oid)
                         AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_constraint WHERE conrelid = target.oid)
                         AND NOT EXISTS (
                             SELECT 1 FROM pg_catalog.pg_trigger
                              WHERE tgrelid = target.oid AND NOT tgisinternal
                         )
                         AND NOT EXISTS (
                             SELECT 1
                               FROM pg_catalog.aclexplode(target.relacl) AS acl
                              WHERE acl.grantee <> target.relowner
                                AND NOT (
                                    @public_select_granted
                                    AND acl.grantee = 0
                                    AND acl.privilege_type = 'SELECT'
                                    AND NOT acl.is_grantable
                                )
                         )
                         AND (
                             SELECT count(*)
                               FROM pg_catalog.aclexplode(target.relacl) AS acl
                              WHERE acl.grantee = 0
                         ) = CASE WHEN @public_select_granted THEN 1 ELSE 0 END
                         AND NOT EXISTS (
                             SELECT 1
                               FROM pg_catalog.pg_attribute AS column_attribute
                               CROSS JOIN LATERAL pg_catalog.aclexplode(column_attribute.attacl) AS column_acl
                              WHERE column_attribute.attrelid = target.oid
                                AND column_attribute.attnum > 0
                                AND NOT column_attribute.attisdropped
                                AND column_acl.grantee <> target.relowner
                         )
                         AND EXISTS (
                             SELECT 1
                               FROM pg_catalog.pg_depend AS dependency
                              WHERE dependency.classid = 'pg_catalog.pg_type'::regclass
                                AND dependency.objid = target.reltype
                                AND dependency.refclassid = 'pg_catalog.pg_class'::regclass
                                AND dependency.refobjid = target.oid
                                AND dependency.deptype = 'i'
                         )
                         AND NOT EXISTS (
                             SELECT 1
                               FROM pg_catalog.pg_depend AS dependency
                              WHERE dependency.refclassid = 'pg_catalog.pg_class'::regclass
                                AND dependency.refobjid = target.oid
                                AND NOT (
                                    dependency.classid = 'pg_catalog.pg_type'::regclass
                                    AND dependency.objid = target.reltype
                                    AND dependency.deptype = 'i'
                                )
                         ),
                         false
                     )
                   END
              FROM pg_catalog.pg_class AS target
              RIGHT JOIN (SELECT pg_catalog.to_regclass(@qualified_name) AS oid) AS resolved
                ON target.oid = resolved.oid
              LEFT JOIN pg_catalog.pg_roles AS owner ON owner.rolname = @expected_owner
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("qualified_name", $"{QuoteIdentifier(schema)}.{QuoteIdentifier(relation)}");
        command.Parameters.AddWithValue("expected_owner", expectedOwner);
        command.Parameters.AddWithValue("public_select_granted", publicSelectGranted);
        var result = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return result is null or DBNull ? null : (bool)result;
    }

    private static async Task AssertPermissionDeniedAsync(NpgsqlConnection connection, string sql)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);
        Assert.Equal("42501", exception.SqlState);
    }

    private static async Task CreateRoleAsync(
        NpgsqlConnectionStringBuilder connection,
        string role,
        bool login,
        string? password)
    {
        await using var database = new NpgsqlConnection(connection.ConnectionString);
        await database.OpenAsync().ConfigureAwait(false);
        var loginClause = login ? "LOGIN" : "NOLOGIN";
        var passwordClause = password is null ? string.Empty : $" PASSWORD '{password}'";
        await ExecuteOwnerAsync(
            database,
            $"CREATE ROLE {QuoteIdentifier(role)} {loginClause}{passwordClause} NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS").ConfigureAwait(false);
    }

    private static async Task ExecuteOwnerAsync(NpgsqlConnectionStringBuilder connection, string sql)
    {
        await using var database = new NpgsqlConnection(connection.ConnectionString);
        await database.OpenAsync().ConfigureAwait(false);
        await ExecuteOwnerAsync(database, sql).ConfigureAwait(false);
    }

    private static async Task ExecuteOwnerAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<PsqlResult> RunPsqlAsync(
        NpgsqlConnectionStringBuilder connection,
        string scriptPath,
        params (string Name, string Value)[] variables)
    {
        var host = connection.Host ?? throw new InvalidOperationException("The disposable PostgreSQL host is required.");
        var username = connection.Username ?? throw new InvalidOperationException("The disposable PostgreSQL user is required.");
        var database = connection.Database ?? throw new InvalidOperationException("The disposable PostgreSQL database is required.");
        var password = connection.Password ?? throw new InvalidOperationException("The disposable PostgreSQL credential is required.");

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
        foreach (var variable in variables)
        {
            startInfo.ArgumentList.Add("-v");
            startInfo.ArgumentList.Add($"{variable.Name}={variable.Value}");
        }

        startInfo.ArgumentList.Add("-h");
        startInfo.ArgumentList.Add(host);
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(connection.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-U");
        startInfo.ArgumentList.Add(username);
        startInfo.ArgumentList.Add("-d");
        startInfo.ArgumentList.Add(database);
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.Environment["PGPASSWORD"] = password;
        startInfo.Environment["PGCONNECT_TIMEOUT"] = "5";
        startInfo.Environment["PGSSLMODE"] = connection.SslMode.ToString().ToLowerInvariant();

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Could not start the local psql client for the isolated runtime-grants fixture.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        return new PsqlResult(process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }

    private static void AssertPsqlSucceeded(PsqlResult result, string message)
    {
        Assert.True(result.ExitCode == 0, $"{message} psql exit code: {result.ExitCode}; stderr: {result.StandardError}");
    }

    private static async Task CleanupOwnedArtifactsAsync(
        NpgsqlConnection ownerConnection,
        string helperRole,
        HashSet<string> createdRoles,
        Dictionary<string, (string Owner, bool PublicSelectGranted)> createdPublicRelations,
        Dictionary<string, (string Owner, bool PublicSelectGranted)> createdInternalRelations,
        Dictionary<string, (string Owner, string GrantedRole)> createdSequences,
        List<(string GrantedRole, string MemberRole)> roleMemberships)
    {
        foreach (var membership in roleMemberships)
        {
            await ExecuteOwnerAsync(ownerConnection,
                $"REVOKE {QuoteIdentifier(membership.GrantedRole)} FROM {QuoteIdentifier(membership.MemberRole)}").ConfigureAwait(false);
        }

        foreach (var (relation, expected) in createdPublicRelations)
        {
            await DropOwnedProbeTableAsync(ownerConnection, "public", relation, expected.Owner, expected.PublicSelectGranted).ConfigureAwait(false);
        }

        foreach (var (relation, expected) in createdInternalRelations)
        {
            await DropOwnedProbeTableAsync(ownerConnection, "swiftaid_internal", relation, expected.Owner, expected.PublicSelectGranted).ConfigureAwait(false);
        }

        foreach (var (sequence, expected) in createdSequences)
        {
            await DropOwnedProbeSequenceAsync(ownerConnection, sequence, expected.Owner, expected.GrantedRole).ConfigureAwait(false);
        }

        var functionOwner = await GetHelperFunctionOwnerAsync(ownerConnection).ConfigureAwait(false);
        if (functionOwner is not null)
        {
            if (!createdRoles.Contains(functionOwner, StringComparer.Ordinal) || !string.Equals(functionOwner, helperRole, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing to remove a lock-helper function not owned by this test's exact helper role.");
            }

            await ExecuteOwnerAsync(ownerConnection,
                "DROP FUNCTION swiftaid_internal.lock_case_authority(uuid, text, text[], boolean)").ConfigureAwait(false);
        }

        foreach (var relation in AllPersistenceRelations)
        {
            foreach (var role in createdRoles)
            {
                await ExecuteOwnerAsync(ownerConnection,
                    $"REVOKE ALL PRIVILEGES ON TABLE public.{QuoteIdentifier(relation)} FROM {QuoteIdentifier(role)}").ConfigureAwait(false);
            }
        }

        await RevokeRuntimeColumnGrantsAsync(ownerConnection, createdRoles).ConfigureAwait(false);
        await RevokeHelperColumnGrantsAsync(ownerConnection, helperRole).ConfigureAwait(false);

        foreach (var role in createdRoles)
        {
            await ExecuteOwnerAsync(ownerConnection,
                $"REVOKE ALL PRIVILEGES ON SCHEMA public, swiftaid_internal FROM {QuoteIdentifier(role)}").ConfigureAwait(false);
        }

        foreach (var role in createdRoles)
        {
            var ownedObjectCount = await GetOwnedObjectCountAsync(ownerConnection, role).ConfigureAwait(false);
            if (ownedObjectCount != 0)
            {
                throw new InvalidOperationException("Refusing to drop a runtime-grants test role that owns unexpected objects.");
            }

            await ExecuteOwnerAsync(ownerConnection, $"DROP ROLE IF EXISTS {QuoteIdentifier(role)}").ConfigureAwait(false);
        }
    }

    private static async Task RevokeRuntimeColumnGrantsAsync(
        NpgsqlConnection connection,
        IEnumerable<string> roles)
    {
        var grants = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["actor_access_states"] = ["access_epoch", "version", "updated_at_utc"],
            ["managed_cases"] = ["aggregate_version", "report_revision", "resource_version", "workflow", "workflow_version", "accountable_owner_actor_id", "updated_at_utc"],
            ["protected_event_payloads"] = ["payload_id", "protected_bytes", "protection_purpose_version", "created_at_utc"],
            ["case_events"] = ["event_id", "case_id", "aggregate_version", "report_revision", "command_id", "actor_id", "event_type", "schema_version", "occurred_at_utc", "recorded_at_utc", "payload_id"],
            ["command_receipts"] = ["receipt_id", "actor_id", "command_id", "case_id", "command_type", "schema_version", "canonical_payload_hash", "first_aggregate_version", "last_aggregate_version", "report_revision", "accepted_at_utc"],
            ["domain_outbox"] = ["outbox_id", "event_id", "message_type", "created_at_utc", "available_at_utc", "attempt_count", "lease_until_utc", "published_at_utc"],
            ["security_audit_entries"] = ["audit_id", "actor_id", "target_actor_id", "resource_id", "operation", "decision_code", "access_epoch", "recorded_at_utc", "reason_code"]
        };

        foreach (var (relation, columns) in grants)
        {
            var columnList = string.Join(", ", columns.Select(QuoteIdentifier));
            foreach (var role in roles)
            {
                await ExecuteOwnerAsync(connection,
                    $"REVOKE ALL PRIVILEGES ({columnList}) ON TABLE public.{QuoteIdentifier(relation)} FROM {QuoteIdentifier(role)}").ConfigureAwait(false);
            }
        }
    }

    private static async Task RevokeHelperColumnGrantsAsync(NpgsqlConnection connection, string helperRole)
    {
        var grants = new Dictionary<string, string[]>(StringComparer.Ordinal)
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

        foreach (var (relation, columns) in grants)
        {
            var columnList = string.Join(", ", columns.Select(QuoteIdentifier));
            await ExecuteOwnerAsync(connection,
                $"REVOKE ALL PRIVILEGES ({columnList}) ON TABLE public.{QuoteIdentifier(relation)} FROM {QuoteIdentifier(helperRole)}").ConfigureAwait(false);
        }
    }

    private static async Task<long> GetOwnedObjectCountAsync(NpgsqlConnection connection, string role)
    {
        const string sql = """
            SELECT count(*)
              FROM pg_catalog.pg_shdepend AS dependency
             WHERE dependency.refclassid = 'pg_catalog.pg_authid'::regclass
               AND dependency.refobjid = (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = @role)
               AND dependency.deptype = 'o'
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("role", role);
        return (long)(await command.ExecuteScalarAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("The task-role ownership query returned no value."));
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

        throw new FileNotFoundException("The repository PostgreSQL script was not found from the test output directory.", relativePath);
    }

    private static string QuoteIdentifier(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private sealed record PsqlResult(int ExitCode, string StandardOutput, string StandardError);
}
