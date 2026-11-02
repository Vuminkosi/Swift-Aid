using System.Text.Json;
using SwiftAid.Application.Ellai;
using SwiftAid.Gateway.Ellai;
using SwiftAid.Infrastructure.Persistence.Ellai;

namespace SwiftAid.Integration.Tests;

public sealed class EllaiMemoryProjectionTests
{
    [Theory]
    [InlineData("owner", "scope", 1, true)]
    [InlineData("other", "scope", 1, false)]
    [InlineData("owner", "other", 1, false)]
    [InlineData("owner", "scope", -1, false)]
    public void ProjectionRequiresCurrentOwnerScopeAndExpiry(string actor, string scope, int hours, bool accepted)
    {
        var source = JsonSerializer.Serialize(new
        {
            ActorId = actor, ScopeEpoch = scope, ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(hours),
            Cases = new[] { new { Id = Guid.NewGuid(), Version = 1L } }
        });
        var topic = new EllaiTopicEntity { TopicId = "topic", SessionId = "session", EntityRef = source };
        var reference = EllaiMemoryProjectionWorker.CreateReference(topic, "owner", "scope");
        Assert.Equal(accepted, reference is not null);
        if (accepted)
        {
            Assert.Equal(EllaiCaseResultMemory.SourceVersion(source), reference!.SourceVersion);
            Assert.Equal("topic", reference.TopicId);
            Assert.DoesNotContain("Cases", JsonSerializer.Serialize(reference));
        }
    }

    [Fact]
    public void InvalidSourceDoesNotProduceAnIndexReference() => Assert.Null(
        EllaiMemoryProjectionWorker.CreateReference(new EllaiTopicEntity { EntityRef = "{}" }, "owner", "scope"));
}
