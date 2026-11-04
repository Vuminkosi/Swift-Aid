using System.Globalization;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases;
using SwiftAid.Application.Intake.Imports;
using SwiftAid.Contracts.Cases;
using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>Rechecks retained case evidence before framework history is sent
/// to the model. Unrecognized or incomplete provenance is never authority.</summary>
public static class EllaiAuthorizedConversationContext
{
    public sealed record Result(IReadOnlyList<EllaiChatMessage> Messages, IReadOnlyList<string> RetainedTurnIds,
        IReadOnlyList<EllaiObservedToolResult> InheritedObservations)
    {
        public IReadOnlyList<string> UserOnlyTurnIds { get; init; } = [];

        public bool IsStillAuthorizedBy(Result current)
        {
            var currentIds = current.RetainedTurnIds.ToHashSet(StringComparer.Ordinal);
            var originalUserOnly = UserOnlyTurnIds.ToHashSet(StringComparer.Ordinal);
            var currentUserOnly = current.UserOnlyTurnIds.ToHashSet(StringComparer.Ordinal);
            return RetainedTurnIds.All(id => currentIds.Contains(id) &&
                (originalUserOnly.Contains(id) || !currentUserOnly.Contains(id)));
        }
    }

    public static async Task<Result> BuildAsync(
        IReadOnlyList<EllaiConversationTurn> conversation, string currentUtteranceId,
        ActorContext actor, ICaseQueueQuery? queue, CancellationToken ct, EllaiTechnicalDocs? docs = null,
        ICaseImportStore? imports = null, ICaseImportWorkspaceReader? importWorkspaces = null,
        IDashboardAggregateQuery? dashboard = null, EllaiToolContext? toolContext = null,
        Func<EllaiToolContext, EllaiToolRequest, CancellationToken, Task<EllaiToolResult>>? invokeTool = null)
    {
        var selected = EllaiConversationContext.SelectTurns(conversation, currentUtteranceId);
        var decisions = new Dictionary<(string Id, bool UserOnly), bool>();
        var completedById = conversation.Where(turn => turn.Outcome == "Completed")
            .ToLookup(turn => (turn.SessionId, turn.ClientUtteranceId));
        // A flattened trace can repeat the same source across several selected
        // turns. Share results only within this single authorization pass; the
        // next execution fence always performs fresh authority reads.
        var caseChecks = new Dictionary<(Guid Id, long Version), bool>();
        var importChecks = new Dictionary<(Guid Id, string Version), bool>();
        var countChecks = new Dictionary<EllaiCountReplayScope, bool>();
        var catalogueChecks = new Dictionary<EllaiUiCatalogueReplayScope, bool>();
        var receiptListChecks = new Dictionary<(string? ReceiptId, string Digest), (bool Allowed, IReadOnlyList<EvidenceRef> Evidence)>();

        async Task<bool> MayReplayAsync(EllaiConversationTurn turn, bool userOnly = false)
        {
            ct.ThrowIfCancellationRequested();
            var decisionKey = (turn.ClientUtteranceId, userOnly);
            if (decisions.TryGetValue(decisionKey, out var existing)) return existing;
            if (turn.ToolTraceJson is null || !EllaiToolTrace.TryRead(turn.ToolTraceJson, out var trace))
                return decisions[decisionKey] = false;
            if (trace!.Version == 2)
            {
                foreach (var id in trace.HistoryTurnIds!)
                {
                    var dependency = completedById[(turn.SessionId, id)].FirstOrDefault(item => item.Sequence < turn.Sequence);
                    if (dependency is null || !await MayReplayAsync(dependency, userOnly).ConfigureAwait(false))
                        return decisions[decisionKey] = false;
                }
            }
            else if (trace.Version == 1 && trace.HistoryIncluded)
            {
                // Version 1 records no dependency identities. A bounded read may
                // have lost some of them, so reconstruction cannot prove safety.
                return decisions[decisionKey] = false;
            }

            var references = new Dictionary<Guid, long>();
            foreach (var observation in trace.Observations)
            {
                if (!observation.Allowed || observation.Truncated && observation.RetainedCaseReplay is null &&
                    (observation.ToolName != EllaiToolNames.State || observation.CaseListQuery is null) ||
                    toolContext?.AllowedTools is { } allowedTools && !allowedTools.Contains(observation.ToolName))
                    return decisions[decisionKey] = false;
                if (observation.RetainedCaseReplay is { } retainedCase)
                {
                    if (!retainedCase.IsValid() || observation.Truncated && retainedCase.ClassificationFilter is null ||
                        observation.CaseSnapshotExpiresAtUtc is not { } expiry || expiry <= DateTimeOffset.UtcNow ||
                        toolContext is null || invokeTool is null || toolContext.SessionId != turn.SessionId ||
                        toolContext.Actor is null || toolContext.Actor.ActorId != actor.ActorId ||
                        toolContext.Actor.AccessEpoch != actor.AccessEpoch)
                        return decisions[decisionKey] = false;
                    try
                    {
                        var arguments = new Dictionary<string, string> { ["ref"] = retainedCase.Reference };
                        if (retainedCase.Field is not null) arguments["field"] = retainedCase.Field;
                        if (retainedCase.ClassificationFilter is not null)
                            arguments["classification_filter"] = retainedCase.ClassificationFilter;
                        var current = await invokeTool(toolContext with { RevalidationOnly = true },
                            new EllaiToolRequest(observation.ToolName, arguments), ct).ConfigureAwait(false);
                        ct.ThrowIfCancellationRequested();
                        if (current.ToolName != observation.ToolName || !current.Allowed || current.Truncated != observation.Truncated ||
                            current.DenialReason is not null || current.ReasonCode != EllaiToolReasonCodes.Ok ||
                            current.HistoryValidUntilUtc != expiry || current.ItemCount != observation.Evidence.Count ||
                            !current.Evidence.SequenceEqual(observation.Evidence) ||
                            EllaiObservedToolResult.CaseListFingerprint(current) != retainedCase.Digest)
                            return decisions[decisionKey] = false;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception error) when (error is not OutOfMemoryException) { return decisions[decisionKey] = false; }
                    continue;
                }
                // This validator returns only a format verdict about the model's
                // own input; it does not query a private data source.
                if (observation.ToolName == EllaiToolNames.ValidateObservations &&
                    observation.ReasonCode == EllaiToolReasonCodes.Ok && observation.Evidence.Count == 0)
                    continue;
                // A server clock reading is a historical observation, not current time or private data.
                if (observation.ToolName == EllaiToolNames.Now &&
                    observation.ClockVersion == EllaiObservedToolResult.CurrentClockVersion &&
                    observation.ReasonCode == EllaiToolReasonCodes.Ok && observation.Evidence.Count == 0)
                    continue;
                // This version marks only the broker's fixed, non-sensitive descriptor.
                if (observation.ToolName == EllaiToolNames.Capabilities &&
                    observation.CapabilityVersion == EllaiObservedToolResult.CurrentCapabilityVersion &&
                    observation.ReasonCode == EllaiToolReasonCodes.Ok && observation.Evidence.Count == 0)
                    continue;
                if (observation.ToolName == EllaiToolNames.OwnedCaseCount)
                {
                    var scope = observation.CountReplayScope;
                    if (dashboard is null || scope is null || !scope.IsValid() ||
                        observation.ReasonCode != EllaiToolReasonCodes.Ok || observation.Evidence.Count != 1 ||
                        observation.Evidence[0].SourceKind != "DashboardAggregate" ||
                        observation.Evidence[0].ResourceId != "owned-cases")
                        return decisions[decisionKey] = false;
                    try
                    {
                        if (!countChecks.TryGetValue(scope, out var allowed))
                        {
                            var current = await dashboard.GetAsync(actor, TimeSpan.FromDays(scope.WindowDays), ct).ConfigureAwait(false);
                            ct.ThrowIfCancellationRequested();
                            allowed = current.Access == DashboardAggregateAccess.Allowed && !current.Truncated &&
                                EllaiCountReplayScope.Create(scope.WindowDays, current.Rows.Select(row => row.CaseId)) == scope;
                            countChecks[scope] = allowed;
                        }
                        if (!allowed) return decisions[decisionKey] = false;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        return decisions[decisionKey] = false;
                    }
                    continue;
                }
                if (observation.ToolName == EllaiToolNames.UiCatalogue)
                {
                    var scope = observation.UiCatalogueReplayScope;
                    if (toolContext is null || invokeTool is null || scope is null || !scope.IsValid() ||
                        toolContext.SessionId != turn.SessionId || toolContext.Actor is null ||
                        toolContext.Actor.ActorId != actor.ActorId || toolContext.Actor.AccessEpoch != actor.AccessEpoch ||
                        observation.ReasonCode != EllaiToolReasonCodes.Ok)
                        return decisions[decisionKey] = false;
                    try
                    {
                        if (!catalogueChecks.TryGetValue(scope, out var allowed))
                        {
                            var arguments = new Dictionary<string, string>
                            {
                                ["feature"] = scope.Feature,
                                ["limit"] = scope.Limit.ToString(CultureInfo.InvariantCulture)
                            };
                            if (scope.GoalTag is not null) arguments["goal"] = scope.GoalTag;
                            var request = new EllaiToolRequest(EllaiToolNames.UiCatalogue, arguments);
                            var current = await invokeTool(toolContext, request, ct).ConfigureAwait(false);
                            ct.ThrowIfCancellationRequested();
                            allowed = current.Allowed && !current.Truncated && current.UiCatalogueReplayScope == scope;
                            catalogueChecks[scope] = allowed;
                        }
                        if (!allowed) return decisions[decisionKey] = false;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        return decisions[decisionKey] = false;
                    }
                    continue;
                }
                if (observation.ToolName == EllaiToolNames.UiObserve)
                {
                    var scope = observation.UiObservationReplayScope;
                    if (toolContext is null || invokeTool is null || scope is null || !scope.IsValid() ||
                        toolContext.SessionId != turn.SessionId || toolContext.Actor is null ||
                        toolContext.Actor.ActorId != actor.ActorId || toolContext.Actor.AccessEpoch != actor.AccessEpoch ||
                        observation.ReasonCode != EllaiToolReasonCodes.Ok)
                        return decisions[decisionKey] = false;
                    try
                    {
                        var current = await invokeTool(toolContext with { RevalidationOnly = true }, new EllaiToolRequest(
                            EllaiToolNames.UiObserve, new Dictionary<string, string>
                            {
                                ["feature"] = scope.Feature, ["controls"] = string.Join(",", scope.Controls), ["view"] = "current"
                            }), ct).ConfigureAwait(false);
                        ct.ThrowIfCancellationRequested();
                        if (!current.Allowed || current.DenialReason is not null || current.ReasonCode != EllaiToolReasonCodes.Ok ||
                            current.Truncated || current.Evidence.Count != scope.Controls.Count ||
                            !current.Evidence.Select(item => item.ResourceId).SequenceEqual(scope.Controls, StringComparer.Ordinal))
                            return decisions[decisionKey] = false;
                        if (!userOnly && !scope.PresentationMayChange)
                        {
                            var replay = current.UiObservationReplayScope;
                            if (!current.Evidence.SequenceEqual(observation.Evidence) || replay is null || !replay.IsValid() ||
                                replay.Feature != scope.Feature || replay.Digest != scope.Digest ||
                                !replay.Controls.SequenceEqual(scope.Controls, StringComparer.Ordinal))
                                return decisions[decisionKey] = false;
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        return decisions[decisionKey] = false;
                    }
                    continue;
                }
                if (observation.ToolName == EllaiToolNames.State)
                {
                    if (observation.StaticFeatureVersion == EllaiObservedToolResult.CurrentStaticFeatureVersion &&
                        observation.ReasonCode == EllaiToolReasonCodes.Ok && observation.Evidence.Count == 0)
                        continue;
                    var query = observation.CaseListQuery;
                    if (queue is null || query is null || !query.IsValid() ||
                        observation.QueueProjectionVersion != EllaiObservedToolResult.CurrentQueueProjectionVersion)
                        return decisions[decisionKey] = false;
                    if (query.PlaceId is not null)
                    {
                        if (toolContext is null || invokeTool is null || toolContext.SessionId != turn.SessionId ||
                            toolContext.Actor is null || toolContext.Actor.ActorId != actor.ActorId ||
                            toolContext.Actor.AccessEpoch != actor.AccessEpoch)
                            return decisions[decisionKey] = false;
                        try
                        {
                            var request = new EllaiToolRequest(EllaiToolNames.State, new Dictionary<string, string>
                            {
                                ["projection"] = "queue", ["limit"] = query.Limit.ToString(CultureInfo.InvariantCulture),
                                ["country"] = query.Country!, ["sort"] = "latest-reported", ["place_id"] = query.PlaceId
                            });
                            var current = await invokeTool(toolContext with { RevalidationOnly = true }, request, ct).ConfigureAwait(false);
                            ct.ThrowIfCancellationRequested();
                            if (!current.Allowed || current.Truncated != observation.Truncated || current.ReasonCode != EllaiToolReasonCodes.Ok ||
                                current.ItemCount != observation.Evidence.Count || !current.Evidence.SequenceEqual(observation.Evidence) ||
                                (observation.Truncated && observation.CaseListDigest is null) ||
                                (observation.CaseListDigest is not null && observation.CaseListDigest != EllaiObservedToolResult.CaseListFingerprint(current)))
                                return decisions[decisionKey] = false;
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception error) when (error is not OutOfMemoryException) { return decisions[decisionKey] = false; }
                        continue;
                    }
                    try
                    {
                        var current = query.Country is null && query.Workflow is null && query.Sort is null
                            ? await queue.GetMineAsync(actor, query.Limit, ct).ConfigureAwait(false)
                            : await queue.GetMineAsync(actor, query.Limit,
                                new CaseQueueFilter(query.Country, query.Workflow, query.Sort == "latest-reported"), ct).ConfigureAwait(false);
                        ct.ThrowIfCancellationRequested();
                        var items = current.Items.Take(query.Limit).ToArray();
                        if (current.Access != CaseQueueAccess.Allowed ||
                            (current.NextCursor is not null || current.Items.Count > query.Limit) != observation.Truncated ||
                            items.Length != observation.Evidence.Count ||
                            items.Where((item, index) => observation.Evidence[index] != new EvidenceRef("CaseQueue",
                                item.CaseId.ToString("D"), item.AggregateVersion.ToString(CultureInfo.InvariantCulture), null,
                                item.CaseId.ToString("D"))).Any())
                            return decisions[decisionKey] = false;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        return decisions[decisionKey] = false;
                    }
                    continue;
                }
                if (observation.ToolName == EllaiToolNames.Imports)
                {
                    var scope = observation.ImportListScope;
                    if (importWorkspaces is null || scope is null || !scope.IsValid())
                        return decisions[decisionKey] = false;
                    try
                    {
                        var current = await importWorkspaces.GetRecentAsync(actor, scope.WorkspaceId, scope.CountryCode, ct, scope.OrganizationId).ConfigureAwait(false);
                        ct.ThrowIfCancellationRequested();
                        if (current.Count != observation.Evidence.Count || current.Count >= 20 ||
                            current.Where((item, index) => item.State is not ("Completed" or "Failed") ||
                                item.WorkspaceId != scope.WorkspaceId || item.CountryCode != scope.CountryCode ||
                                observation.Evidence[index] != new EvidenceRef("ImportJob", item.ImportId.ToString("D"),
                                    item.UpdatedAtUtc.ToString("O", CultureInfo.InvariantCulture), "status", item.ImportId.ToString("D"))).Any())
                            return decisions[decisionKey] = false;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        return decisions[decisionKey] = false;
                    }
                    continue;
                }
                if (observation.ToolName == EllaiToolNames.Receipts)
                {
                    var digest = observation.ReceiptListDigest;
                    if (digest is null || observation.ReasonCode != EllaiToolReasonCodes.Ok ||
                        toolContext is null || invokeTool is null || toolContext.SessionId != turn.SessionId ||
                        toolContext.Actor is null || toolContext.Actor.ActorId != actor.ActorId ||
                        toolContext.Actor.AccessEpoch != actor.AccessEpoch)
                        return decisions[decisionKey] = false;
                    try
                    {
                        var key = (observation.ReceiptLookupId, digest);
                        if (!receiptListChecks.TryGetValue(key, out var check))
                        {
                            var arguments = observation.ReceiptLookupId is null ? new Dictionary<string, string>() :
                                new Dictionary<string, string> { ["receipt"] = observation.ReceiptLookupId };
                            var request = new EllaiToolRequest(EllaiToolNames.Receipts, arguments);
                            var current = await invokeTool(toolContext, request, ct).ConfigureAwait(false);
                            ct.ThrowIfCancellationRequested();
                            var allowed = current.ToolName == EllaiToolNames.Receipts && current.Allowed &&
                                current.DenialReason is null && current.ReasonCode == EllaiToolReasonCodes.Ok &&
                                !current.Truncated && current.ItemCount == current.Evidence.Count && current.Evidence.Count <= 20 &&
                                EllaiObservedToolResult.ReceiptListFingerprint(current) == digest;
                            check = (allowed, current.Evidence.ToArray());
                            receiptListChecks[key] = check;
                        }
                        if (!check.Allowed || !observation.Evidence.SequenceEqual(check.Evidence))
                            return decisions[decisionKey] = false;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        return decisions[decisionKey] = false;
                    }
                    continue;
                }
                if (observation.Evidence.Count == 0 &&
                    (observation.ToolName != EllaiToolNames.Evidence || observation.RetainedCaseReplay?.ClassificationFilter is null))
                    return decisions[decisionKey] = false;
                if (observation.ToolName == EllaiToolNames.ImportStatus)
                {
                    if (imports is null || (observation.ToolName == EllaiToolNames.ImportStatus && observation.Evidence.Count != 1))
                        return decisions[decisionKey] = false;
                    try
                    {
                        foreach (var reference in observation.Evidence)
                        {
                            if (reference.SourceKind != "ImportJob" || reference.FieldPath != "status" ||
                                reference.EvidenceId != reference.ResourceId ||
                                !Guid.TryParseExact(reference.ResourceId, "D", out var id) || id == Guid.Empty)
                                return decisions[decisionKey] = false;
                            var key = (id, reference.Version);
                            if (!importChecks.TryGetValue(key, out var allowed))
                            {
                                var current = await imports.GetAsync(actor, id, ct).ConfigureAwait(false);
                                ct.ThrowIfCancellationRequested();
                                // A lease may expire without updating the timestamp.
                                allowed = current is not null && current.ImportId == id && current.State is "Completed" or "Failed" &&
                                    current.UpdatedAtUtc.ToString("O", CultureInfo.InvariantCulture) == reference.Version;
                                importChecks[key] = allowed;
                            }
                            if (!allowed)
                                return decisions[decisionKey] = false;
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        return decisions[decisionKey] = false;
                    }
                    continue;
                }
                if (observation.ToolName is EllaiToolNames.DocsSearch or EllaiToolNames.DocsGet)
                {
                    if (docs is null) return decisions[decisionKey] = false;
                    try
                    {
                        if (!docs.IsAvailable || observation.Evidence.Any(reference =>
                        {
                            if (reference.SourceKind != "Documentation" || reference.FieldPath is not null ||
                                reference.EvidenceId != reference.ResourceId) return true;
                            var section = docs.Get(reference.ResourceId);
                            return section is null || section.DocumentId != EllaiTechnicalDocs.DocumentId ||
                                section.SectionId != reference.ResourceId || section.Revision != reference.Version;
                        })) return decisions[decisionKey] = false;
                    }
                    catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
                    {
                        return decisions[decisionKey] = false;
                    }
                    continue;
                }
                if (observation.ToolName == EllaiToolNames.IngestionSchema)
                {
                    if (observation.Evidence.Count != 1 || observation.Evidence[0] != EllaiToolBroker.IngestionSchemaEvidence)
                        return decisions[decisionKey] = false;
                    continue;
                }
                if (observation.ToolName == EllaiToolNames.ProductFacts)
                {
                    var facts = new EllaiProductFactCatalog().Find(null);
                    if (observation.Evidence.Any(reference => reference.SourceKind != "ProductFact" ||
                        !facts.Any(fact => fact.FactId == reference.ResourceId && fact.Revision == reference.Version &&
                            fact.SourceId == reference.FieldPath && fact.FactId == reference.EvidenceId)))
                        return decisions[decisionKey] = false;
                    continue;
                }
                if (observation.ToolName is not (EllaiToolNames.Topic or EllaiToolNames.Evidence))
                    return decisions[decisionKey] = false;
                if (observation.CaseSnapshotExpiresAtUtc is not { } expires || expires <= DateTimeOffset.UtcNow)
                    return decisions[decisionKey] = false;
                foreach (var reference in observation.Evidence)
                {
                    if (reference.SourceKind != "CaseQueue" || reference.FieldPath == "classification" ||
                        !Guid.TryParse(reference.ResourceId, out var id) || id == Guid.Empty ||
                        !long.TryParse(reference.Version, NumberStyles.None, CultureInfo.InvariantCulture, out var version) ||
                        version < 0 || (references.TryGetValue(id, out var previous) && previous != version))
                        return decisions[decisionKey] = false;
                    references[id] = version;
                }
            }
            if (references.Count > 0)
            {
                if (queue is null) return decisions[decisionKey] = false;
                try
                {
                    var uncheckedIds = references.Where(pair => !caseChecks.ContainsKey((pair.Key, pair.Value)))
                        .Select(pair => pair.Key).ToArray();
                    foreach (var batch in uncheckedIds.Chunk(20))
                    {
                        var result = await queue.GetMineAsync(actor, 20, new CaseQueueFilter(CaseIds: batch), ct).ConfigureAwait(false);
                        ct.ThrowIfCancellationRequested();
                        foreach (var id in batch)
                            caseChecks[(id, references[id])] = result.Access == CaseQueueAccess.Allowed && result.NextCursor is null &&
                                result.Items.Any(item => item.CaseId == id && item.AggregateVersion == references[id]);
                    }
                    if (references.Any(pair => !caseChecks[(pair.Key, pair.Value)]))
                        return decisions[decisionKey] = false;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    return decisions[decisionKey] = false;
                }
            }
            return decisions[decisionKey] = true;
        }

        var messages = new List<EllaiChatMessage>();
        var retained = new List<string>();
        var retainedSet = new HashSet<string>(StringComparer.Ordinal);
        var userOnlyIds = new HashSet<string>(StringComparer.Ordinal);
        var omitted = false;
        foreach (var turn in selected)
        {
            if (!await MayReplayAsync(turn).ConfigureAwait(false))
            {
                if (!await MayReplayAsync(turn, userOnly: true).ConfigureAwait(false)) { omitted = true; continue; }
                messages.Add(new("user", turn.UserText!));
                messages.Add(new("assistant", "The earlier screen state has changed or expired. Check the current screen before relying on it."));
                retained.Add(turn.ClientUtteranceId);
                retainedSet.Add(turn.ClientUtteranceId);
                userOnlyIds.Add(turn.ClientUtteranceId);
                continue;
            }
            messages.Add(new("user", turn.UserText!));
            messages.Add(new("assistant", turn.AssistantText!));
            retained.Add(turn.ClientUtteranceId);
            retainedSet.Add(turn.ClientUtteranceId);
        }
        if (omitted)
            messages.Add(new("assistant", "Some earlier conversation is unavailable because its current authorization could not be verified. Ask for clarification when needed."));

        // Only copy provenance from turns that passed the checks above. Version 3
        // already contains its complete inherited observations; older version 2
        // traces must still follow their identities while those rows are available.
        var inherited = new List<EllaiObservedToolResult>();
        var collected = new HashSet<(string Id, bool UserOnly)>();
        void Collect(EllaiConversationTurn first, bool userOnly)
        {
            var pending = new Stack<EllaiConversationTurn>();
            pending.Push(first);
            while (pending.TryPop(out var turn))
            {
                ct.ThrowIfCancellationRequested();
                if (!collected.Add((turn.ClientUtteranceId, userOnly))) continue;
                EllaiToolTrace.TryRead(turn.ToolTraceJson!, out var trace);
                inherited.AddRange(trace!.Observations.Select(observation => userOnly && observation.UiObservationReplayScope is { } scope
                    ? observation with { UiObservationReplayScope = scope with { PresentationMayChange = true } } : observation));
                if (trace.Version != 2) continue;
                foreach (var id in trace.HistoryTurnIds!.Reverse())
                    pending.Push(completedById[(turn.SessionId, id)].First(item => item.Sequence < turn.Sequence));
            }
        }
        foreach (var turn in selected.Where(turn => retainedSet.Contains(turn.ClientUtteranceId)))
            Collect(turn, userOnlyIds.Contains(turn.ClientUtteranceId));
        ct.ThrowIfCancellationRequested();
        return new(messages, retained, inherited) { UserOnlyTurnIds = userOnlyIds.ToArray() };
    }
}
