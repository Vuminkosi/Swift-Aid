using SwiftAid.Application.Ellai;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Gateway.Ellai;

/// <summary>Executes accepted Ellai work from the PostgreSQL-backed outbox.
/// A durable claim survives process restarts; leases recover abandoned work,
/// and the store serializes claims within a session.</summary>
public sealed class EllaiUtteranceWorker(
    IServiceProvider services,
    ILogger<EllaiUtteranceWorker> logger,
    TimeSpan? leaseRenewalInterval = null) : BackgroundService
{
    public const int MaxTokensPerTurn = 2048;
    private static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan IdlePollDelay = TimeSpan.FromSeconds(1);
    private static readonly EllaiIsolatedToolCalls ToolCalls = new();
    private static readonly EllaiIsolatedToolCalls ReferenceCalls = new();
    private static readonly EllaiIsolatedToolCalls StateCalls = new();

    private readonly IServiceProvider _services =
        services ?? throw new ArgumentNullException(nameof(services));
    private readonly ILogger<EllaiUtteranceWorker> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly TimeSpan _leaseRenewalInterval = leaseRenewalInterval is null ? TimeSpan.FromMinutes(2)
        : leaseRenewalInterval > TimeSpan.Zero && leaseRenewalInterval < ClaimLease
            ? leaseRenewalInterval.Value : throw new ArgumentOutOfRangeException(nameof(leaseRenewalInterval));

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

        var responseStore = scopedServices.GetRequiredService<EllaiSessionResponseStore>();
        var responseGeneration = responseStore.CaptureGeneration(work.SessionId);
        var progress = new EllaiToolProgressPublisher(
            responseStore, work.SessionId,
            item.ClientUtteranceId, work.ControllerEpoch, item.AttemptCount,
            englishVoiceId: work.Languages?.EnglishVoiceId);
        var answerChunks = new List<string>();
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

            if (session.CyclesUsed > session.CyclesAllowed)
                _logger.LogWarning("Ellai session {SessionId} exceeded its {CycleThreshold} turn threshold; execution will continue.",
                    work.SessionId, session.CyclesAllowed);

            var started = System.Diagnostics.Stopwatch.StartNew();
            var (committed, providerElapsed) = await RunWithLeaseAsync(item, outbox, stoppingToken, async token =>
            {
                var runOutcome = await RunWorkAsync(work, item, scopedServices, responseStore, responseGeneration,
                    token, progress, answerChunks).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (!runOutcome.Completed)
                {
                    _logger.LogWarning("Ellai turn {SessionId}/{UtteranceId} stopped with {FailureCode}.",
                        work.SessionId, item.ClientUtteranceId, runOutcome.FailureCode ?? "AdmissionDenied");
                    if (runOutcome.FailureCode == "InvalidDecision")
                        _logger.LogWarning("Ellai decision shape rejected: {Diagnostic}", runOutcome.StopReason);
                }
                var resolution = runOutcome.Completed && !string.IsNullOrWhiteSpace(runOutcome.DisplayText)
                    && EllaiResponsePublicationPolicy.CanPublish(work)
                    ? EllaiUtteranceResolution.Completed(runOutcome.DisplayText, work.ControllerEpoch) with
                    {
                        UiProposalsJson = EllaiUiProposalPayload.Serialize(runOutcome.Decision?.UiIntents ?? []),
                        FrameworkSessionJson = runOutcome.FrameworkSessionJson,
                        ExpectedOwnerActorId = work.Actor.ActorId,
                        ExpectedScopeEpoch = work.ScopeEpoch,
                        ToolTraceJson = runOutcome.InheritedObservations is null
                            ? EllaiToolTrace.Serialize(runOutcome.ObservedTools, runOutcome.HistoryIncluded, runOutcome.HistoryTurnIds)
                            : EllaiToolTrace.SerializeFlattened(runOutcome.ObservedTools, runOutcome.HistoryIncluded, runOutcome.InheritedObservations)
                    }
                    : EllaiUtteranceResolution.Failed with { ExpectedInteractionEpoch = work.ControllerEpoch };
                var elapsed = started.Elapsed.TotalMilliseconds;
                token.ThrowIfCancellationRequested();
                var persisted = await outbox.CompleteAsync(item.SessionId, item.ClientUtteranceId, item.AttemptCount,
                    DateTimeOffset.UtcNow, token, resolution).ConfigureAwait(false);
                return (persisted, elapsed);
            }).ConfigureAwait(false);
            EllaiPipelineMetrics.Record("outcome_commit", started.Elapsed.TotalMilliseconds - providerElapsed);
            if (committed is not null)
            {
                try
                {
                    using var publication = EllaiPipelineMetrics.Measure("publication");
                    // Transient delivery is only a hint; the owner can recover the committed outcome
                    // from PostgreSQL even when this process exits before this publication.
                    // A cancellation on a different Gateway does not clear this process's
                    // in-memory feed. Re-read the durable session before publishing.
                    var current = await sessions.GetAsync(work.SessionId, stoppingToken).ConfigureAwait(false);
                    if (committed.Outcome == "Completed" &&
                        (current is null || current.Status != "Active" || current.InteractionEpoch != work.ControllerEpoch ||
                         current.OwnerActorId != work.Actor.ActorId || current.ScopeEpoch != work.ScopeEpoch))
                    {
                        _logger.LogInformation("Skipping stale Ellai publication for session {SessionId}.", work.SessionId);
                        return;
                    }
                    var chunks = committed.Outcome == "Completed" && answerChunks.Count > 0 &&
                        string.Equals(string.Join(" ", answerChunks.Select(chunk => chunk.Trim())), committed.AssistantText?.Trim(), StringComparison.Ordinal)
                        ? answerChunks : [];
                    if (responseStore.PublishCommitted(work.SessionId, responseGeneration, item.ClientUtteranceId,
                        committed, chunks, work.Languages?.OutputLanguage ?? "eng", work.Languages?.EnglishVoiceId))
                        progress.Finish(committed.Outcome);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _logger.LogWarning(ex, "Ellai outcome was committed but transient publication failed for session {SessionId}.", work.SessionId);
                }
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

    private async Task<T> RunWithLeaseAsync<T>(EllaiUtteranceOutboxItem item, IEllaiUtteranceOutboxStore outbox,
        CancellationToken stoppingToken, Func<CancellationToken, Task<T>> run)
    {
        if (outbox is not IEllaiUtteranceLeaseRenewal)
            return await run(stoppingToken).ConfigureAwait(false);

        using var execution = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var renewal = RenewLeaseLoopAsync(item, execution);
        try
        {
            return await run(execution.Token).ConfigureAwait(false);
        }
        finally
        {
            await execution.CancelAsync().ConfigureAwait(false);
            await renewal.ConfigureAwait(false);
        }
    }

    private async Task RenewLeaseLoopAsync(EllaiUtteranceOutboxItem item, CancellationTokenSource execution)
    {
        try
        {
            while (true)
            {
                await Task.Delay(_leaseRenewalInterval, execution.Token).ConfigureAwait(false);
                await using var scope = _services.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<IEllaiUtteranceOutboxStore>();
                if (store is not IEllaiUtteranceLeaseRenewal renewal)
                    throw new InvalidOperationException("Ellai lease renewal is unavailable in a new scope.");
                var now = DateTimeOffset.UtcNow;
                if (!await renewal.RenewLeaseAsync(item.SessionId, item.ClientUtteranceId, item.AttemptCount,
                    now, now.Add(ClaimLease), execution.Token).ConfigureAwait(false))
                    throw new InvalidOperationException("Ellai outbox claim is no longer renewable.");
            }
        }
        catch (OperationCanceledException) when (execution.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Ellai lease renewal failed for session {SessionId}; stopping this attempt.", item.SessionId);
            await execution.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task<EllaiUtteranceOutcome> RunWorkAsync(
        EllaiUtteranceWork work, EllaiUtteranceOutboxItem item,
        IServiceProvider scopedServices, EllaiSessionResponseStore responseStore, Guid responseGeneration,
        CancellationToken stoppingToken, EllaiToolProgressPublisher progress, List<string> answerChunks)
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

        var scopedBroker = scopedServices.GetService<EllaiToolBroker>();
        var registeredQueue = scopedServices.GetService<SwiftAid.Application.Cases.ICaseQueueQuery>();
        var registeredTopics = scopedServices.GetService<IEllaiTopicStore>();
        var scopedSessions = scopedServices.GetRequiredService<IEllaiSessionStore>();
        var canFenceQueue = scopedBroker?.UsesStateQueue(registeredQueue) == true &&
            scopedBroker.UsesTopicStore(registeredTopics) &&
            scopedSessions is IEllaiCaseResultSnapshotFence scopedFence && scopedFence.CanFenceTopicStore(registeredTopics);
        var authorizationQueue = scopedBroker?.UsesStateQueue(registeredQueue) == true
            ? new EllaiIsolatedCaseQueueQuery(_services, ToolCalls)
            : scopedBroker?.StateQueue;
        var dependencies = new EllaiUtteranceServices(
            scopedServices.GetRequiredService<IEllaiReasoningExecutor>(),
            scopedServices.GetService<EllaiActivityStatusCoordinator>(),
            scopedServices.GetService<EllaiSessionEventJournal>(),
            consent,
            scopedServices.GetRequiredService<IEllaiSessionStore>(),
            consentStore,
            EllaiResponsePublicationPolicy.CanPublish(work)
                ? (text, ct) => { ct.ThrowIfCancellationRequested(); answerChunks.Add(text); return ValueTask.CompletedTask; }
                : null,
            scopedServices.GetRequiredService<IEllaiUtteranceOutboxStore>(), progress.Observe,
            scopedBroker is null ? null : (context, request, ct) =>
            {
                var detachQueue = canFenceQueue && IsFencedQueueRequest(request);
                async Task<EllaiToolResult> InvokeInScopeAsync(CancellationToken toolCt)
                {
                    // Independent reads must not share the worker's EF DbContext.
                    await using var toolScope = _services.CreateAsyncScope();
                    var toolServices = toolScope.ServiceProvider;
                    var sessionStore = toolServices.GetRequiredService<IEllaiSessionStore>();
                    var current = await sessionStore.GetAsync(work.SessionId, toolCt).ConfigureAwait(false);
                    toolCt.ThrowIfCancellationRequested();
                    if (current is null || current.Status != "Active" || current.InteractionEpoch != work.ControllerEpoch
                        || current.OwnerActorId != work.Actor.ActorId || current.ScopeEpoch != work.ScopeEpoch)
                        return new EllaiToolResult(request.ToolName, false, "This request is no longer current.", [],
                            string.Empty, 0, false, EllaiToolReasonCodes.DeniedByPolicy);
                    var broker = toolServices.GetRequiredService<EllaiToolBroker>();
                    var queue = request.ToolName == EllaiToolNames.State && broker.UsesStateQueue(
                        toolServices.GetService<SwiftAid.Application.Cases.ICaseQueueQuery>())
                        ? new EllaiIsolatedCaseQueueQuery(_services, ToolCalls) : null;
                    var index = request.ToolName is EllaiToolNames.Topic or EllaiToolNames.Evidence &&
                        broker.UsesReferenceIndex(toolServices.GetService<IEllaiExactReferenceIndex>())
                        ? new EllaiIsolatedExactReferenceIndex(_services, ReferenceCalls) : null;
                    var registry = request.ToolName == EllaiToolNames.Locations
                        ? toolServices.GetService<SwiftAid.Application.Intake.IGeographicRegistry>() : null;
                    var geography = request.ToolName == EllaiToolNames.Locations &&
                        registry is SwiftAid.Infrastructure.Intake.Geography.GeographicRegistryService &&
                        broker.UsesGeographicRegistry(registry)
                        ? new EllaiIsolatedGeographicRegistry(_services, ToolCalls, registry) : null;
                    var topicStore = toolServices.GetService<IEllaiTopicStore>();
                    Func<EllaiToolContext, IReadOnlyList<SwiftAid.Contracts.Cases.CaseQueueItem>, CancellationToken, Task<bool>>?
                        remember = sessionStore is IEllaiCaseResultSnapshotFence fence &&
                            broker.UsesTopicStore(topicStore) && fence.CanFenceTopicStore(topicStore)
                            ? (toolContext, items, token) =>
                            {
                                if (toolContext.SessionId != work.SessionId || toolContext.ScopeEpoch != work.ScopeEpoch ||
                                    toolContext.Actor?.ActorId != work.Actor.ActorId)
                                    return Task.FromResult(false);
                                return fence.StoreCaseResultsIfCurrentAsync(work.SessionId, item.ClientUtteranceId,
                                    item.AttemptCount, work.Actor.ActorId, work.ScopeEpoch, work.ControllerEpoch,
                                    EllaiCaseResultMemory.SerializeSnapshot(toolContext, items), token, toolContext.ToolDeadlineUtc);
                            }
                            : null;
                    // A new scope must satisfy the same safety checks before a
                    // detached operation can perform the queue or snapshot work.
                    if (detachQueue && (queue is null || remember is null))
                        return new EllaiToolResult(request.ToolName, false, "The queue is unavailable.", [],
                            string.Empty, 0, false, EllaiToolReasonCodes.Unavailable);
                    return await broker.InvokeAsync(context, request, toolCt, queue, index, remember, geography).ConfigureAwait(false);
                }

                return detachQueue
                    ? StateCalls.RunAsync(InvokeInScopeAsync, ct)
                    : InvokeToolWithIsolation(request, InvokeInScopeAsync, ToolCalls, ct);
            }, scopedServices.GetService<IEllaiLanguageClient>(),
            authorizationQueue,
            scopedServices.GetService<EllaiTechnicalDocs>(),
            scopedServices.GetService<SwiftAid.Application.Intake.Imports.ICaseImportStore>(),
            scopedServices.GetService<SwiftAid.Application.Intake.Imports.ICaseImportWorkspaceReader>(),
            scopedServices.GetService<SwiftAid.Application.Cases.IDashboardAggregateQuery>(),
            CreateTextDeltaCollector(work, item, responseStore, responseGeneration, answerChunks),
            scopedServices.GetService<IEllaiLocationReplayProtector>());
        return await EllaiUtteranceRunner.RunOneAsync(
            work, dependencies, MaxTokensPerTurn, stoppingToken).ConfigureAwait(false);
    }

    internal static Task<T> InvokeToolWithIsolation<T>(EllaiToolRequest request,
        Func<CancellationToken, Task<T>> invoke, EllaiIsolatedToolCalls calls, CancellationToken ct) =>
        CanDetachReadOnlyTool(request) ? calls.RunAsync(invoke, ct) : invoke(ct);

    internal static bool IsFencedQueueRequest(EllaiToolRequest request) =>
        request.ToolName == EllaiToolNames.State &&
        request.Arguments.TryGetValue("projection", out var projection) &&
        string.Equals(projection, "queue", StringComparison.OrdinalIgnoreCase) &&
        request.Arguments.Keys.All(key => key is "projection" or "limit" or "country" or "workflow" or "sort");

    internal static bool CanDetachReadOnlyTool(EllaiToolRequest request) =>
        // The static feature projection has no session-memory write. Queue
        // projections may persist case references and must remain attached.
        request.ToolName == EllaiToolNames.State
            ? request.Arguments.Count == 0 ||
                (request.Arguments.Count == 1 && request.Arguments.TryGetValue("projection", out var projection) &&
                    string.Equals(projection, "feature", StringComparison.OrdinalIgnoreCase))
            : request.ToolName is EllaiToolNames.Topic or EllaiToolNames.Evidence or EllaiToolNames.ClassificationEvidence or
                EllaiToolNames.OwnedCaseCount or EllaiToolNames.OwnedRegionCounts or EllaiToolNames.Receipts or EllaiToolNames.ImportDestinations or
                EllaiToolNames.Imports or EllaiToolNames.ImportStatus or
                EllaiToolNames.UiCatalogue or EllaiToolNames.UiObserve or
                EllaiToolNames.Now or EllaiToolNames.Capabilities or
                EllaiToolNames.ProductFacts or EllaiToolNames.DocsSearch or EllaiToolNames.DocsGet or
                EllaiToolNames.IngestionSchema or EllaiToolNames.ValidateObservations;

    private Func<string, CancellationToken, ValueTask>? CreateTextDeltaCollector(
        EllaiUtteranceWork work, EllaiUtteranceOutboxItem item, EllaiSessionResponseStore responseStore,
        Guid responseGeneration, List<string> answerChunks)
    {
        if (!EllaiResponsePublicationPolicy.CanPublish(work))
        {
            return null;
        }

        return async (text, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            answerChunks.Add(text);
            await using var scope = _services.CreateAsyncScope();
            var outbox = scope.ServiceProvider.GetRequiredService<IEllaiUtteranceOutboxStore>();
            if (outbox is not IEllaiUtteranceLeaseRenewal lease) return;
            var now = DateTimeOffset.UtcNow;
            if (!await lease.RenewLeaseAsync(item.SessionId, item.ClientUtteranceId, item.AttemptCount,
                    now, now.Add(ClaimLease), ct).ConfigureAwait(false)) return;
            ct.ThrowIfCancellationRequested();
            if (outbox is not IEllaiProvisionalPublicationFence fence) return;
            await fence.PublishIfCurrentAsync(item.SessionId, item.ClientUtteranceId, item.AttemptCount,
                work.Actor.ActorId, work.ScopeEpoch, work.ControllerEpoch,
                () => responseStore.PublishProvisional(work.SessionId, responseGeneration, item.ClientUtteranceId,
                    item.AttemptCount, work.ScopeEpoch, work.ControllerEpoch, answerChunks.Count, text, work.Languages?.EnglishVoiceId), ct).ConfigureAwait(false);
        };
    }
}
