using Microsoft.Extensions.DependencyInjection;
using SwiftAid.Application.Sessions;
using SwiftAid.Contracts.Sessions;

namespace SwiftAid.Infrastructure.Persistence.Sessions;

/// <summary>
/// Optional wiring for the P10.1 community-session slice. The Gateway/API lane owns
/// actual service registration; this extension only collects the registrations so the
/// API lane can call it without duplicating them.
/// </summary>
public static class CommunitySessionServiceCollectionExtensions
{
    public static IServiceCollection AddCommunitySessions(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<ICommunitySessionStore, PostgresCommunitySessionStore>();
        services.AddScoped<ICommunitySessionService, CommunitySessionService>();
        services.AddScoped<ICommunitySessionHistoryReader, CommunitySessionHistoryReader>();
        services.AddScoped<IEventSubscriptionStore, PostgresEventSubscriptionStore>();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IEventSubscriptionService, EventSubscriptionService>();
        services.AddScoped<ISessionReminderDeliveryStore, PostgresSessionReminderDeliveryStore>();
        services.AddScoped<ISessionReminderChannelSender, SessionReminderChannelSender>();
        services.AddScoped<SessionReminderDeliveryDispatcher>();
        return services;
    }
}
