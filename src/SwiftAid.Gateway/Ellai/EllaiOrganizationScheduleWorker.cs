using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using SwiftAid.Infrastructure.Classification;
using SwiftAid.Intelligence.Meta;

namespace SwiftAid.Gateway.Ellai;

public sealed class EllaiOrganizationScheduleWorker(IServiceScopeFactory scopes,
    IOptions<EllaiOrganizationScheduleOptions> configuration, IOptions<EllaiOrganizationOptions> organization,
    ILogger<EllaiOrganizationScheduleWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = configuration.Value;
        if (!options.Enabled) return;
        options.Validate();
        if (!organization.Value.Enabled) throw new InvalidOperationException("Organization scheduling requires an explicit organization scope.");
        organization.Value.Validate();
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var workspace in options.Workspaces)
            {
                var progress = new EllaiOrganizationSweepProgress();
                var startedUtc = DateTimeOffset.UtcNow;
                var started = Stopwatch.GetTimestamp();
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var services = scope.ServiceProvider;
                    var factory = services.GetRequiredService<IEllaiFrameworkAgentFactory>();
                    var client = services.GetRequiredKeyedService<IChatClient>(EllaiReasoningRegistration.FrameworkClientKey);
                    var agent = factory.Create(client, new ChatClientAgentOptions
                    {
                        Name = "EllaiOrganization",
                        UseProvidedChatClientAsIs = true,
                        ChatHistoryProvider = new InMemoryChatHistoryProvider()
                    });
                    var result = await services.GetRequiredService<EllaiOrganizationSweep>().RunAsync(workspace, options,
                        token => agent.CreateSessionAsync(cancellationToken: token).AsTask(), progress, stoppingToken);
                    var level = result == "not-due" ? LogLevel.Debug :
                        result is "configuration-mismatch" or "policy-mismatch" ? LogLevel.Warning : LogLevel.Information;
                    logger.Log(level, "Ellai operational run {RunId} role {Role} finished {Outcome}; turns {WorkTurns}, changes {ChangesCommitted}, model calls {ModelCalls}, started {StartedUtc}, finished {FinishedUtc}, elapsed ms {ElapsedMilliseconds}",
                        progress.RunId, "ops.organization", result, progress.WorkTurns, progress.ChangesCommitted, 0,
                        startedUtc, DateTimeOffset.UtcNow, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    logger.LogInformation("Ellai operational run {RunId} role {Role} cancelled; turns {WorkTurns}, changes {ChangesCommitted}, started {StartedUtc}, finished {FinishedUtc}, elapsed ms {ElapsedMilliseconds}",
                        progress.RunId, "ops.organization", progress.WorkTurns, progress.ChangesCommitted, startedUtc,
                        DateTimeOffset.UtcNow, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    return;
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    logger.LogWarning("Ellai operational run {RunId} role {Role} failed {FailureType}; turns {WorkTurns}, changes {ChangesCommitted}, started {StartedUtc}, finished {FinishedUtc}, elapsed ms {ElapsedMilliseconds}",
                        progress.RunId, "ops.organization", error.GetType().Name, progress.WorkTurns, progress.ChangesCommitted,
                        startedUtc, DateTimeOffset.UtcNow, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(options.PollSeconds), stoppingToken);
        }
    }
}

