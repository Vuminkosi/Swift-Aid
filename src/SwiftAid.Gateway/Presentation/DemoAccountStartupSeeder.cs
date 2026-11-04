using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SwiftAid.Application.Authorization;
using SwiftAid.Infrastructure.Demo;
using SwiftAid.Infrastructure.Identity;
using SwiftAid.Infrastructure.Persistence;

namespace SwiftAid.Gateway.Presentation;

/// <summary>Idempotent, explicitly configured development login for the synthetic demo workspace.</summary>
public sealed class DemoAccountStartupSeeder(IServiceScopeFactory scopes, IConfiguration configuration,
    IHostEnvironment environment) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue<bool>("DemoAccount:Enabled")) return;
        if (!environment.IsDevelopment() || !configuration.GetValue<bool>("DemoAccess:Enabled"))
            throw new InvalidOperationException("Demo accounts require Development and explicit demo access.");
        var email = configuration["DemoAccount:Email"];
        var password = configuration["DemoAccount:Password"];
        if (string.IsNullOrWhiteSpace(email) || !email.EndsWith("@swiftaid.test", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Configure a synthetic demo email and password.");
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var users = services.GetRequiredService<UserManager<SwiftAidUser>>();
        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            var result = await services.GetRequiredService<IPasswordIdentityRegistrar>()
                .RegisterAsync(email, password, "SwiftAid Demo", cancellationToken);
            if (result.Status is not (PasswordRegistrationStatus.Registered or PasswordRegistrationStatus.DuplicateEmail))
                throw new InvalidOperationException("The demo account could not be created.");
            user = await users.FindByEmailAsync(email) ?? throw new InvalidOperationException("The demo account is missing.");
            user.EmailConfirmed = true;
            if (!(await users.UpdateAsync(user)).Succeeded) throw new InvalidOperationException("Demo email confirmation failed.");
        }
        // Never reset an existing password or revive a disabled account on restart.
        var db = services.GetRequiredService<SwiftAidDbContext>();
        if (!await db.Workspaces.AnyAsync(row => row.WorkspaceId == DemoCaseImporter.DemoWorkspaceId, cancellationToken))
            throw new InvalidOperationException("The demo workspace must be seeded before the demo account.");
        var principal = await services.GetRequiredService<IUserClaimsPrincipalFactory<SwiftAidUser>>().CreateAsync(user);
        await services.GetRequiredService<DemoAccessProvisioner>().EnsureAsync(ActorContext.FromValidatedPrincipal(principal), cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
