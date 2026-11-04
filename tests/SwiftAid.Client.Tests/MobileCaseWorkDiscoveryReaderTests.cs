using SwiftAid.Contracts.Cases;
using SwiftAid.Mobile.Services.Presentation;

namespace SwiftAid.Client.Tests;

public sealed class MobileCaseWorkDiscoveryReaderTests
{
    [Fact]
    public async Task ReadsEveryPageAndPreservesAssignedScope()
    {
        var client = new Fixture();
        var result = await MobileCaseWorkDiscoveryReader.ReadAsync(client, CaseWorkDiscoveryKind.FollowUp, CaseWorkDiscoveryScope.Assigned);
        Assert.Equal(CaseQueueAccess.Allowed, result.Access);
        Assert.Equal(205, result.CaseIds.Count);
        Assert.Equal(3, client.Reads);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DiscardsEarlierPagesOnDenialOrRepeatedCase(bool deny)
    {
        var client = new Fixture { Deny = deny, Duplicate = !deny };
        var result = await MobileCaseWorkDiscoveryReader.ReadAsync(client, CaseWorkDiscoveryKind.FollowUp, CaseWorkDiscoveryScope.Assigned);
        Assert.Equal(deny ? CaseQueueAccess.Denied : CaseQueueAccess.Unavailable, result.Access);
        Assert.Empty(result.CaseIds);
    }

    private sealed class Fixture : ICaseWorkDiscoveryClient
    {
        public int Reads { get; private set; }
        public bool Deny { get; init; }
        public bool Duplicate { get; init; }
        public Task<CaseWorkDiscoveryPage> GetAsync(CaseWorkDiscoveryKind kind, CaseWorkDiscoveryScope scope,
            int limit, Guid? cursor, CancellationToken cancellationToken = default)
        {
            Assert.Equal(CaseWorkDiscoveryKind.FollowUp, kind);
            Assert.Equal(CaseWorkDiscoveryScope.Assigned, scope);
            Reads++;
            if (Deny && cursor is not null) return Task.FromResult(new CaseWorkDiscoveryPage(CaseQueueAccess.Denied, []));
            var offset = (Reads - 1) * limit;
            var ids = Enumerable.Range(offset, Math.Min(limit, 205 - offset))
                .Select(index => Guid.Parse($"{(Duplicate && index == 100 ? 1 : index + 1):x8}-0000-0000-0000-000000000001"))
                .ToArray();
            return Task.FromResult(new CaseWorkDiscoveryPage(CaseQueueAccess.Allowed, ids,
                offset + ids.Length < 205 ? ids[^1] : null));
        }
    }
}
