using System.Collections.Immutable;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Contracts.Cases;

namespace SwiftAid.Application.Cases.Correlation;

public sealed record StoredCorrelationCandidate(Guid CandidateId, Guid FirstCaseId, Guid SecondCaseId,
    long FirstRevision, long SecondRevision, DateTimeOffset RecordedAtUtc, string[] Reasons, string? Decision = null);
public sealed record CorrelationScanRow(Guid CandidateId, Guid FirstCaseId, Guid SecondCaseId,
    long FirstRevision, long SecondRevision, DateTimeOffset RecordedAtUtc, string[] Reasons, string? Decision, DateTimeOffset SortTimeUtc);
public sealed record CorrelationCursor(DateTimeOffset SortTimeUtc, Guid CandidateId);

public interface ICaseCorrelationStore
{
    Task<IReadOnlyList<StoredCorrelationCandidate>> GetCandidatesAsync(Guid caseId, int limit, CancellationToken ct);
    Task<bool> AreCurrentAsync(StoredCorrelationCandidate candidate, CancellationToken ct);
    Task<IReadOnlyList<CorrelationScanRow>> GetAfterAsync(Guid caseId, CorrelationCursor? cursor, int limit, CancellationToken ct);
}

/// <summary>Returns only candidates for which both current case scopes grant ReadCase.</summary>
public sealed class CaseCorrelationQuery(ICaseCorrelationStore store, ICaseCommandUnitOfWork unitOfWork,
    ICaseAccessPolicy policy, IEllaiCaseAuthScopeFactory scopes)
{
    public async Task<CaseCorrelationPage> GetPageAsync(ActorContext actor, Guid caseId, string? cursor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var position = Decode(cursor);
        if (caseId == Guid.Empty) return new(false, [], null);
        ICaseCommandLease source;
        try { source = await unitOfWork.BeginCaseAsync(actor, caseId, [], CaseLeaseMode.Read, ct); }
        catch (KeyNotFoundException) { return new(false, [], null); }
        catch (InvalidOperationException error) when (error.Message is "The case authority scope is unavailable." or "The current case authority scope is unavailable.")
        { return new(false, [], null); }
        await using (source.ConfigureAwait(false))
        {
            if (!(await policy.AuthorizeAsync(actor, caseId, CaseOperation.ReadCase, ct)).IsAllowed) return new(false, [], null);
            const int pageSize = 20, batchSize = 50, scanLimit = 1000;
            var visible = new List<CaseCorrelationView>(pageSize);
            CorrelationCursor? lastVisible = null;
            var scanned = 0;
            while (scanned < scanLimit)
            {
                var rows = await store.GetAfterAsync(caseId, position, batchSize, ct);
                foreach (var row in rows)
                {
                    scanned++;
                    position = new(row.SortTimeUtc, row.CandidateId);
                    var other = row.FirstCaseId == caseId ? row.SecondCaseId : row.FirstCaseId;
                    await using var scope = scopes.CreateScope();
                    ICaseCommandLease target;
                    try { target = await scope.UnitOfWork.BeginCaseAsync(actor, other, [], CaseLeaseMode.Read, ct); }
                    catch (KeyNotFoundException) { continue; }
                    catch (InvalidOperationException error) when (error.Message is "The case authority scope is unavailable." or "The current case authority scope is unavailable.") { continue; }
                    await using (target.ConfigureAwait(false))
                    {
                        var candidate = new StoredCorrelationCandidate(row.CandidateId, row.FirstCaseId, row.SecondCaseId,
                            row.FirstRevision, row.SecondRevision, row.RecordedAtUtc, row.Reasons, row.Decision);
                        if (!(await scope.Policy.AuthorizeAsync(actor, other, CaseOperation.ReadCase, ct)).IsAllowed || !await store.AreCurrentAsync(candidate, ct)) continue;
                        await target.CommitAsync(ct);
                        if (visible.Count == pageSize)
                        {
                            await source.CommitAsync(ct);
                            return new(true, visible, Encode(lastVisible!));
                        }
                        visible.Add(new(row.CandidateId, other, row.RecordedAtUtc, row.Reasons,
                            "Possible relationship only; shared attributes do not establish identity or causation.",
                            Enum.TryParse<OrganizationDecision>(row.Decision, false, out var reviewed) && Enum.IsDefined(reviewed) ? reviewed : null));
                        lastVisible = position;
                    }
                }
                if (rows.Count < batchSize) { await source.CommitAsync(ct); return new(true, visible, null); }
            }
            // Do not expose whether inaccessible rows remain or use one as a cursor.
            throw new InvalidOperationException("The candidate scan limit was reached.");
        }
    }

    private static string Encode(CorrelationCursor cursor) => $"c1.{cursor.SortTimeUtc.UtcDateTime.Ticks:x}.{cursor.CandidateId:N}";

    private static CorrelationCursor? Decode(string? cursor)
    {
        if (cursor is null) return null;
        var parts = cursor.Split('.');
        if (cursor.Length > 60 || parts.Length != 3 || parts[0] != "c1" ||
            !long.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var ticks) ||
            ticks < 0 || ticks > DateTime.MaxValue.Ticks || !Guid.TryParseExact(parts[2], "N", out var id) || id == Guid.Empty)
            throw new ArgumentException("The correlation cursor is invalid.", nameof(cursor));
        return new(new DateTimeOffset(ticks, TimeSpan.Zero), id);
    }

    public async Task<CaseCorrelationReadResult> GetAsync(ActorContext actor, Guid caseId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (caseId == Guid.Empty) return new(false, []);
        ICaseCommandLease source;
        try { source = await unitOfWork.BeginCaseAsync(actor, caseId, ImmutableArray<string>.Empty, CaseLeaseMode.Read, ct); }
        catch (KeyNotFoundException) { return new(false, []); }
        catch (InvalidOperationException error) when (error.Message is "The case authority scope is unavailable." or "The current case authority scope is unavailable.")
        { return new(false, []); }
        await using (source.ConfigureAwait(false))
        {
            if (!(await policy.AuthorizeAsync(actor, caseId, CaseOperation.ReadCase, ct)).IsAllowed) return new(false, []);
            var candidates = await store.GetCandidatesAsync(caseId, 100, ct);
            var visible = new List<CaseCorrelationView>();
            foreach (var candidate in candidates)
            {
                var other = candidate.FirstCaseId == caseId ? candidate.SecondCaseId : candidate.FirstCaseId;
                await using var scope = scopes.CreateScope();
                ICaseCommandLease target;
                try { target = await scope.UnitOfWork.BeginCaseAsync(actor, other, ImmutableArray<string>.Empty, CaseLeaseMode.Read, ct); }
                catch (KeyNotFoundException) { continue; }
                catch (InvalidOperationException error) when (error.Message is "The case authority scope is unavailable." or "The current case authority scope is unavailable.") { continue; }
                await using (target.ConfigureAwait(false))
                {
                    if (!(await scope.Policy.AuthorizeAsync(actor, other, CaseOperation.ReadCase, ct)).IsAllowed ||
                        !await store.AreCurrentAsync(candidate, ct)) continue;
                    visible.Add(new(candidate.CandidateId, other, candidate.RecordedAtUtc, candidate.Reasons,
                        "Possible relationship only; shared attributes do not establish identity or causation.",
                        Enum.TryParse<OrganizationDecision>(candidate.Decision, false, out var reviewed) && Enum.IsDefined(reviewed) ? reviewed : null));
                    await target.CommitAsync(ct);
                }
            }
            await source.CommitAsync(ct);
            return new(true, visible);
        }
    }
}
