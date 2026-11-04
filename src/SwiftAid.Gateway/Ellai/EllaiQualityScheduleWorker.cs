using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using SwiftAid.Infrastructure.Classification;
using SwiftAid.Intelligence.Meta;

namespace SwiftAid.Gateway.Ellai;

public sealed class EllaiQualityScheduleWorker(IServiceScopeFactory scopes,
    IOptions<EllaiQualityScheduleOptions> configuration, IOptions<ClassificationOptions> classification,
    ILogger<EllaiQualityScheduleWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = configuration.Value;
        if (!options.Enabled) return;
        options.Validate();
        if (!classification.Value.Enabled) throw new InvalidOperationException("Quality scheduling requires classification scope.");
        classification.Value.Validate();
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var workspace in options.Workspaces)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var services = scope.ServiceProvider;
                    var factory = services.GetRequiredService<IEllaiFrameworkAgentFactory>();
                    var client = services.GetRequiredKeyedService<IChatClient>(EllaiReasoningRegistration.FrameworkClientKey);
                    var agent = factory.Create(client, new ChatClientAgentOptions
                    {
                        Name = "EllaiDataQuality",
                        UseProvidedChatClientAsIs = true,
                        ChatHistoryProvider = new InMemoryChatHistoryProvider()
                    });
                    var result = await services.GetRequiredService<EllaiQualitySweep>().RunAsync(workspace, options,
                        token => agent.CreateSessionAsync(cancellationToken: token).AsTask(), stoppingToken);
                    logger.LogInformation("Ellai quality sweep finished: {Outcome}", result);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    logger.LogWarning("Ellai quality sweep failed: {FailureType}", error.GetType().Name);
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(options.PollSeconds), stoppingToken);
        }
    }
}
