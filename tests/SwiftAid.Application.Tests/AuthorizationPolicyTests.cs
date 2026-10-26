using System.Security.Claims;
using System.Text.Json;
using SwiftAid.Application.Authorization;
using SwiftAid.Contracts.Commands;
using SwiftAid.Contracts.Sync;

namespace SwiftAid.Application.Tests;

public sealed class AuthorizationPolicyTests
{
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

        Assert.Equal(new[] { "org-a", "org-z" }, manifest.OrganizationIds);
        Assert.Equal(new[] { "workspace-a", "workspace-z" }, manifest.WorkspaceIds);
        Assert.Equal(new[] { "SZ", "ZW" }, manifest.CountryCodes);
        Assert.Equal(new[] { "ZW-MA", "ZW-MW" }, manifest.RegionIds);
        Assert.Equal(3, manifest.CaseIds.Count);
        Assert.Equal(7, manifest.AccessEpoch);
        Assert.Equal(Now.AddHours(1), manifest.ExpiresAtUtc);
        Assert.DoesNotContain("STANDARD", JsonSerializer.Serialize(manifest), StringComparison.Ordinal);
        Assert.DoesNotContain("HealthOfficer", JsonSerializer.Serialize(manifest), StringComparison.Ordinal);
        Assert.Equal(1, store.LookupCount);
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
        IEnumerable<Guid>? assignedCaseIds = null,
        IEnumerable<Guid>? sharedCaseIds = null,
        bool canAccessJurisdictionalCases = false,
        IEnumerable<string>? organizationIds = null,
        IEnumerable<string>? workspaceIds = null,
        IEnumerable<string>? countryCodes = null,
        IEnumerable<string>? regionIds = null,
        IEnumerable<Sector>? sectors = null,
        IEnumerable<string>? sensitivityCodes = null,
        IEnumerable<CaseOperation>? operations = null) => new(
        actorId,
        accessEpoch,
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
        operations ?? [CaseOperation.ReadCase]);

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

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
