using System.Security.Cryptography;
using System.Text.Json;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Registration;
using SwiftAid.Domain.Intake;

namespace SwiftAid.Application.Intake.Imports;

/// <summary>Bounded resumable batches. A crash between registration and checkpoint reuses the same command receipt.</summary>
public sealed class CaseCsvImportService(ICaseImportStore store, IManagedCaseRegistrationProcessor registration,
    IIntakeSubmissionValidator validator)
{
    public const int MaximumRowsPerBatch = 1000;

    public async Task<CaseImportBatchResult> ProcessAsync(ActorContext actor, Guid importId, CaseImportContext context,
        long startRow, bool finalBatch, TextReader reader, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(context.WorkspaceId) || context.WorkspaceId.Length > 200 || context.Confirmations is null)
            throw new CaseImportException("invalid-import-context");
        if (context.OrganizationId is not null && (string.IsNullOrWhiteSpace(context.OrganizationId) || context.OrganizationId.Length > 200 ||
            context.OrganizationId != context.OrganizationId.Trim() || context.OrganizationId.Any(char.IsControl)))
            throw new CaseImportException("invalid-import-context");
        // Check context shape before persisting an accepted import. Country/policy currency is also checked per row.
        var confirmations = context.Confirmations;
        if (!confirmations.ReportReviewConfirmed || !confirmations.ProcessingConfirmed || !confirmations.OccurrenceCountryConfirmed ||
            confirmations.OccurrenceCountryCode is not { Length: 2 } ||
            string.IsNullOrWhiteSpace(confirmations.OccurrenceCountryCatalogueVersion) || confirmations.OccurrenceCountryCatalogueVersion.Length > 64 ||
            string.IsNullOrWhiteSpace(confirmations.ProcessingPolicyReference) || confirmations.ProcessingPolicyReference.Length > 200 ||
            string.IsNullOrWhiteSpace(confirmations.ProcessingPolicyVersion) || confirmations.ProcessingPolicyVersion.Length > 64 ||
            confirmations.RecordingConfirmed is not null || confirmations.RecordingPolicyReference is not null || confirmations.RecordingPolicyVersion is not null)
            throw new CaseImportException("invalid-import-context");
        await using var records = BoundedCsvReader.ReadAsync(reader, ct).GetAsyncEnumerator(ct);
        if (!await records.MoveNextAsync()) throw new CaseImportException("csv-header-missing");
        var header = records.Current.Fields;
        CaseCsvImportAdapter.ValidateHeader(header);
        var lease = await store.OpenAsync(actor, importId, context, startRow, ct);
        var outcomes = new List<CaseImportRowOutcome>();
        var state = "Paused";
        string? error = null;
        try
        {
            while (await records.MoveNextAsync())
            {
                if (outcomes.Count >= MaximumRowsPerBatch) { error = "batch-row-limit"; break; }
                var rowNumber = checked(startRow + outcomes.Count);
                var row = records.Current with { Number = rowNumber + 1 };
                var parsed = CaseCsvImportAdapter.Parse(row, header, context, actor.ActorId);
                var source = JsonSerializer.SerializeToUtf8Bytes(new { columns = header, values = row.Fields });
                var hash = SHA256.HashData(source);
                int attempts;
                try { attempts = await store.BeginRowAsync(lease, rowNumber, parsed.Record?.CommandId, hash, source, ct); }
                finally { CryptographicOperations.ZeroMemory(source); }
                CaseImportRowOutcome outcome;
                if (!parsed.IsValid)
                {
                    outcome = new(rowNumber, null, "Invalid", null, parsed.ErrorCode);
                }
                else
                {
                    var record = parsed.Record!;
                    if (!await validator.ValidateAsync(record.Submission, new IntakeLimits(), ct))
                    {
                        outcome = new(rowNumber, record.CommandId, "Invalid", null, "current-policy-or-country");
                    }
                    else
                    {
                        var report = IntakePromotionMapper.ToCapturedReport(record.CommandId,
                            $"csv:{record.CommandId:N}:{record.SourceReference}", record.Submission);
                        var request = new RegisterManagedCaseRequest(record.CommandId, context.WorkspaceId, report, context.OrganizationId);
                        var result = await registration.RegisterAsync(actor, request, ct);
                        var status = result.Status == CaseRegistrationStatus.Unavailable
                            ? attempts >= 5 ? "Failed" : "RetryableFailure"
                            : result.Status.ToString();
                        outcome = new(rowNumber, record.CommandId, status, result.CaseId,
                            result.Status is CaseRegistrationStatus.Accepted or CaseRegistrationStatus.Replayed ? null : "registration-" + status.ToLowerInvariant());
                    }
                }
                await store.CompleteRowAsync(lease, outcome, ct);
                outcomes.Add(outcome);
                if (outcome.Status == "RetryableFailure") { error = "registration-unavailable"; break; }
                if (outcome.Status == "Denied") { error = "registration-denied"; break; }
            }
            if (error is null && finalBatch) state = "Completed";
        }
        catch (CaseImportException exception) { error = exception.Code; }
        catch (InvalidDataException) { state = "Failed"; error = "csv-structure-invalid"; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { error = "interrupted"; throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException) { error = "import-unavailable"; }
        finally
        {
            // A disconnected HTTP request does not undo accepted cases. Leave a recoverable checkpoint.
            using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await store.CloseAsync(lease, state, error, closeTimeout.Token); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { /* Lease expiry is the durable recovery fallback. */ }
        }
        return new(importId, outcomes, await store.GetAsync(actor, importId, ct), error);
    }
}
