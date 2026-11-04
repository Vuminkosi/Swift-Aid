using SwiftAid.Application.Ellai;
using SwiftAid.Infrastructure.Classification;

namespace SwiftAid.Gateway.Ellai;

/// <summary>Processes protected PostgreSQL translation jobs, never holding a transaction across inference.</summary>
public sealed class EllaiTranslationWorker(IServiceScopeFactory scopes, ILogger<EllaiTranslationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var registry = scope.ServiceProvider.GetRequiredService<TranslationRegistry>();
                var provider = scope.ServiceProvider.GetRequiredService<IEllaiLanguageClient>();
                // Missing provider configuration pauses work rather than exhausting durable attempts.
                var work = provider.IsConfigured ? await registry.ClaimAsync(stoppingToken) : null;
                if (work is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    continue;
                }
                try
                {
                    var result = await provider.TranslateAsync(new(work.ArtifactId.ToString("N"), work.Text, work.SourceLanguage, "eng"), stoppingToken);
                    await registry.CompleteAsync(work, result, stoppingToken);
                }
                catch (EllaiLanguageProviderException error)
                {
                    await registry.FailAsync(work, error.Retryable, stoppingToken);
                }
                catch (InvalidDataException)
                {
                    await registry.FailAsync(work, false, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                // Provider and database exceptions can contain protected values: log the type only.
                logger.LogWarning("Translation processing deferred: {FailureType}", error.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
