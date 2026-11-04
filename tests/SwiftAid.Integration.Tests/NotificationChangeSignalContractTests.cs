using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql.EntityFrameworkCore.PostgreSQL;
using SwiftAid.Application.Authorization;
using SwiftAid.Gateway.Notifications;
using SwiftAid.Infrastructure.Configuration;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Integration.Tests;

public sealed class NotificationChangeSignalContractTests
{
    [Fact]
    public async Task PollingSignal_NullActor_ThrowsBeforeTouchingDatabase()
    {
        await using var context = CreateUnconnectedDbContext();
        var signal = new PollingNotificationChangeSignal(context, TimeProvider.System);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => signal.WaitForChangeAsync(null!, TimeSpan.FromSeconds(5), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task PollingSignal_NonPositiveTimeout_ThrowsBeforeTouchingDatabase()
    {
        await using var context = CreateUnconnectedDbContext();
        var signal = new PollingNotificationChangeSignal(context, TimeProvider.System);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => signal.WaitForChangeAsync(TestActor(), TimeSpan.Zero, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Bridge_NullActor_ThrowsBeforeTouchingInfrastructure()
    {
        var bridge = CreateBridge();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => bridge.WaitForChangeAsync(null!, TimeSpan.FromSeconds(5), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Bridge_TimeoutOutsideOneMinuteWindow_ThrowsBeforeTouchingInfrastructure()
    {
        var bridge = CreateBridge();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => bridge.WaitForChangeAsync(TestActor(), TimeSpan.Zero, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => bridge.WaitForChangeAsync(TestActor(), TimeSpan.FromMinutes(2), CancellationToken.None).AsTask());
    }

    [Fact]
    public void Bridge_NullDependencies_Throw()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        var options = Options.Create(new NatsOptions());

        Assert.Throws<ArgumentNullException>(() => new JetStreamNotificationChangeBridge(
            null!, scopes, options, TimeProvider.System, NullLogger<JetStreamNotificationChangeBridge>.Instance));
        Assert.Throws<ArgumentNullException>(() => new JetStreamNotificationChangeBridge(
            services, null!, options, TimeProvider.System, NullLogger<JetStreamNotificationChangeBridge>.Instance));
        Assert.Throws<ArgumentNullException>(() => new JetStreamNotificationChangeBridge(
            services, scopes, null!, TimeProvider.System, NullLogger<JetStreamNotificationChangeBridge>.Instance));
        Assert.Throws<ArgumentNullException>(() => new JetStreamNotificationChangeBridge(
            services, scopes, options, null!, NullLogger<JetStreamNotificationChangeBridge>.Instance));
        Assert.Throws<ArgumentNullException>(() => new JetStreamNotificationChangeBridge(
            services, scopes, options, TimeProvider.System, null!));
    }

    private static JetStreamNotificationChangeBridge CreateBridge()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        return new JetStreamNotificationChangeBridge(
            services,
            services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new NatsOptions()),
            TimeProvider.System,
            NullLogger<JetStreamNotificationChangeBridge>.Instance);
    }

    private static SwiftAidDbContext CreateUnconnectedDbContext()
    {
        var options = new DbContextOptionsBuilder<SwiftAidDbContext>()
            .UseNpgsql("Host=localhost;Port=1;Database=unused;Username=unused;Password=unused")
            .Options;
        return new SwiftAidDbContext(options);
    }

    private static ActorContext TestActor()
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "actor-1"),
                new Claim(ActorContext.AccessEpochClaimType, "7")
            ],
            authenticationType: "test");
        return ActorContext.FromValidatedPrincipal(new ClaimsPrincipal(identity));
    }
}
