using SwiftAid.Application.Ellai;

namespace SwiftAid.Gateway.Ellai;

/// <summary>Background reasoning worker: dequeues accepted utterances and
/// runs each through the multi-turn loop on its own task, one run per
/// session at a time (queue single-flight, latest wins). Progress and
/// results flow through the session event journal to the polling UI; the
/// utterance POST never blocks on the model. Runs without a resolvable
/// reasoning loop (Ellai disabled) are released unprocessed.</summary>
public sealed class EllaiUtteranceWorker(
    IServiceProvider services,
    ILogger<EllaiUtteranceWorker> logger) : BackgroundService
{
    public const int MaxTokensPerTurn = 1024;

    private readonly IServiceProvider _services =
        services ?? throw new ArgumentNullException(nameof(services));
    private readonly ILogger<EllaiUtteranceWorker> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    public EllaiUtteranceQueue Queue => _services.GetRequiredService<EllaiUtteranceQueue>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queue = _services.GetRequiredService<EllaiUtteranceQueue>();
        while (!stoppingToken.IsCancellationRequested)
        {
            EllaiUtteranceWork work;
            try
            {
                work = await queue.DequeueAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            _ = RunOneAsync(work, stoppingToken);
        }
    }

    private async Task RunOneAsync(EllaiUtteranceWork work, CancellationToken stoppingToken)
    {
        try
        {
            using var scope = _services.CreateScope();
            var services = new EllaiUtteranceServices(
                scope.ServiceProvider.GetRequiredService<EllaiReasoningLoop>(),
                scope.ServiceProvider.GetService<EllaiActivityStatusCoordinator>(),
                scope.ServiceProvider.GetService<EllaiSessionEventJournal>(),
                scope.ServiceProvider.GetRequiredService<EllaiConsentService>());
            var outcome = await EllaiUtteranceRunner.RunOneAsync(
                work, services, MaxTokensPerTurn, stoppingToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Ellai utterance run for session {SessionId} finished: started {Started}, completed {Completed}.",
                work.SessionId, outcome.Started, outcome.Completed);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ellai utterance run for session {SessionId} failed.", work.SessionId);
        }
        finally
        {
            Queue.Complete(work.SessionId, work);
        }
    }
}
