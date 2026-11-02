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
        _logger.LogInformation("Ellai worker pickup: session={SessionId} turn={TurnId} attempt={Attempt}.",
            item.SessionId, item.ClientUtteranceId, item.AttemptCount);
        if (item.AcceptedAtUtc is { } acceptedAt)
        {
            EllaiPipelineMetrics.Record("admission_to_pickup", (DateTimeOffset.UtcNow - acceptedAt).TotalMilliseconds);
        }
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

        var progress = new EllaiToolProgressPublisher(
            scopedServices.GetRequiredService<EllaiSessionResponseStore>(), work.SessionId,
            item.ClientUtteranceId, work.ControllerEpoch, item.AttemptCount,
            englishVoiceId: work.Languages?.EnglishVoiceId);
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
                    DateTimeOffset.UtcNow, stoppingToken, EllaiUtteranceResolution.Cancelled).ConfigureAwait(false);
                return;
            }

            var started = System.Diagnostics.Stopwatch.StartNew();
            var outcome = await RunWorkAsync(work, scopedServices, stoppingToken, progress).ConfigureAwait(false);
            if (!outcome.Completed)
            {
                _logger.LogWarning("Ellai turn {SessionId}/{UtteranceId} stopped with {FailureCode}.",
                    work.SessionId, item.ClientUtteranceId, outcome.FailureCode ?? "AdmissionDenied");
                if (outcome.FailureCode == "InvalidDecision")
                    _logger.LogWarning("Ellai decision shape rejected: {Diagnostic}", outcome.StopReason);
            }
            var resolution = outcome.Completed && !string.IsNullOrWhiteSpace(outcome.DisplayText)
                && EllaiResponsePublicationPolicy.CanPublish(work)
                ? EllaiUtteranceResolution.Completed(outcome.DisplayText, work.ControllerEpoch) with
                {
                    UiProposalsJson = EllaiUiProposalPayload.Serialize(outcome.Decision?.UiIntents ?? [])
                }
                : EllaiUtteranceResolution.Failed with { ExpectedInteractionEpoch = work.ControllerEpoch };
            var providerElapsed = started.Elapsed.TotalMilliseconds;
            var committed = await outbox.CompleteAsync(item.SessionId, item.ClientUtteranceId, item.AttemptCount,
                DateTimeOffset.UtcNow, stoppingToken, resolution).ConfigureAwait(false);
            EllaiPipelineMetrics.Record("outcome_commit", started.Elapsed.TotalMilliseconds - providerElapsed);
            if (committed is not null)
            {
                using var publication = EllaiPipelineMetrics.Measure("publication");
                // Transient delivery is only a hint; the owner can recover the committed outcome
                // from PostgreSQL even when this process exits before this publication.
                scopedServices.GetService<EllaiSessionResponseStore>()?.Reconcile(
                    work.SessionId, item.ClientUtteranceId, committed, work.Languages?.OutputLanguage ?? "eng",
                    work.Languages?.EnglishVoiceId);
                progress.Finish(committed.Outcome);
            }
            _logger.LogInformation(
                "Ellai turn {SessionId}/{UtteranceId} attempt {Attempt} outcome {Outcome}; processing {ProcessingMs}ms, commit {CommitMs}ms.",
                work.SessionId, item.ClientUtteranceId, item.AttemptCount, committed?.Outcome ?? "StaleClaim",
                providerElapsed, started.Elapsed.TotalMilliseconds - providerElapsed);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Make the item immediately recoverable during a clean shutdown.
            try
            {
                await outbox.FailAsync(item.SessionId, item.ClientUtteranceId, item.AttemptCount,
                    DateTimeOffset.UtcNow, terminal: false, CancellationToken.None).ConfigureAwait(false);
                progress.Finish("RetryableFailure");
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
            progress.Finish(terminal ? "TerminalFailure" : "RetryableFailure");
        }
    }

    private static async Task<EllaiUtteranceOutcome> RunWorkAsync(
        EllaiUtteranceWork work,
        IServiceProvider scopedServices,
        CancellationToken stoppingToken, EllaiToolProgressPublisher progress)
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
            consentStore,
            CreateTextDeltaPublisher(work, scopedServices.GetRequiredService<EllaiSessionResponseStore>()),
            scopedServices.GetRequiredService<IEllaiUtteranceOutboxStore>(), progress.Observe,
            scopedServices.GetService<EllaiToolBroker>() is null ? null : async (context, request, ct) =>
            {
                // Independent reads must not share the worker's EF DbContext.
                await using var toolScope = scopedServices.CreateAsyncScope();
                var toolServices = toolScope.ServiceProvider;
                var current = await toolServices.GetRequiredService<IEllaiSessionStore>()
                    .GetAsync(work.SessionId, ct).ConfigureAwait(false);
                if (current is null || current.Status != "Active" || current.InteractionEpoch != work.ControllerEpoch
                    || current.OwnerActorId != work.Actor.ActorId || current.ScopeEpoch != work.ScopeEpoch)
                    return new EllaiToolResult(request.ToolName, false, "This request is no longer current.", [],
                        string.Empty, 0, false, EllaiToolReasonCodes.DeniedByPolicy);
                return await toolServices.GetRequiredService<EllaiToolBroker>()
                    .InvokeAsync(context, request, ct).ConfigureAwait(false);
            }, scopedServices.GetService<IEllaiLanguageClient>());
        return await EllaiUtteranceRunner.RunOneAsync(
            work, dependencies, MaxTokensPerTurn, stoppingToken).ConfigureAwait(false);
    }

    private static Func<string, CancellationToken, ValueTask>? CreateTextDeltaPublisher(
        EllaiUtteranceWork work, EllaiSessionResponseStore? responses)
    {
        if (responses is null || !EllaiResponsePublicationPolicy.CanPublish(work))
        {
            return null;
        }

        var chunkIndex = 0;
        return (text, _) =>
        {
            responses.PublishChunk(work.SessionId, work.ClientUtteranceId, ++chunkIndex, text, work.Languages?.EnglishVoiceId);
            return ValueTask.CompletedTask;
        };
    }
}
