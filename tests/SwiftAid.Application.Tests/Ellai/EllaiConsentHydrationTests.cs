using SwiftAid.Application.Ellai;

namespace SwiftAid.Application.Tests.Ellai;

[Trait("Feature", "Ellai")]
[Trait("Task", "ELL-T03")]
public sealed class EllaiConsentHydrationTests
{
    private static EllaiConsentGrant Record(
        InMemoryEllaiConsentStore store, string session, string purpose, bool revoked = false)
    {
        var grant = new EllaiConsentGrant(
            $"grant-{Guid.NewGuid():N}", session, EllaiConsentDimension.ReasoningText,
            purpose, EllaiProcessingClassification.SyntheticDemo, 3, DateTimeOffset.UtcNow.AddHours(1), revoked);
        store.RecordAsync(grant, CancellationToken.None).GetAwaiter().GetResult();
        return grant;
    }

    [Fact]
    public async Task HydrateRestoresGrantsAfterRestart()
    {
        var store = new InMemoryEllaiConsentStore();
        Record(store, "session-1", EllaiToolBroker.ToolReadPurpose);

        // Fresh service (simulated restart) sees nothing until hydration.
        var service = new EllaiConsentService();
        Assert.False(service.IsGranted(
            "session-1", EllaiConsentDimension.ReasoningText,
            EllaiToolBroker.ToolReadPurpose, EllaiProcessingClassification.SyntheticDemo, DateTimeOffset.UtcNow));

        await service.HydrateAsync("session-1", store, CancellationToken.None);

        Assert.True(service.IsGranted(
            "session-1", EllaiConsentDimension.ReasoningText,
            EllaiToolBroker.ToolReadPurpose, EllaiProcessingClassification.SyntheticDemo, DateTimeOffset.UtcNow));
        Assert.Equal("consent/3", service.VersionLabel("session-1"));
    }

    [Fact]
    public async Task HydrateAppliesRecordedRevocations()
    {
        var store = new InMemoryEllaiConsentStore();
        var grant = Record(store, "session-1", EllaiToolBroker.ToolReadPurpose);
        var service = new EllaiConsentService();
        await service.HydrateAsync("session-1", store, CancellationToken.None);
        Assert.True(service.IsGranted(
            "session-1", EllaiConsentDimension.ReasoningText,
            EllaiToolBroker.ToolReadPurpose, EllaiProcessingClassification.SyntheticDemo, DateTimeOffset.UtcNow));

        Assert.True(await store.SetRevokedAsync("session-1", grant.GrantId, CancellationToken.None));
        await service.HydrateAsync("session-1", store, CancellationToken.None);

        Assert.False(service.IsGranted(
            "session-1", EllaiConsentDimension.ReasoningText,
            EllaiToolBroker.ToolReadPurpose, EllaiProcessingClassification.SyntheticDemo, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task HydratePreservesMemoryGrantsAndNeverUnrevokes()
    {
        var store = new InMemoryEllaiConsentStore();
        var service = new EllaiConsentService();
        service.Grant("session-1", EllaiConsentDimension.ReasoningText,
            EllaiToolBroker.ToolReadPurpose, EllaiProcessingClassification.SyntheticDemo, TimeSpan.FromHours(1));
        service.RevokeSession("session-1");

        await service.HydrateAsync("session-1", store, CancellationToken.None);

        Assert.Equal(1, service.LatestVersion("session-1"));
        Assert.False(service.IsGranted(
            "session-1", EllaiConsentDimension.ReasoningText,
            EllaiToolBroker.ToolReadPurpose, EllaiProcessingClassification.SyntheticDemo, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task HydrateAdvancesVersionPastDurableRows()
    {
        var store = new InMemoryEllaiConsentStore();
        Record(store, "session-1", EllaiToolBroker.ToolReadPurpose);
        var service = new EllaiConsentService();
        await service.HydrateAsync("session-1", store, CancellationToken.None);

        var next = service.Grant("session-1", EllaiConsentDimension.ReasoningText,
            EllaiToolBroker.ToolReadPurpose, EllaiProcessingClassification.SyntheticDemo, TimeSpan.FromHours(1));

        Assert.True(next.Version > 3);
    }
}
