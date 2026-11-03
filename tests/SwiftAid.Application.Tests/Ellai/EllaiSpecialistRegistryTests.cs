using SwiftAid.Application.Ellai;
using SwiftAid.Application.Ellai.Specialists;

namespace SwiftAid.Application.Tests.Ellai;

public sealed class EllaiSpecialistRegistryTests
{
    [Fact]
    public void KnowledgeSpecialistCannotExpandParentToolAuthority()
    {
        var parent = Context(new HashSet<string> { EllaiToolNames.DocsSearch, EllaiToolNames.State });

        Assert.True(EllaiSpecialistRegistry.TryCreateToolContext(EllaiSpecialistRegistry.Knowledge, true,
            parent, out var child));
        Assert.NotNull(child);
        Assert.Equal(new[] { EllaiToolNames.DocsSearch }, child.AllowedTools);
        Assert.Equal(parent.SessionId, child.SessionId);
        Assert.Equal(parent.ScopeEpoch, child.ScopeEpoch);
        Assert.Equal(parent.Actor, child.Actor);
        Assert.Equal(parent.RemainingCalls, child.RemainingCalls);
    }

    [Fact]
    public void DisabledUnknownOrUnboundedParentCannotCreateContext()
    {
        var parent = Context(new HashSet<string> { EllaiToolNames.DocsSearch });
        Assert.False(EllaiSpecialistRegistry.TryCreateToolContext(EllaiSpecialistRegistry.Knowledge, false, parent, out _));
        Assert.False(EllaiSpecialistRegistry.TryCreateToolContext("unknown", true, parent, out _));
        Assert.False(EllaiSpecialistRegistry.TryCreateToolContext(EllaiSpecialistRegistry.Knowledge, true, Context(null), out _));
    }

    private static EllaiToolContext Context(IReadOnlySet<string>? tools) =>
        new("session", "epoch", "feature", null, 3, true, AllowedTools: tools);
}
