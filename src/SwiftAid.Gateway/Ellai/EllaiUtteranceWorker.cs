using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Gateway.Ellai;

/// <summary>Executes accepted Ellai work from the PostgreSQL-backed outbox.
/// A durable claim survives process restarts; leases recover abandoned work,
/// and the store serializes claims within a session.</summary>
public sealed class EllaiUtteranceWorker(
    IServiceProvider services,
    ILogger<EllaiUtteranceWorker> logger) : BackgroundService
{
    public const int MaxTokensPerTurn = 2048;
    private static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan IdlePollDelay = TimeSpan.FromSeconds(1);

    private readonly IServiceProvider _services =
        services ?? throw new ArgumentNullException(nameof(services));
    private readonly ILogger<EllaiUtteranceWorker> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var outbox = scope.ServiceProvider.GetRequiredService<IEllaiUtteranceOutboxStore>();
                var now = DateTimeOffset.UtcNow;
                var item = await outbox.ClaimNextAsync(now, now.Add(ClaimLease), stoppingToken)
                    .ConfigureAwait(false);
                if (item is null)
                {
                    await Task.Delay(IdlePollDelay, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await ProcessOneAsync(item, outbox, scope.ServiceProvider, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ellai outbox polling failed; retrying after a short delay.");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private async Task ProcessOneAsync(
        EllaiUtteranceOutboxItem item,
        IEllaiUtteranceOutboxStore outbox,
        IServiceProvider scopedServices,
        CancellationToken stoppingToken)
    {
        EllaiUtteranceWork work;
        try
        {
            var envelope = EllaiUtteranceWorkEnvelope.Deserialize(item.PayloadJson);
            if (!string.Equals(envelope.SessionId, item.SessionId, StringComparison.Ordinal)
                || !string.Equals(envelope.ClientUtteranceId, item.ClientUtteranceId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The outbox item does not match its work envelope.");
            }

            work = envelope.ToWork();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException)
        {
            _logger.LogError(ex,
                "Discarding invalid Ellai outbox payload for session {SessionId}.", item.SessionId);
            await outbox.FailAsync(item.SessionId, item.ClientUtteranceId, item.AttemptCount,
                DateTimeOffset.UtcNow, terminal: true, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        try
        {
            var sessions = scopedServices.GetRequiredService<IEllaiSessionStore>();
            var session = await sessions.GetAsync(work.SessionId, stoppingToken).ConfigureAwait(false);
            if (session is null
                || !string.Equals(session.Status, "Active", StringComparison.Ordinal)
                || session.InteractionEpoch != work.ControllerEpoch
                || !string.Equals(session.ScopeEpoch, work.ScopeEpoch, StringComparison.Ordinal)
                || !string.Equals(session.OwnerActorId, work.Actor.ActorId, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "Skipping stale Ellai outbox work for session {SessionId}.", work.SessionId);
                await outbox.CompleteAsync(item.SessionId, item.ClientUtteranceId, item.AttemptCount,
                    DateTimeOffset.UtcNow, stoppingToken).ConfigureAwait(false);
                return;
            }

            var outcome = await RunWorkAsync(work, scopedServices, stoppingToken).ConfigureAwait(false);
            if (outcome.Completed
                && !string.IsNullOrWhiteSpace(outcome.DisplayText)
                && string.Equals(work.ConsentPurpose, EllaiConsentPurposes.PublicProductHelp, StringComparison.Ordinal)
                && string.Equals(work.ProcessingLabel, EllaiProcessingClassification.ConsentedNonSensitive, StringComparison.Ordinal)
                && string.Equals(work.DataClassification, EllaiProcessingClassification.ConsentedNonSensitive, StringComparison.Ordinal))
            {
                var current = await sessions.GetAsync(work.SessionId, stoppingToken).ConfigureAwait(false);
                if (current is not null
                    && string.Equals(current.Status, "Active", StringComparison.Ordinal)
                    && current.InteractionEpoch == work.ControllerEpoch
                    && string.Equals(current.ScopeEpoch, work.ScopeEpoch, StringComparison.Ordinal)
                    && string.Equals(current.OwnerActorId, work.Actor.ActorId, StringComparison.Ordinal))
                {
                    scopedServices.GetService<EllaiSessionResponseStore>()?
                        .Publish(work.SessionId, item.ClientUtteranceId, outcome.DisplayText);
                }
            }

            _logger.LogInformation(
                "Ellai utterance run for session {SessionId} finished: started {Started}, completed {Completed}.",
                work.SessionId, outcome.Started, outcome.Completed);
            await outbox.CompleteAsync(item.SessionId, item.ClientUtteranceId, item.AttemptCount,
                DateTimeOffset.UtcNow, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Make the item immediately recoverable during a clean shutdown.
            try
            {
                await outbox.FailAsync(item.SessionId, item.ClientUtteranceId, item.AttemptCount,
                    DateTimeOffset.UtcNow, terminal: false, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Could not release Ellai outbox work for session {SessionId} during shutdown.", item.SessionId);
            }

            throw;
        }
        catch (Exception ex)
        {
            var terminal = item.AttemptCount >= 3;
            var retryDelay = item.AttemptCount switch
            {
                1 => TimeSpan.FromSeconds(5),
                2 => TimeSpan.FromSeconds(30),
                _ => TimeSpan.Zero,
            };
            _logger.LogError(ex,
                "Ellai outbox work for session {SessionId} failed on attempt {AttemptCount}.",
                item.SessionId, item.AttemptCount);
            await outbox.FailAsync(item.SessionId, item.ClientUtteranceId, item.AttemptCount,
                DateTimeOffset.UtcNow.Add(retryDelay), terminal, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task<EllaiUtteranceOutcome> RunWorkAsync(
        EllaiUtteranceWork work,
        IServiceProvider scopedServices,
        CancellationToken stoppingToken)
    {
        var consent = scopedServices.GetRequiredService<EllaiConsentService>();
        var consentStore = scopedServices.GetService<IEllaiConsentStore>();
        if (consentStore is not null)
        {
            await consent.HydrateAsync(work.SessionId, consentStore, stoppingToken).ConfigureAwait(false);
        }

        var ledger = scopedServices.GetService<EllaiUsageLedger>();
        var usageStore = scopedServices.GetService<IEllaiUsageStore>();
        if (ledger is not null && usageStore is not null)
        {
            await ledger.HydrateAsync(work.SessionId, usageStore, stoppingToken).ConfigureAwait(false);
        }

        var dependencies = new EllaiUtteranceServices(
            scopedServices.GetRequiredService<EllaiReasoningLoop>(),
            scopedServices.GetService<EllaiActivityStatusCoordinator>(),
            scopedServices.GetService<EllaiSessionEventJournal>(),
            consent,
            scopedServices.GetRequiredService<IEllaiSessionStore>(),
            consentStore);
        return await EllaiUtteranceRunner.RunOneAsync(
            work, dependencies, MaxTokensPerTurn, stoppingToken).ConfigureAwait(false);
    }
}
