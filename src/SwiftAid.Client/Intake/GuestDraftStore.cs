using System.Security.Cryptography;
using System.Text.Json;
using SwiftAid.Contracts.Intake;

namespace SwiftAid.Client.Intake;

/// <summary>Creates stable guest submission identities and proof before the item is queued.</summary>
public sealed class GuestDraftStore(
    IGuestSubmissionPersistence persistence,
    TimeProvider? timeProvider = null,
    IGuestIntakeTransport? transport = null,
    IGuestAudioCleanupJournal? cleanupJournal = null,
    IGuestAudioStore? audioStore = null,
    IGuestDraftMutationGate? mutationGate = null) : IGuestSubmissionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IGuestDraftMutationGate _mutationGate = mutationGate ?? GuestDraftMutationGate.Instance;

    public async Task<GuestSubmissionSummary> CreateDraftAsync(
        IntakeSubmission submission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        var mutationLease = await _mutationGate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CreateRecordAsync(submission, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await mutationLease.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<GuestSubmissionSummary> CreateEditableDraftAsync(IntakeSubmission carrier,
        GuestDraftFormSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ValidateSnapshot(snapshot);
        ArgumentNullException.ThrowIfNull(carrier);
        var mutationLease = await _mutationGate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var record = CreateRecord(UnconfirmCarrier(carrier), _timeProvider.GetUtcNow()) with { FormSnapshot = snapshot };
            await persistence.SaveVersionedAsync(record, null, cancellationToken).ConfigureAwait(false);
            return ToSummary(record);
        }
        finally { await mutationLease.DisposeAsync().ConfigureAwait(false); }
    }

    public Task<GuestSubmissionSummary> CreateRevisionAsync(
        Guid previousSubmissionId,
        IntakeSubmission revisedSubmission,
        CancellationToken cancellationToken = default) =>
        CreateRevisionCoreAsync(previousSubmissionId, revisedSubmission, null, false, cancellationToken);

    public Task<GuestSubmissionSummary> CreateEditableRevisionAsync(Guid previousSubmissionId, IntakeSubmission carrier,
        GuestDraftFormSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ValidateSnapshot(snapshot);
        return CreateRevisionCoreAsync(previousSubmissionId, UnconfirmCarrier(carrier), snapshot, true, cancellationToken);
    }

    public Task<GuestSubmissionSummary> CompleteEditableDraftAsync(Guid previousSubmissionId, IntakeSubmission submission,
        CancellationToken cancellationToken = default) =>
        CreateRevisionCoreAsync(previousSubmissionId, submission, null, true, cancellationToken);

    private async Task<GuestSubmissionSummary> CreateRevisionCoreAsync(Guid previousSubmissionId,
        IntakeSubmission revisedSubmission, GuestDraftFormSnapshot? snapshot, bool allowEditable,
        CancellationToken cancellationToken)
    {
        if (previousSubmissionId == Guid.Empty) throw new ArgumentException("A previous report is required.", nameof(previousSubmissionId));
        ArgumentNullException.ThrowIfNull(revisedSubmission);
        var mutationLease = await _mutationGate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = await persistence.GetAsync(previousSubmissionId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("The saved report could not be found.");
            if (previous.State is not (GuestSubmissionLocalState.Draft or GuestSubmissionLocalState.NeedsAttention))
            {
                throw new InvalidOperationException("A queued or received report cannot be changed. Create a separate report revision instead.");
            }
            if (previous.FormSnapshot is not null && !allowEditable)
                throw new InvalidOperationException("Open this draft in a compatible editor before replacing it.");
            if (previous.FormSnapshot is { } previousSnapshot && !previousSnapshot.IsValid())
                throw new InvalidOperationException("This editable draft is invalid or unsupported.");
            if (allowEditable && snapshot is null && previous.FormSnapshot is null)
                throw new InvalidOperationException("This report does not contain an editable snapshot.");

            // Editable form snapshots do not own audio changes. Preserve the existing
            // attachment binding; replacing a recording uses the explicit retake path.
            if (allowEditable)
                revisedSubmission = revisedSubmission with
                {
                    OriginalAudioAttachmentIds = previous.Submission.OriginalAudioAttachmentIds.ToArray()
                };

            var replacement = CreateRecord(revisedSubmission, _timeProvider.GetUtcNow()) with
            {
                FormSnapshot = snapshot,
                AudioAssets = (previous.AudioAssets ?? Array.Empty<GuestAudioAsset>())
                    .Where(asset => revisedSubmission.OriginalAudioAttachmentIds.Contains(asset.AttachmentId))
                    .ToArray()
            };
            var superseded = previous with
            {
                State = GuestSubmissionLocalState.Superseded,
                UpdatedAtUtc = _timeProvider.GetUtcNow(),
                Version = checked(previous.Version + 1)
            };
            await persistence.SaveBatchVersionedAsync(
                [
                    new GuestSubmissionWrite(superseded, previous.Version),
                    new GuestSubmissionWrite(replacement, null)
                ],
                cancellationToken).ConfigureAwait(false);
            return ToSummary(replacement);
        }
        finally
        {
            await mutationLease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static void ValidateSnapshot(GuestDraftFormSnapshot? snapshot)
    {
        if (snapshot is null || !snapshot.IsValid())
            throw new ArgumentException("The editable draft is invalid or unsupported.", nameof(snapshot));
    }

    private static IntakeSubmission UnconfirmCarrier(IntakeSubmission carrier)
    {
        ArgumentNullException.ThrowIfNull(carrier);
        ArgumentNullException.ThrowIfNull(carrier.Confirmations);
        return carrier with { Confirmations = carrier.Confirmations with
        {
            ReportReviewConfirmed = false,
            OccurrenceCountryConfirmed = false,
            ProcessingConfirmed = false
        } };
    }

    public async Task<GuestAudioRetakeResult> RetakeAudioAsync(
        Guid submissionId,
        Guid attachmentId,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        if (submissionId == Guid.Empty || attachmentId == Guid.Empty || expectedVersion < 0)
        {
            return Retake(GuestAudioRetakeStatus.InvalidRequest, "Choose a saved recording from this report.");
        }

        if (cleanupJournal is null || audioStore is null)
        {
            return Retake(GuestAudioRetakeStatus.Unavailable, "Recording cleanup is unavailable on this device.");
        }

        var mutationLease = await _mutationGate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        GuestAudioCleanupIntent? intent = null;
        var intentWritten = false;
        var revisionSaved = false;
        try
        {
            var current = await persistence.GetAsync(submissionId, cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                return Retake(GuestAudioRetakeStatus.NotFound, "The saved report could not be found.");
            }

            if (current.Version != expectedVersion)
            {
                return Retake(GuestAudioRetakeStatus.Conflict, "This report changed. Refresh it before trying again.");
            }

            if (current.State is not (GuestSubmissionLocalState.Draft or GuestSubmissionLocalState.NeedsAttention))
            {
                return Retake(GuestAudioRetakeStatus.NotEditable, "Only an unsent report can replace a saved recording.");
            }

            var assets = current.AudioAssets ?? Array.Empty<GuestAudioAsset>();
            if (!HasExactAudioBinding(current.Submission, assets) ||
                !current.Submission.OriginalAudioAttachmentIds.Contains(attachmentId) ||
                !assets.Any(asset => asset.AttachmentId == attachmentId))
            {
                return Retake(GuestAudioRetakeStatus.InvalidRequest, "That recording is not saved on this report.");
            }

            var revisedSubmission = current.Submission with
            {
                OriginalAudioAttachmentIds = current.Submission.OriginalAudioAttachmentIds
                    .Where(id => id != attachmentId)
                    .ToArray(),
                ClientSubmissionId = Guid.Empty
            };
            ValidateSubmission(revisedSubmission);

            var now = _timeProvider.GetUtcNow();
            var replacement = CreateRecord(revisedSubmission, now) with
            {
                FormSnapshot = current.FormSnapshot,
                AudioAssets = assets.Where(asset => asset.AttachmentId != attachmentId).ToArray()
            };
            var superseded = current with
            {
                State = GuestSubmissionLocalState.Superseded,
                UpdatedAtUtc = now,
                Version = checked(current.Version + 1)
            };
            intent = new GuestAudioCleanupIntent(
                $"guest-audio-retake:{current.ClientSubmissionId:N}:{superseded.Version}:{replacement.ClientSubmissionId:N}:{attachmentId:N}",
                current.ClientSubmissionId,
                superseded.Version,
                replacement.ClientSubmissionId,
                replacement.Version,
                attachmentId,
                now);

            var journalResult = await cleanupJournal.EnsurePendingAsync(intent, cancellationToken).ConfigureAwait(false);
            if (!journalResult.IsPending)
            {
                return Retake(journalResult.Code == GuestAudioCleanupJournalCode.InvalidRequest
                    ? GuestAudioRetakeStatus.InvalidRequest
                    : GuestAudioRetakeStatus.Unavailable,
                    "The recording cleanup could not be prepared.");
            }
            intentWritten = true;

            try
            {
                await persistence.SaveBatchVersionedAsync(
                    [
                        new GuestSubmissionWrite(superseded, current.Version),
                        new GuestSubmissionWrite(replacement, null)
                    ],
                    cancellationToken).ConfigureAwait(false);
                revisionSaved = true;
            }
            catch
            {
                await CancelIntentSafelyAsync(intent.IntentKey).ConfigureAwait(false);
                intentWritten = false;
                throw;
            }

            var cleanup = await TryCleanupAsync(intent, cancellationToken).ConfigureAwait(false);
            if (cleanup.IsTerminal)
            {
                var message = cleanup.Code == GuestAudioCleanupCode.RetainedForHistory
                    ? "A new report draft is ready. The earlier recording remains available in another report."
                    : "A new report draft is ready. The previous recording was removed from device storage.";
                return new GuestAudioRetakeResult(GuestAudioRetakeStatus.Replaced, ToSummary(replacement),
                    superseded.ClientSubmissionId, replacement.ClientSubmissionId, attachmentId, intent.IntentKey,
                    message);
            }

            return new GuestAudioRetakeResult(GuestAudioRetakeStatus.CleanupPending, ToSummary(replacement),
                superseded.ClientSubmissionId, replacement.ClientSubmissionId, attachmentId, intent.IntentKey,
                "A new report draft is ready. Removal of the previous recording is pending.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (intentWritten && !revisionSaved && intent is not null)
            {
                await CancelIntentSafelyAsync(intent.IntentKey).ConfigureAwait(false);
            }
            if (revisionSaved && intent is not null)
            {
                var replacement = await persistence.GetAsync(intent.ReplacementSubmissionId, CancellationToken.None).ConfigureAwait(false);
                return new GuestAudioRetakeResult(GuestAudioRetakeStatus.CleanupPending,
                    replacement is null ? null : ToSummary(replacement), intent.SupersededSubmissionId,
                    intent.ReplacementSubmissionId, intent.AttachmentId, intent.IntentKey,
                    "A new report draft is ready. Removal of the previous recording is pending.");
            }
            return Retake(GuestAudioRetakeStatus.Cancelled, "The recording change was cancelled.");
        }
        catch (GuestSubmissionPersistenceConflictException)
        {
            if (intentWritten && !revisionSaved && intent is not null)
            {
                await CancelIntentSafelyAsync(intent.IntentKey).ConfigureAwait(false);
            }
            return Retake(GuestAudioRetakeStatus.Conflict, "This report changed. Refresh it before trying again.");
        }
        catch (Exception)
        {
            if (intentWritten && intent is not null)
            {
                // A durable replacement may already exist, so leave its intent for safe retry.
                var old = await persistence.GetAsync(intent.SupersededSubmissionId, CancellationToken.None).ConfigureAwait(false);
                if (old?.State == GuestSubmissionLocalState.Superseded)
                {
                    var replacement = await persistence.GetAsync(intent.ReplacementSubmissionId, CancellationToken.None).ConfigureAwait(false);
                    return new GuestAudioRetakeResult(GuestAudioRetakeStatus.CleanupPending,
                        replacement is null ? null : ToSummary(replacement), intent.SupersededSubmissionId,
                        intent.ReplacementSubmissionId, intent.AttachmentId, intent.IntentKey,
                        "A new report draft is ready. Removal of the previous recording is pending.");
                }

                if (old is null)
                {
                    await CancelIntentSafelyAsync(intent.IntentKey).ConfigureAwait(false);
                }
            }
            return Retake(GuestAudioRetakeStatus.Failed, "The recording change could not be completed.");
        }
        finally
        {
            await mutationLease.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<GuestAudioCleanupRetrySummary> RetryPendingAudioCleanupAsync(
        CancellationToken cancellationToken = default)
    {
        if (cleanupJournal is null || audioStore is null)
        {
            return new GuestAudioCleanupRetrySummary(0, 0, 0, 1);
        }

        var mutationLease = await _mutationGate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pending = await cleanupJournal.GetPendingAsync(cancellationToken).ConfigureAwait(false);
            var completed = 0;
            var pendingCount = 0;
            var cancelled = 0;
            var failed = 0;
            var retainedForHistory = 0;
            foreach (var intent in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!intent.IsValid)
                {
                    failed++;
                    continue;
                }

                var old = await persistence.GetAsync(intent.SupersededSubmissionId, cancellationToken).ConfigureAwait(false);
                var replacement = await persistence.GetAsync(intent.ReplacementSubmissionId, cancellationToken).ConfigureAwait(false);
                if (old is null || replacement is null ||
                    old.State != GuestSubmissionLocalState.Superseded || old.Version != intent.ExpectedSupersededVersion ||
                    replacement.State is not (GuestSubmissionLocalState.Draft or GuestSubmissionLocalState.NeedsAttention) ||
                    replacement.Version != intent.ExpectedReplacementVersion ||
                    replacement.Submission.OriginalAudioAttachmentIds.Contains(intent.AttachmentId) ||
                    (replacement.AudioAssets ?? Array.Empty<GuestAudioAsset>()).Any(asset => asset.AttachmentId == intent.AttachmentId))
                {
                    var cancel = await cleanupJournal.CancelAsync(intent.IntentKey, cancellationToken).ConfigureAwait(false);
                    if (cancel.Code is GuestAudioCleanupJournalCode.Cancelled or GuestAudioCleanupJournalCode.AlreadyCancelled)
                    {
                        cancelled++;
                    }
                    else
                    {
                        failed++;
                    }
                    continue;
                }

                GuestAudioCleanupResult result;
                try
                {
                    result = await TryCleanupAsync(intent, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    pendingCount++;
                    continue;
                }
                if (result.VerifiedAbsent)
                {
                    completed++;
                }
                else if (result.Code == GuestAudioCleanupCode.RetainedForHistory)
                {
                    retainedForHistory++;
                }
                else if (result.Code is GuestAudioCleanupCode.NotSafe or GuestAudioCleanupCode.Unavailable)
                {
                    pendingCount++;
                }
                else
                {
                    failed++;
                }
            }

            return new GuestAudioCleanupRetrySummary(completed, pendingCount, cancelled, failed, retainedForHistory);
        }
        finally
        {
            await mutationLease.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<GuestSubmissionSummary> QueueAsync(
        Guid clientSubmissionId,
        CancellationToken cancellationToken = default)
    {
        if (clientSubmissionId == Guid.Empty) throw new ArgumentException("A report identifier is required.", nameof(clientSubmissionId));
        var mutationLease = await _mutationGate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await persistence.GetAsync(clientSubmissionId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("The saved report could not be found.");
            if (current.State == GuestSubmissionLocalState.Queued || current.State == GuestSubmissionLocalState.Received)
            {
                return ToSummary(current);
            }

            if (current.State != GuestSubmissionLocalState.Draft)
            {
                throw new InvalidOperationException("This report is not ready to be queued.");
            }
            if (current.FormSnapshot is not null)
                throw new InvalidOperationException("Open and complete this draft before sending it.");

            if (transport is null)
            {
                throw new GuestIntakeTransportException();
            }

            // Validate the selected country against the latest public snapshot only when queuing.
            // An unavailable or stale catalogue leaves the local draft untouched.
            var metadata = await transport.GetMetadataAsync(cancellationToken).ConfigureAwait(false);
            var catalogue = await transport.GetCountriesAsync(cancellationToken).ConfigureAwait(false);
            ValidateCurrentCountry(current.Submission, metadata, catalogue);
            ValidateReadyToQueue(current.Submission);
            var audioAssets = current.AudioAssets ?? Array.Empty<GuestAudioAsset>();
            if (audioAssets.Count != current.Submission.OriginalAudioAttachmentIds.Count ||
                !audioAssets.Select(asset => asset.AttachmentId).ToHashSet()
                    .SetEquals(current.Submission.OriginalAudioAttachmentIds))
            {
                throw new InvalidOperationException("Original audio must be saved on this device before the report is queued.");
            }

            var queued = current with
            {
                State = GuestSubmissionLocalState.Queued,
                UpdatedAtUtc = _timeProvider.GetUtcNow(),
                Version = checked(current.Version + 1)
            };
            await persistence.SaveVersionedAsync(queued, current.Version, cancellationToken).ConfigureAwait(false);
            return ToSummary(queued);
        }
        finally
        {
            await mutationLease.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<GuestSubmissionSummary?> GetAsync(
        Guid clientSubmissionId,
        CancellationToken cancellationToken = default)
    {
        var item = await persistence.GetAsync(clientSubmissionId, cancellationToken).ConfigureAwait(false);
        return item is null ? null : ToSummary(item);
    }

    public async Task<GuestDraftDetails?> GetDraftAsync(
        Guid clientSubmissionId,
        CancellationToken cancellationToken = default)
    {
        if (clientSubmissionId == Guid.Empty) throw new ArgumentException("A report identifier is required.", nameof(clientSubmissionId));
        var item = await persistence.GetAsync(clientSubmissionId, cancellationToken).ConfigureAwait(false);
        if (item is null || item.State is not (GuestSubmissionLocalState.Draft or GuestSubmissionLocalState.NeedsAttention))
        {
            return null;
        }

        // Existing editors restore only Submission and would silently discard raw fields.
        // Keep the envelope intact until an editor explicitly supports snapshots.
        if (item.FormSnapshot is not null)
            throw new InvalidOperationException("This draft requires a compatible editor.");

        return new GuestDraftDetails(
            item.ClientSubmissionId,
            item.Submission,
            item.State,
            item.CreatedAtUtc,
            item.UpdatedAtUtc,
            item.Version);
    }

    public async Task<GuestEditableDraftDetails?> GetEditableDraftAsync(Guid clientSubmissionId,
        CancellationToken cancellationToken = default)
    {
        if (clientSubmissionId == Guid.Empty) throw new ArgumentException("A report identifier is required.", nameof(clientSubmissionId));
        var item = await persistence.GetAsync(clientSubmissionId, cancellationToken).ConfigureAwait(false);
        if (item is null || item.State is not (GuestSubmissionLocalState.Draft or GuestSubmissionLocalState.NeedsAttention) ||
            item.FormSnapshot is null) return null;
        if (!item.FormSnapshot.IsValid())
            throw new InvalidOperationException("This editable draft is invalid or unsupported.");
        return new(item.ClientSubmissionId, item.Submission, item.FormSnapshot, item.State, item.Version);
    }

    public async Task<IReadOnlyList<GuestSubmissionSummary>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var items = await persistence.GetAllAsync(cancellationToken).ConfigureAwait(false);
        return items.Where(item => item.State != GuestSubmissionLocalState.Superseded)
            .Select(ToSummary)
            .OrderByDescending(item => item.CreatedAtUtc)
            .ToArray();
    }

    private async Task<GuestSubmissionSummary> CreateRecordAsync(
        IntakeSubmission submission,
        CancellationToken cancellationToken)
    {
        var record = CreateRecord(submission, _timeProvider.GetUtcNow());
        await persistence.SaveVersionedAsync(record, null, cancellationToken).ConfigureAwait(false);
        return ToSummary(record);
    }

    private static GuestSubmissionRecord CreateRecord(IntakeSubmission submission, DateTimeOffset now)
    {
        ValidateSubmission(submission);
        var id = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var immutableSubmission = Freeze(submission with { ClientSubmissionId = id });
        var payload = JsonSerializer.SerializeToUtf8Bytes(immutableSubmission, JsonOptions);
        var hash = Convert.ToHexString(SHA256.HashData(payload));
        CryptographicOperations.ZeroMemory(payload);

        var proofBytes = RandomNumberGenerator.GetBytes(32);
        try
        {
            var receiptProof = Convert.ToBase64String(proofBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return new GuestSubmissionRecord(
                id,
                sessionId,
                immutableSubmission,
                receiptProof,
                hash,
                GuestSubmissionLocalState.Draft,
                now,
                now);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(proofBytes);
        }
    }

    private async Task<GuestAudioCleanupResult> TryCleanupAsync(
        GuestAudioCleanupIntent intent,
        CancellationToken cancellationToken)
    {
        var result = await audioStore!.CleanupSupersededOriginalAsync(intent, cancellationToken).ConfigureAwait(false);
        if (!result.IsTerminal)
        {
            return result;
        }

        var journalResult = await cleanupJournal!.CompleteAsync(intent.IntentKey, cancellationToken).ConfigureAwait(false);
        return journalResult.IsComplete
            ? result
            : new GuestAudioCleanupResult(GuestAudioCleanupCode.Failed, "The cleanup record could not be finalized.");
    }

    private async Task CancelIntentSafelyAsync(string intentKey)
    {
        try
        {
            await cleanupJournal!.CancelAsync(intentKey, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The cleanup worker revalidates both revisions before any deletion.
        }
    }

    private static bool HasExactAudioBinding(IntakeSubmission submission, IReadOnlyList<GuestAudioAsset> assets) =>
        assets.Count == submission.OriginalAudioAttachmentIds.Count &&
        assets.Select(asset => asset.AttachmentId).Distinct().Count() == assets.Count &&
        assets.Select(asset => asset.AttachmentId).ToHashSet().SetEquals(submission.OriginalAudioAttachmentIds);

    private static GuestAudioRetakeResult Retake(
        GuestAudioRetakeStatus status,
        string message) => new(
            status,
            null,
            null,
            null,
            null,
            null,
            message);

    private static IntakeSubmission Freeze(IntakeSubmission submission) => submission with
    {
        Observations = Array.AsReadOnly(submission.Observations.ToArray()),
        OriginalAudioAttachmentIds = Array.AsReadOnly(submission.OriginalAudioAttachmentIds.ToArray())
    };

    private static void ValidateSubmission(IntakeSubmission submission)
    {
        if (submission.SchemaVersion != 1 || !Enum.IsDefined(submission.SubjectScope) ||
            submission.Observations is null || submission.OriginalAudioAttachmentIds is null ||
            submission.CapturedAt is null || submission.Occurrence is null || submission.Location is null ||
            submission.Confirmations is null ||
            (submission.Narrative is not null && submission.Narrative.Length > 6000) ||
            submission.Observations.Count > 20 || submission.Observations.Any(value =>
                string.IsNullOrWhiteSpace(value) || value.Length > 1000) ||
            submission.OriginalAudioAttachmentIds.Count > 2 ||
            submission.OriginalAudioAttachmentIds.Any(id => id == Guid.Empty) ||
            submission.OriginalAudioAttachmentIds.Distinct().Count() != submission.OriginalAudioAttachmentIds.Count)
        {
            throw new ArgumentException("The saved report is incomplete or exceeds local limits.", nameof(submission));
        }
    }

    private static void ValidateReadyToQueue(IntakeSubmission submission)
    {
        ValidateSubmission(submission);
        var confirmation = submission.Confirmations;
        var location = submission.Location;
        if (string.IsNullOrWhiteSpace(submission.Narrative) && submission.Observations.Count == 0 &&
                submission.OriginalAudioAttachmentIds.Count == 0 ||
            !confirmation.ReportReviewConfirmed || !confirmation.OccurrenceCountryConfirmed ||
            !confirmation.ProcessingConfirmed || string.IsNullOrWhiteSpace(confirmation.OccurrenceCountryCode) ||
            confirmation.OccurrenceCountryCode.Length != 2 || string.IsNullOrWhiteSpace(location.CountryCode) ||
            !string.Equals(confirmation.OccurrenceCountryCode, confirmation.OccurrenceCountryCode.ToUpperInvariant(), StringComparison.Ordinal) ||
            !string.Equals(location.CountryCode, location.CountryCode.ToUpperInvariant(), StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(location.CatalogueVersion) ||
            !string.Equals(location.CatalogueVersion, confirmation.OccurrenceCountryCatalogueVersion, StringComparison.Ordinal) ||
            !confirmation.OccurrenceCountryCode.All(char.IsAsciiLetter) ||
            !string.Equals(confirmation.OccurrenceCountryCode, location.CountryCode, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(confirmation.OccurrenceCountryCatalogueVersion) ||
            string.IsNullOrWhiteSpace(confirmation.ProcessingPolicyReference) ||
            string.IsNullOrWhiteSpace(confirmation.ProcessingPolicyVersion) ||
            !Enum.IsDefined(submission.CapturedAt.Precision) || !Enum.IsDefined(submission.Occurrence.Precision) ||
            !Enum.IsDefined(location.Source) || !Enum.IsDefined(location.Precision) ||
            location.Region is null || location.Area is null || location.Locality is null ||
            !Enum.IsDefined(location.Region.Presence) || !Enum.IsDefined(location.Area.Presence) ||
            !Enum.IsDefined(location.Locality.Presence) ||
            location.Latitude is < -90 or > 90 || location.Longitude is < -180 or > 180 ||
            location.Latitude is { } latitude && !double.IsFinite(latitude) ||
            location.Longitude is { } longitude && !double.IsFinite(longitude) ||
            location.AccuracyMetres is < 0 || location.AccuracyMetres is { } accuracy && !double.IsFinite(accuracy) ||
            (location.Latitude.HasValue != location.Longitude.HasValue) ||
            (submission.OriginalAudioAttachmentIds.Count > 0 &&
                (confirmation.RecordingConfirmed != true ||
                 string.IsNullOrWhiteSpace(confirmation.RecordingPolicyReference) ||
                 string.IsNullOrWhiteSpace(confirmation.RecordingPolicyVersion))))
        {
            throw new ArgumentException("The report needs valid details and the required confirmations.", nameof(submission));
        }
    }

    private static void ValidateCurrentCountry(
        IntakeSubmission submission,
        PublicIntakeMetadata metadata,
        PublicIntakeCountryCatalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(catalogue);
        var selectedCode = submission.Confirmations?.OccurrenceCountryCode;
        var version = submission.Confirmations?.OccurrenceCountryCatalogueVersion;
        if (string.IsNullOrWhiteSpace(metadata.CountryCatalogueVersion) ||
            !string.Equals(metadata.Version, metadata.CountryCatalogueVersion, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(metadata.CountryCatalogueReference) ||
            string.IsNullOrWhiteSpace(catalogue.Version) ||
            !string.Equals(metadata.CountryCatalogueVersion, catalogue.Version, StringComparison.Ordinal) ||
            !string.Equals(metadata.CountryCatalogueReference, catalogue.Reference, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(selectedCode) || string.IsNullOrWhiteSpace(version) ||
            !string.Equals(version, catalogue.Version, StringComparison.Ordinal) ||
            catalogue.Countries is not { Count: > 0 } ||
            catalogue.Countries.Any(country => country is null || country.Code is not { Length: 2 } ||
                country.Code.Any(character => character is < 'A' or > 'Z') ||
                string.IsNullOrWhiteSpace(country.Label)) ||
            catalogue.Countries.Select(country => country.Code).Distinct(StringComparer.Ordinal).Count() !=
                catalogue.Countries.Count ||
            !catalogue.Countries.Any(country => string.Equals(country.Code, selectedCode, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Choose a country from the current list before sending this report.");
        }
    }

    internal static GuestSubmissionSummary ToSummary(GuestSubmissionRecord item) => new(
        item.ClientSubmissionId,
        item.State,
        item.CreatedAtUtc,
        item.Receipt,
        item.FormSnapshot is not null);
}
