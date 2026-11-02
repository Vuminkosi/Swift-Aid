using System.Security.Claims;
using System.Text.Json;
using SwiftAid.Application.Authorization;
using SwiftAid.Contracts.Commands;
using SwiftAid.Contracts.Sync;

namespace SwiftAid.Application.Tests;

public sealed class AuthorizationPolicyTests
{
    [Fact]
    public async Task Bounded_share_does_not_inherit_broader_operations_or_combine_scopes()
    {
        var actor = CreateActor("worker-opaque-1");
        var allowedActions = new List<CaseOperation> { CaseOperation.ReadCase };
        var limits = new Dictionary<Guid, IEnumerable<CaseOperation>> { [CaseId] = allowedActions };
        var group = CreateScopedGrant("bounded", sharedCaseIds: [CaseId],
            operations: [CaseOperation.ReadCase, CaseOperation.ReadAttachment, CaseOperation.ManageFollowUp],
            sharedCaseOperations: limits);
        allowedActions.Add(CaseOperation.ReadAttachment);
        limits.Clear();
        var grant = CreateGrant(actor.ActorId, grants: [group]);
        var policy = CreatePolicy(new TestGrantStore(_ => grant));
        Assert.True((await policy.AuthorizeAsync(actor, CaseId, CaseOperation.ReadCase, CancellationToken.None)).IsAllowed);
        Assert.False((await policy.AuthorizeAsync(actor, CaseId, CaseOperation.ReadAttachment, CancellationToken.None)).IsAllowed);
        Assert.False((await policy.AuthorizeAllAsync(actor, CaseId,
            [CaseOperation.ReadCase, CaseOperation.ManageFollowUp], CancellationToken.None)).IsAllowed);

        var scope = await new ScopeService(new TestGrantStore(_ => grant), new FixedTimeProvider(Now))
            .GetAsync(actor, CancellationToken.None);
        var shared = Assert.Single(scope.Grants, item => item.CaseIds.Contains(CaseId));
        Assert.Equal([nameof(CaseOperation.ReadCase)], shared.Operations);
        Assert.False(shared.CanAccessJurisdictionalCases);
        Assert.DoesNotContain(scope.Grants, item => item.CaseIds.Contains(CaseId) && item.Operations.Contains(nameof(CaseOperation.ReadAttachment)));
    }

    [Fact]
    public async Task Empty_share_actions_deny_but_independent_assignment_retains_its_permissions()
    {
        var actor = CreateActor("worker-opaque-1");
        var limits = new Dictionary<Guid, IEnumerable<CaseOperation>> { [CaseId] = [] };
        foreach (var assigned in new[] { false, true })
        {
            var group = CreateScopedGrant("bounded", sharedCaseIds: [CaseId], assignedCaseIds: assigned ? [CaseId] : [],
                sharedCaseOperations: limits);
            var policy = CreatePolicy(new TestGrantStore(_ => CreateGrant(actor.ActorId, grants: [group])));
            Assert.Equal(assigned, (await policy.AuthorizeAsync(actor, CaseId, CaseOperation.ReadCase, CancellationToken.None)).IsAllowed);
        }
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid CaseId = Guid.Parse("d69f2928-f7c5-4c8d-9f15-b9cc8e54e034");

    [Fact]
    public async Task Assigned_case_is_allowed_only_for_a_current_grant_and_requested_operation()
    {
        var actor = CreateActor("worker-opaque-1");
        var store = new TestGrantStore(_ => CreateGrant(
            actor.ActorId,
            assignedCaseIds: [CaseId],
            operations: [CaseOperation.ReadCase]));
        var descriptorStore = CreateResourceStore();
        var policy = CreatePolicy(store, descriptorStore);

        var decision = await policy.AuthorizeAsync(actor, CaseId, CaseOperation.ReadCase, CancellationToken.None);
        var deniedOperation = await policy.AuthorizeAsync(actor, CaseId, CaseOperation.Export, CancellationToken.None);

        Assert.True(decision.IsAllowed);
        Assert.Equal(AccessDecisionCode.Allowed, decision.Code);
        Assert.Null(decision.AuditRecord);
        Assert.Equal(AccessDecisionCode.OperationNotGranted, deniedOperation.Code);
        Assert.NotNull(deniedOperation.AuditRecord);
        Assert.Equal(2, store.LookupCount);
        Assert.Equal(2, descriptorStore.LookupCount);
    }

    [Fact]
    public async Task Separate_grants_cannot_combine_organization_workspace_or_operation()
    {
        var actor = CreateActor("worker-opaque-1");
        var organizationGroup = CreateScopedGrant(
            "group-a", organizationIds: ["org-a"], workspaceIds: ["workspace-other"],
            operations: [CaseOperation.ReadCase]);
        var workspaceGroup = CreateScopedGrant(
            "group-b", organizationIds: ["org-other"], workspaceIds: ["workspace-a"],
            operations: [CaseOperation.Export]);
        var grant = CreateGrant(actor.ActorId, grants: [organizationGroup, workspaceGroup]);
        var policy = CreatePolicy(new TestGrantStore(_ => grant));

        var decision = await policy.AuthorizeAsync(actor, CaseId, CaseOperation.ReadCase, CancellationToken.None);

        Assert.False(decision.IsAllowed);
        Assert.Equal(AccessDecisionCode.WorkspaceOutsideGrant, decision.Code);
    }

    [Fact]
    public async Task Multi_operation_authorization_requires_every_operation_in_one_matching_group()
    {
        var actor = CreateActor("worker-opaque-1");
        var splitGrant = CreateGrant(actor.ActorId, grants:
        [
            CreateScopedGrant("group-a", assignedCaseIds: [CaseId], operations: [CaseOperation.ReadCase]),
            CreateScopedGrant("group-b", assignedCaseIds: [CaseId], operations: [CaseOperation.Export], organizationIds: ["org-other"])
        ]);
        var policy = CreatePolicy(new TestGrantStore(_ => splitGrant));

        var denied = await policy.AuthorizeAllAsync(
            actor, CaseId, [CaseOperation.ReadCase, CaseOperation.Export], CancellationToken.None);
        var validGroup = CreateScopedGrant(
            "group-valid", assignedCaseIds: [CaseId], operations: [CaseOperation.ReadCase, CaseOperation.Export]);
        var validAlongsideUnrelated = CreateScopedGrant(
            "group-unrelated", assignedCaseIds: [CaseId], operations: [CaseOperation.ExecuteProtectedAction], organizationIds: ["org-other"]);
        var validPolicy = CreatePolicy(new TestGrantStore(_ => CreateGrant(actor.ActorId, grants: [validAlongsideUnrelated, validGroup])));

        var allowed = await validPolicy.AuthorizeAllAsync(
            actor, CaseId, [CaseOperation.ReadCase, CaseOperation.Export], CancellationToken.None);

        Assert.False(denied.IsAllowed);
        Assert.Equal(AccessDecisionCode.OperationNotGranted, denied.Code);
        Assert.Null(denied.MatchedGrantGroupId);
        Assert.True(allowed.IsAllowed);
        Assert.Equal("group-valid", allowed.MatchedGrantGroupId);
    }

    [Fact]
    public async Task Empty_or_undefined_multi_operation_sets_fail_before_store_access()
    {
        var actor = CreateActor("worker-opaque-1");
        var grantStore = new TestGrantStore(_ => CreateGrant(actor.ActorId, assignedCaseIds: [CaseId]));
        var descriptorStore = CreateResourceStore();
        var policy = CreatePolicy(grantStore, descriptorStore);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await policy.AuthorizeAllAsync(actor, CaseId, [], CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await policy.AuthorizeAllAsync(actor, CaseId, [(CaseOperation)999], CancellationToken.None));

        Assert.Equal(0, grantStore.LookupCount);
        Assert.Equal(0, descriptorStore.LookupCount);
    }

    [Fact]
    public async Task Single_operation_invalid_enum_remains_audited_denial_without_store_access()
    {
        var actor = CreateActor("worker-opaque-1");
        var grantStore = new TestGrantStore(_ => CreateGrant(actor.ActorId, assignedCaseIds: [CaseId]));
        var descriptorStore = CreateResourceStore();
        var policy = CreatePolicy(grantStore, descriptorStore);

        var decision = await policy.AuthorizeAsync(actor, CaseId, (CaseOperation)999, CancellationToken.None);

        Assert.False(decision.IsAllowed);
        Assert.Equal(AccessDecisionCode.OperationNotGranted, decision.Code);
        Assert.NotNull(decision.AuditRecord);
        Assert.Equal(0, grantStore.LookupCount);
        Assert.Equal(0, descriptorStore.LookupCount);
    }

    [Fact]
    public void Case_operation_values_preserve_existing_ordinals_and_append_narrow_mutation_operations()
    {
        Assert.Equal(0, (int)CaseOperation.ReadCase);
        Assert.Equal(1, (int)CaseOperation.ReadAttachment);
        Assert.Equal(2, (int)CaseOperation.Synchronize);
        Assert.Equal(3, (int)CaseOperation.Search);
        Assert.Equal(4, (int)CaseOperation.Export);
        Assert.Equal(5, (int)CaseOperation.Count);
        Assert.Equal(6, (int)CaseOperation.RequestProtectedAction);
        Assert.Equal(7, (int)CaseOperation.ExecuteProtectedAction);
        Assert.Equal(8, (int)CaseOperation.ReadLinkedSignalEvidence);
        Assert.Equal(9, (int)CaseOperation.CorrectReport);
        Assert.Equal(10, (int)CaseOperation.ReviewProtectedAction);
        Assert.Equal(11, (int)CaseOperation.AssignCase);
        Assert.Equal(12, (int)CaseOperation.ResolveCase);
        Assert.Equal(13, (int)CaseOperation.ReopenCase);
    }

    [Fact]
    public async Task Operation_permission_cannot_be_borrowed_from_another_scope_group()
    {
        var actor = CreateActor("worker-opaque-1");
        var scopeOnly = CreateScopedGrant("group-a", operations: [CaseOperation.Export]);
        var operationOnly = CreateScopedGrant(
            "group-b", organizationIds: ["org-other"], operations: [CaseOperation.ReadCase]);
        var policy = CreatePolicy(new TestGrantStore(_ => CreateGrant(actor.ActorId, grants: [scopeOnly, operationOnly])));

        var decision = await policy.AuthorizeAsync(actor, CaseId, CaseOperation.ReadCase, CancellationToken.None);

        Assert.False(decision.IsAllowed);
        Assert.Equal(AccessDecisionCode.OperationNotGranted, decision.Code);
    }

    [Fact]
    public async Task Region_sets_are_not_combined_across_groups()
    {
        var actor = CreateActor("worker-opaque-1");
        var firstRegion = CreateScopedGrant(
            "group-a", canAccessJurisdictionalCases: true, regionIds: ["ZW-MW"]);
        var secondRegion = CreateScopedGrant(
            "group-b", canAccessJurisdictionalCases: true, regionIds: ["ZW-MA"]);
        var descriptorStore = CreateResourceStore(CreateDescriptor(regionIds: ["ZW-MW", "ZW-MA"]));
        var policy = CreatePolicy(
            new TestGrantStore(_ => CreateGrant(actor.ActorId, grants: [firstRegion, secondRegion])), descriptorStore);

        var decision = await policy.AuthorizeAsync(actor, CaseId, CaseOperation.ReadCase, CancellationToken.None);

        Assert.False(decision.IsAllowed);
        Assert.Equal(AccessDecisionCode.RegionOutsideGrant, decision.Code);
    }

    [Fact]
    public async Task Assignment_cannot_borrow_resource_dimensions_from_another_group()
    {
        var actor = CreateActor("worker-opaque-1");
        var assignmentOnly = CreateScopedGrant(
            "group-a", assignedCaseIds: [CaseId], organizationIds: ["org-other"]);
        var dimensionsOnly = CreateScopedGrant("group-b", canAccessJurisdictionalCases: false);
        var policy = CreatePolicy(new TestGrantStore(_ => CreateGrant(actor.ActorId, grants: [assignmentOnly, dimensionsOnly])));

        var decision = await policy.AuthorizeAsync(actor, CaseId, CaseOperation.ReadCase, CancellationToken.None);

        Assert.False(decision.IsAllowed);
        Assert.Equal(AccessDecisionCode.CaseNotAssignedOrShared, decision.Code);
    }

    [Fact]
    public async Task Valid_group_allows_access_even_when_an_unrelated_group_is_empty()
    {
        var actor = CreateActor("worker-opaque-1");
        var valid = CreateScopedGrant("group-valid", assignedCaseIds: [CaseId]);
        var empty = CreateScopedGrant(
            "group-empty", organizationIds: [], workspaceIds: [], countryCodes: [], regionIds: [],
            sectors: [], sensitivityCodes: [], operations: []);
        var policy = CreatePolicy(new TestGrantStore(_ => CreateGrant(actor.ActorId, grants: [empty, valid])));

        var decision = await policy.AuthorizeAsync(actor, CaseId, CaseOperation.ReadCase, CancellationToken.None);

        Assert.True(decision.IsAllowed);
    }

    [Fact]
    public async Task Empty_or_inactive_grants_deny_by_default()
    {
        var actor = CreateActor("worker-opaque-1");
        var noGroups = CreatePolicy(new TestGrantStore(_ => CreateGrant(actor.ActorId, grants: [])));
        var inactive = CreatePolicy(new TestGrantStore(_ => CreateGrant(
            actor.ActorId, status: AccessGrantStatus.Suspended, grants: [CreateScopedGrant("group-suspended")])));

        var emptyDecision = await noGroups.AuthorizeAsync(actor, CaseId, CaseOperation.ReadCase, CancellationToken.None);
        var inactiveDecision = await inactive.AuthorizeAsync(actor, CaseId, CaseOperation.ReadCase, CancellationToken.None);

        Assert.Equal(AccessDecisionCode.GrantMissing, emptyDecision.Code);
        Assert.Equal(AccessDecisionCode.GrantMissing, inactiveDecision.Code);
    }

    [Fact]
    public async Task Worker_without_assignment_or_explicit_share_is_denied_even_inside_a_matching_region()
    {
        var actor = CreateActor("worker-opaque-1");
        var grant = CreateGrant(actor.ActorId, canAccessJurisdictionalCases: false);
        var policy = CreatePolicy(new TestGrantStore(_ => grant));

        var decision = await policy.AuthorizeAsync(actor, CaseId, CaseOperation.ReadCase, CancellationToken.None);

        Assert.Equal(AccessDecisionCode.CaseNotAssignedOrShared, decision.Code);
        Assert.NotNull(decision.AuditRecord);
    }

    [Fact]
    public async Task Explicitly_shared_case_is_allowed_without_jurisdiction_wide_access()
    {
        var actor = CreateActor("worker-opaque-2");
        var policy = CreatePolicy(new TestGrantStore(_ => CreateGrant(
            actor.ActorId,
            sharedCaseIds: [CaseId],
            canAccessJurisdictionalCases: false)));

        var decision = await policy.AuthorizeAsync(actor, CaseId, CaseOperation.ReadCase, CancellationToken.None);

        Assert.True(decision.IsAllowed);
    }

    [Fact]
    public async Task Jurisdictional_access_requires_matching_country_region_sector_and_sensitivity()
    {
        var actor = CreateActor("officer-opaque-1");
        var grant = CreateGrant(
            actor.ActorId,
            canAccessJurisdictionalCases: true,
            countryCodes: ["ZW"],
            regionIds: ["ZW-MW"],
            sectors: [Sector.AnimalHealth],
            sensitivityCodes: ["STANDARD"],
            operations: [CaseOperation.ReadCase]);
        var grantStore = new TestGrantStore(_ => grant);
        var trustedDescriptor = CreateDescriptor();
        var policy = CreatePolicy(grantStore, new TestResourceStore(_ => trustedDescriptor));

        var allowed = await policy.AuthorizeAsync(actor, CaseId, CaseOperation.ReadCase, CancellationToken.None);
        trustedDescriptor = CreateDescriptor(countryCode: "SZ");
        var outsideCountry = await policy.AuthorizeAsync(actor, CaseId, CaseOperation.ReadCase, CancellationToken.None);
        trustedDescriptor = CreateDescriptor(regionIds: ["ZW-MA"]);
        var outsideRegion = await policy.AuthorizeAsync(actor, CaseId, CaseOperation.ReadCase, CancellationToken.None);
        trustedDescriptor = CreateDescriptor(sector: Sector.HumanHealth);
        var wrongSector = await policy.AuthorizeAsync(actor, CaseId, CaseOperation.ReadCase, CancellationToken.None);
        trustedDescriptor = CreateDescriptor(sensitivityCode: "HUM_HIV");
        var restricted = await policy.AuthorizeAsync(actor, CaseId, CaseOperation.ReadCase, CancellationToken.None);
        trustedDescriptor = CreateDescriptor(regionIds: ["ZW-MW", "ZW-MA"]);
        var partialRegionCoverage = await policy.AuthorizeAsync(actor, CaseId, CaseOperation.ReadCase, CancellationToken.None);

        Assert.True(allowed.IsAllowed);
        Assert.Equal(AccessDecisionCode.CountryOutsideGrant, outsideCountry.Code);
        Assert.Equal(AccessDecisionCode.RegionOutsideGrant, outsideRegion.Code);
        Assert.Equal(AccessDecisionCode.SectorOutsideGrant, wrongSector.Code);
        Assert.Equal(AccessDecisionCode.SensitivityOutsideGrant, restricted.Code);
        Assert.Equal(AccessDecisionCode.RegionOutsideGrant, partialRegionCoverage.Code);
    }

    [Fact]
    public async Task Organization_workspace_and_grant_identity_must_match()
    {
        var actor = CreateActor("worker-opaque-1");
        var trustedDescriptor = CreateDescriptor(organizationId: "org-other");
        var grantStore = new TestGrantStore(_ => CreateGrant(actor.ActorId, assignedCaseIds: [CaseId]));
        var policy = CreatePolicy(grantStore, new TestResourceStore(_ => trustedDescriptor));

        var wrongOrganization = await policy.AuthorizeAsync(actor, CaseId, CaseOperation.ReadCase, CancellationToken.None);
        trustedDescriptor = CreateDescriptor(workspaceId: "workspace-other");
        var wrongWorkspace = await policy.AuthorizeAsync(actor, CaseId, CaseOperation.ReadCase, CancellationToken.None);

        var mismatchedGrantPolicy = CreatePolicy(new TestGrantStore(_ => CreateGrant("actor-other", assignedCaseIds: [CaseId])));
        var mismatchedActor = await mismatchedGrantPolicy.AuthorizeAsync(actor, CaseId, CaseOperation.ReadCase, CancellationToken.None);

        Assert.Equal(AccessDecisionCode.OrganizationOutsideGrant, wrongOrganization.Code);
        Assert.Equal(AccessDecisionCode.WorkspaceOutsideGrant, wrongWorkspace.Code);
        Assert.Equal(AccessDecisionCode.GrantActorMismatch, mismatchedActor.Code);
    }

    [Fact]
    public async Task Policy_uses_only_descriptor_resolved_from_authoritative_resource_store()
    {
        var actor = CreateActor("worker-opaque-1");
        var grantStore = new TestGrantStore(_ => CreateGrant(actor.ActorId, assignedCaseIds: [CaseId]));
        var descriptorStore = CreateResourceStore(CreateDescriptor(organizationId: "org-other"));
        var policy = CreatePolicy(grantStore, descriptorStore);

        var decision = await policy.AuthorizeAsync(actor, CaseId, CaseOperation.ReadCase, CancellationToken.None);

        Assert.Equal(AccessDecisionCode.OrganizationOutsideGrant, decision.Code);
        Assert.Equal(1, descriptorStore.LookupCount);
    }

    [Fact]
    public async Task Descriptor_for_a_different_case_is_denied_with_an_opaque_audit()
    {
        var actor = CreateActor("worker-opaque-1");
        var otherCaseId = Guid.Parse("ba738fc4-1ca7-4e61-8d37-4dc2e3148b02");
        var grantStore = new TestGrantStore(_ => CreateGrant(actor.ActorId, assignedCaseIds: [CaseId]));
        var descriptorStore = CreateResourceStore(
            CreateDescriptorForCase(otherCaseId, sensitivityCode: "HUM_HIV"));
        var policy = CreatePolicy(grantStore, descriptorStore);

        var decision = await policy.AuthorizeAsync(actor, CaseId, CaseOperation.ReadCase, CancellationToken.None);

        Assert.Equal(AccessDecisionCode.ResourceIdMismatch, decision.Code);
        Assert.NotNull(decision.AuditRecord);
        Assert.Equal(CaseId, decision.AuditRecord!.ResourceId);
        Assert.Null(decision.AuditRecord.ResourceVersion);
        var auditJson = JsonSerializer.Serialize(decision.AuditRecord);
        Assert.DoesNotContain(otherCaseId.ToString(), auditJson, StringComparison.Ordinal);
        Assert.DoesNotContain("HUM_HIV", auditJson, StringComparison.Ordinal);
        Assert.DoesNotContain("org-a", auditJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_or_unavailable_authoritative_resource_descriptor_fails_closed()
    {
        var actor = CreateActor("worker-opaque-1");
        var grantStore = new TestGrantStore(_ => CreateGrant(actor.ActorId, assignedCaseIds: [CaseId]));
        var missingPolicy = CreatePolicy(grantStore, new TestResourceStore(_ => null));
        var unavailablePolicy = CreatePolicy(
            grantStore,
            new TestResourceStore(_ => throw new InvalidOperationException("private resource detail")));

        var missing = await missingPolicy.AuthorizeAsync(actor, CaseId, CaseOperation.ReadCase, CancellationToken.None);
        var unavailable = await unavailablePolicy.AuthorizeAsync(actor, CaseId, CaseOperation.ReadCase, CancellationToken.None);

        Assert.Equal(AccessDecisionCode.ResourceMissing, missing.Code);
        Assert.Equal(AccessDecisionCode.ResourceSourceUnavailable, unavailable.Code);
        Assert.Null(missing.AuditRecord!.ResourceVersion);
        Assert.Null(unavailable.AuditRecord!.ResourceVersion);
        Assert.DoesNotContain("private resource detail", unavailable.AuditRecord.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_unavailable_expired_or_stale_grants_fail_closed()
    {
        var actor = CreateActor("worker-opaque-1");
        var missingPolicy = CreatePolicy(new TestGrantStore(_ => null));
        var unavailablePolicy = CreatePolicy(new TestGrantStore(_ => throw new InvalidOperationException("private store detail")));
        var expiredPolicy = CreatePolicy(new TestGrantStore(_ => CreateGrant(actor.ActorId, expiresAtUtc: Now)));
        var stalePolicy = CreatePolicy(new TestGrantStore(_ => CreateGrant(actor.ActorId, accessEpoch: actor.AccessEpoch + 1)));

        var missing = await missingPolicy.AuthorizeAsync(actor, CaseId, CaseOperation.ReadCase, CancellationToken.None);
        var unavailable = await unavailablePolicy.AuthorizeAsync(actor, CaseId, CaseOperation.ReadCase, CancellationToken.None);
        var expired = await expiredPolicy.AuthorizeAsync(actor, CaseId, CaseOperation.ReadCase, CancellationToken.None);
        var stale = await stalePolicy.AuthorizeAsync(actor, CaseId, CaseOperation.ReadCase, CancellationToken.None);

        Assert.Equal(AccessDecisionCode.GrantMissing, missing.Code);
        Assert.Equal(AccessDecisionCode.GrantSourceUnavailable, unavailable.Code);
        Assert.Equal(AccessDecisionCode.GrantExpired, expired.Code);
        Assert.Equal(AccessDecisionCode.AccessEpochMismatch, stale.Code);
        Assert.All([missing, unavailable, expired, stale], decision => Assert.NotNull(decision.AuditRecord));
    }

    [Fact]
    public async Task Grant_expiring_during_descriptor_lookup_is_rechecked_before_authorization()
    {
        var actor = CreateActor("worker-opaque-1");
        var timeProvider = new AdvanceableTimeProvider(Now);
        var grant = CreateGrant(
            actor.ActorId,
            expiresAtUtc: Now.AddSeconds(1),
            grants: [CreateScopedGrant(
                "longer-lived-group",
                expiresAtUtc: Now.AddHours(1),
                assignedCaseIds: [CaseId])]);
        var policy = new CaseAccessPolicy(
            new TestGrantStore(_ => grant),
            new AdvancingResourceStore(timeProvider, CreateDescriptor(), TimeSpan.FromSeconds(1)),
            timeProvider);

        var decision = await policy.AuthorizeAsync(actor, CaseId, CaseOperation.ReadCase, CancellationToken.None);

        Assert.False(decision.IsAllowed);
        Assert.Equal(AccessDecisionCode.GrantExpired, decision.Code);
        Assert.Equal(CaseId, decision.AuditRecord!.ResourceId);
        Assert.Equal(12, decision.AuditRecord.ResourceVersion);
        Assert.Equal(Now.AddSeconds(1), decision.AuditRecord.RecordedAtUtc);
    }

    [Fact]
    public async Task Each_sensitive_decision_rechecks_current_grant_and_records_minimized_denial_audit()
    {
        var actor = CreateActor("worker-opaque-1");
        var currentGrant = CreateGrant(actor.ActorId, assignedCaseIds: [CaseId]);
        var store = new TestGrantStore(_ => currentGrant);
        var policy = CreatePolicy(store);

        var first = await policy.AuthorizeAsync(actor, CaseId, CaseOperation.ReadCase, CancellationToken.None);
        currentGrant = null;
        var second = await policy.AuthorizeAsync(actor, CaseId, CaseOperation.ReadCase, CancellationToken.None);

        Assert.True(first.IsAllowed);
        Assert.Equal(AccessDecisionCode.GrantMissing, second.Code);
        Assert.Equal(2, store.LookupCount);
        Assert.NotNull(second.AuditRecord);
        Assert.Equal(CaseId, second.AuditRecord!.ResourceId);
        Assert.Equal("worker-opaque-1", second.AuditRecord.ActorId);
        Assert.DoesNotContain("HUM_HIV", JsonSerializer.Serialize(second.AuditRecord), StringComparison.Ordinal);
        Assert.DoesNotContain("contact", JsonSerializer.Serialize(second.AuditRecord), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", JsonSerializer.Serialize(second.AuditRecord), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            new[] { "AccessEpoch", "ActorId", "DecisionCode", "Operation", "RecordedAtUtc", "ResourceId", "ResourceVersion" },
            typeof(AccessAuditRecord).GetProperties().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Actor_context_requires_an_authenticated_server_principal_and_does_not_use_role_claims()
    {
        var unauthenticated = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "worker-opaque-1")]));
        var authenticated = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "worker-opaque-1"),
            new Claim(ActorContext.AccessEpochClaimType, "7"),
            new Claim(ClaimTypes.Role, "HealthOfficer")
        ], authenticationType: "validated-test"));

        Assert.Throws<UnauthorizedAccessException>(() => ActorContext.FromValidatedPrincipal(unauthenticated));
        var actor = ActorContext.FromValidatedPrincipal(authenticated);

        Assert.Equal("worker-opaque-1", actor.ActorId);
        Assert.Equal(7, actor.AccessEpoch);

        var policy = CreatePolicy(new TestGrantStore(_ => CreateGrant(actor.ActorId)));
        var decision = await policy.AuthorizeAsync(actor, CaseId, CaseOperation.ReadCase, CancellationToken.None);
        Assert.Equal(AccessDecisionCode.CaseNotAssignedOrShared, decision.Code);
    }

    [Fact]
    public async Task Scope_manifest_is_minimized_sorted_and_retrieved_from_current_grant()
    {
        var actor = CreateActor("officer-opaque-1");
        var store = new TestGrantStore(_ => CreateGrant(
            actor.ActorId,
            assignedCaseIds: [CaseId, Guid.Parse("52a742c1-caaa-4626-9eca-2e2312d3a34b")],
            sharedCaseIds: [Guid.Parse("d69f2928-f7c5-4c8d-9f15-b9cc8e54e035")],
            workspaceIds: ["workspace-z", "workspace-a", "workspace-a"],
            organizationIds: ["org-z", "org-a"],
            countryCodes: ["ZW", "SZ"],
            regionIds: ["ZW-MW", "ZW-MA"],
            sectors: [Sector.AnimalHealth],
            sensitivityCodes: ["STANDARD"],
            operations: [CaseOperation.ReadCase]));
        var service = new ScopeService(store, new FixedTimeProvider(Now));

        var manifest = await service.GetAsync(actor, CancellationToken.None);

        Assert.Equal(ScopeManifest.CurrentSchemaVersion, manifest.SchemaVersion);
        Assert.Single(manifest.Grants);
        Assert.Equal(new[] { "org-a", "org-z" }, manifest.Grants[0].OrganizationIds);
        Assert.Equal(new[] { "workspace-a", "workspace-z" }, manifest.Grants[0].WorkspaceIds);
        Assert.Equal(new[] { "SZ", "ZW" }, manifest.Grants[0].CountryCodes);
        Assert.Equal(new[] { "ZW-MA", "ZW-MW" }, manifest.Grants[0].RegionIds);
        Assert.Equal(3, manifest.Grants[0].CaseIds.Count);
        Assert.Equal(7, manifest.AccessEpoch);
        Assert.Equal(Now.AddHours(1), manifest.ExpiresAtUtc);
        Assert.Contains("STANDARD", JsonSerializer.Serialize(manifest), StringComparison.Ordinal);
        Assert.DoesNotContain("HealthOfficer", JsonSerializer.Serialize(manifest), StringComparison.Ordinal);
        Assert.Equal(1, store.LookupCount);
    }

    [Fact]
    public async Task Scope_manifest_serialization_preserves_group_boundaries_without_flattened_fields()
    {
        var actor = CreateActor("officer-opaque-1");
        var groups = new[]
        {
            CreateScopedGrant(
                "scope-a", organizationIds: ["org-a"], workspaceIds: ["workspace-a"],
                sensitivityCodes: ["sens-A", "SENS-A"]),
            CreateScopedGrant(
                "scope-b", organizationIds: ["org-b"], workspaceIds: ["workspace-b"], sensitivityCodes: ["sens-b"])
        };
        var service = new ScopeService(
            new TestGrantStore(_ => CreateGrant(actor.ActorId, grants: groups)), new FixedTimeProvider(Now));

        var manifest = await service.GetAsync(actor, CancellationToken.None);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(manifest));
        var root = json.RootElement;

        Assert.Equal(2, root.GetProperty("SchemaVersion").GetInt32());
        Assert.False(root.TryGetProperty("OrganizationIds", out _));
        var entries = root.GetProperty("Grants").EnumerateArray().ToArray();
        Assert.Equal(2, entries.Length);
        Assert.Equal("scope-a", entries[0].GetProperty("GroupId").GetString());
        Assert.Equal(new[] { "org-a" }, entries[0].GetProperty("OrganizationIds").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(new[] { "workspace-a" }, entries[0].GetProperty("WorkspaceIds").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(new[] { "SENS-A", "sens-A" }, entries[0].GetProperty("SensitivityCodes").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("scope-b", entries[1].GetProperty("GroupId").GetString());
        Assert.Equal(new[] { "org-b" }, entries[1].GetProperty("OrganizationIds").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(new[] { "workspace-b" }, entries[1].GetProperty("WorkspaceIds").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(new[] { "sens-b" }, entries[1].GetProperty("SensitivityCodes").EnumerateArray().Select(value => value.GetString()));
        Assert.DoesNotContain("SENS-B", entries[0].GetProperty("SensitivityCodes").EnumerateArray().Select(value => value.GetString()));
        Assert.DoesNotContain("SENS-A", entries[1].GetProperty("SensitivityCodes").EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public async Task Scope_manifest_fails_closed_when_current_grant_is_unavailable()
    {
        var actor = CreateActor("officer-opaque-1");
        var service = new ScopeService(
            new TestGrantStore(_ => throw new InvalidOperationException("private store detail")),
            new FixedTimeProvider(Now));

        var exception = await Assert.ThrowsAsync<ScopeUnavailableException>(
            async () => await service.GetAsync(actor, CancellationToken.None));

        Assert.Equal(ScopeFailureCode.GrantSourceUnavailable, exception.Code);
        Assert.DoesNotContain("private store detail", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scope_manifest_rejects_stale_epoch_and_expired_grants()
    {
        var actor = CreateActor("officer-opaque-1");
        var staleService = new ScopeService(
            new TestGrantStore(_ => CreateGrant(actor.ActorId, accessEpoch: actor.AccessEpoch + 1)),
            new FixedTimeProvider(Now));
        var expiredService = new ScopeService(
            new TestGrantStore(_ => CreateGrant(actor.ActorId, expiresAtUtc: Now)),
            new FixedTimeProvider(Now));

        var stale = await Assert.ThrowsAsync<ScopeUnavailableException>(
            async () => await staleService.GetAsync(actor, CancellationToken.None));
        var expired = await Assert.ThrowsAsync<ScopeUnavailableException>(
            async () => await expiredService.GetAsync(actor, CancellationToken.None));

        Assert.Equal(ScopeFailureCode.AccessEpochMismatch, stale.Code);
        Assert.Equal(ScopeFailureCode.GrantExpired, expired.Code);
    }

    private static ICaseAccessPolicy CreatePolicy(TestGrantStore store, TestResourceStore? descriptorStore = null) =>
        new CaseAccessPolicy(store, descriptorStore ?? CreateResourceStore(), new FixedTimeProvider(Now));

    private static TestResourceStore CreateResourceStore(CaseAccessDescriptor? descriptor = null) =>
        new(_ => descriptor ?? CreateDescriptor());

    private static ActorContext CreateActor(string actorId, long epoch = 7) =>
        ActorContext.FromValidatedPrincipal(new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", actorId),
            new Claim(ActorContext.AccessEpochClaimType, epoch.ToString(System.Globalization.CultureInfo.InvariantCulture))
        ], authenticationType: "validated-test")));

    private static CaseAccessDescriptor CreateDescriptor(
        string organizationId = "org-a",
        string workspaceId = "workspace-a",
        string countryCode = "ZW",
        IEnumerable<string>? regionIds = null,
        Sector sector = Sector.AnimalHealth,
        string sensitivityCode = "STANDARD") => new(
        CaseId,
        12,
        organizationId,
        workspaceId,
        countryCode,
        regionIds ?? ["ZW-MW"],
        sector,
        sensitivityCode);

    private static CaseAccessDescriptor CreateDescriptorForCase(
        Guid caseId,
        string organizationId = "org-a",
        string workspaceId = "workspace-a",
        string countryCode = "ZW",
        IEnumerable<string>? regionIds = null,
        Sector sector = Sector.AnimalHealth,
        string sensitivityCode = "STANDARD") => new(
        caseId,
        12,
        organizationId,
        workspaceId,
        countryCode,
        regionIds ?? ["ZW-MW"],
        sector,
        sensitivityCode);

    private static CurrentAccessGrant CreateGrant(
        string actorId,
        long accessEpoch = 7,
        DateTimeOffset? expiresAtUtc = null,
        AccessGrantStatus status = AccessGrantStatus.Active,
        IEnumerable<Guid>? assignedCaseIds = null,
        IEnumerable<Guid>? sharedCaseIds = null,
        bool canAccessJurisdictionalCases = false,
        IEnumerable<string>? organizationIds = null,
        IEnumerable<string>? workspaceIds = null,
        IEnumerable<string>? countryCodes = null,
        IEnumerable<string>? regionIds = null,
        IEnumerable<Sector>? sectors = null,
        IEnumerable<string>? sensitivityCodes = null,
        IEnumerable<CaseOperation>? operations = null,
        IEnumerable<ScopedAccessGrant>? grants = null)
    {
        var expires = expiresAtUtc ?? Now.AddHours(1);
        var scopeGroups = grants ??
        [
            CreateScopedGrant(
                "default-group",
                expiresAtUtc: expires,
                assignedCaseIds: assignedCaseIds,
                sharedCaseIds: sharedCaseIds,
                canAccessJurisdictionalCases: canAccessJurisdictionalCases,
                organizationIds: organizationIds,
                workspaceIds: workspaceIds,
                countryCodes: countryCodes,
                regionIds: regionIds,
                sectors: sectors,
                sensitivityCodes: sensitivityCodes,
                operations: operations)
        ];

        return new CurrentAccessGrant(actorId, accessEpoch, expires, status, scopeGroups);
    }

    private static ScopedAccessGrant CreateScopedGrant(
        string groupId,
        DateTimeOffset? validFromUtc = null,
        DateTimeOffset? expiresAtUtc = null,
        IEnumerable<Guid>? assignedCaseIds = null,
        IEnumerable<Guid>? sharedCaseIds = null,
        bool canAccessJurisdictionalCases = false,
        IEnumerable<string>? organizationIds = null,
        IEnumerable<string>? workspaceIds = null,
        IEnumerable<string>? countryCodes = null,
        IEnumerable<string>? regionIds = null,
        IEnumerable<Sector>? sectors = null,
        IEnumerable<string>? sensitivityCodes = null,
        IEnumerable<CaseOperation>? operations = null,
        IReadOnlyDictionary<Guid, IEnumerable<CaseOperation>>? sharedCaseOperations = null) => new(
        groupId,
        validFromUtc ?? Now.AddDays(-1),
        expiresAtUtc ?? Now.AddHours(1),
        assignedCaseIds ?? [],
        sharedCaseIds ?? [],
        canAccessJurisdictionalCases,
        organizationIds ?? ["org-a"],
        workspaceIds ?? ["workspace-a"],
        countryCodes ?? ["ZW"],
        regionIds ?? ["ZW-MW"],
        sectors ?? [Sector.AnimalHealth],
        sensitivityCodes ?? ["STANDARD"],
        operations ?? [CaseOperation.ReadCase], sharedCaseOperations);

    private sealed class TestGrantStore(Func<ActorContext, CurrentAccessGrant?> getGrant) : ICurrentAccessGrantStore
    {
        public int LookupCount { get; private set; }

        public ValueTask<CurrentAccessGrant?> GetCurrentGrantAsync(ActorContext actor, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LookupCount++;
            return ValueTask.FromResult(getGrant(actor));
        }
    }

    private sealed class TestResourceStore(Func<Guid, CaseAccessDescriptor?> getDescriptor) : ICaseAccessDescriptorStore
    {
        public int LookupCount { get; private set; }

        public ValueTask<CaseAccessDescriptor?> GetCurrentDescriptorAsync(Guid caseId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LookupCount++;
            return ValueTask.FromResult(getDescriptor(caseId));
        }
    }

    private sealed class AdvancingResourceStore(
        AdvanceableTimeProvider timeProvider,
        CaseAccessDescriptor descriptor,
        TimeSpan elapsedDuringLookup) : ICaseAccessDescriptorStore
    {
        public ValueTask<CaseAccessDescriptor?> GetCurrentDescriptorAsync(Guid caseId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            timeProvider.Advance(elapsedDuringLookup);
            return ValueTask.FromResult<CaseAccessDescriptor?>(descriptor);
        }
    }

    private sealed class AdvanceableTimeProvider(DateTimeOffset currentUtc) : TimeProvider
    {
        private DateTimeOffset _currentUtc = currentUtc;

        public override DateTimeOffset GetUtcNow() => _currentUtc;

        public void Advance(TimeSpan elapsed) => _currentUtc = _currentUtc.Add(elapsed);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
