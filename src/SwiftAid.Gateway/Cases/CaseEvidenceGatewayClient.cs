using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Components.Authorization;
using SwiftAid.Application.Abstractions;
using SwiftAid.Application.Attachments;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Client.Presentation;
using SwiftAid.Contracts.Commands;
using SwiftAid.Domain.Cases;

namespace SwiftAid.Gateway.Cases;

/// <summary>Original private media bound to an authorized, versioned case journal event.</summary>
public sealed class CaseEvidenceGatewayClient(AuthenticationStateProvider authentication, IServiceScopeFactory scopes,
    ILogger<CaseEvidenceGatewayClient> logger) : ICaseEvidenceClient, ICaseEvidenceUploadClient, ICaseEvidenceContentClient
{
    public Task<UiCapabilityResult<CaseEvidenceSnapshot>> GetAsync(Guid caseId, CaseEvidenceQuery query, CancellationToken cancellationToken = default) =>
        !query.IsValid ? Task.FromResult(Result<CaseEvidenceSnapshot>(UiCapabilityResultCode.InvalidRequest))
        : ReadAsync(caseId, async (_, aggregate) =>
        {
            var rows = aggregate.Events.OfType<CaseObservationRecorded>().Where(row => row.Attachment is not null)
                .OrderByDescending(row => row.AggregateVersion).ToArray();
            var offset = (query.Page.PageNumber - 1) * query.Page.PageSize;
            await Task.CompletedTask;
            return Result(UiCapabilityResultCode.Success, new CaseEvidenceSnapshot(caseId, aggregate.AggregateVersion,
                new(rows.Skip(offset).Take(query.Page.PageSize).Select(ToReference).ToArray(), query.Page.PageNumber,
                    query.Page.PageSize, rows.Length, offset + query.Page.PageSize < rows.Length)));
        }, cancellationToken);

    public Task<UiCapabilityResult<CaseEvidenceDetail>> GetDetailAsync(Guid caseId, string evidenceKey, CancellationToken cancellationToken = default) =>
        ReadAsync(caseId, (_, aggregate) =>
        {
            var row = Find(aggregate, evidenceKey);
            return Task.FromResult(row?.Attachment is not { } attachment
                ? Result<CaseEvidenceDetail>(UiCapabilityResultCode.Denied)
                : Result(UiCapabilityResultCode.Success, new CaseEvidenceDetail(caseId, aggregate.AggregateVersion,
                    ToReference(row), attachment.ContentType, attachment.ByteLength, attachment.Sha256)));
        }, cancellationToken);

    public async Task<UiCapabilityResult<CaseEvidenceDownload>> DownloadAsync(Guid caseId, string evidenceKey, CancellationToken cancellationToken = default)
    {
        await using var bytes = new MemoryStream();
        var result = await DownloadToAsync(caseId, evidenceKey, bytes, cancellationToken);
        if (!result.Succeeded || result.Value is not { } metadata) return Result<CaseEvidenceDownload>(result.Code);
        if (metadata.ByteLength > CaseEvidenceDownload.MaximumContentBytes || metadata.ContentType != "text/plain; charset=utf-8")
            return Result<CaseEvidenceDownload>(UiCapabilityResultCode.InvalidRequest);
        return Result(UiCapabilityResultCode.Success, new CaseEvidenceDownload(caseId, metadata.AggregateVersion,
            evidenceKey, metadata.FileName, metadata.ContentType, metadata.ByteLength, metadata.Sha256, bytes.ToArray()));
    }

    public Task<UiCapabilityResult<CaseEvidenceContentMetadata>> DownloadToAsync(Guid caseId, string evidenceKey, Stream destination,
        CancellationToken cancellationToken = default) => ReadAsync(caseId, async (services, aggregate) =>
        {
            var row = Find(aggregate, evidenceKey);
            if (row?.Attachment is not { IsValid: true } attachment)
                return Result<CaseEvidenceContentMetadata>(UiCapabilityResultCode.Denied);
            await using var media = await services.GetRequiredService<IPrivateMediaObjectStore>().OpenReadAsync(
                new ObjectReference(attachment.ObjectId), new AttachmentByteRange(0, attachment.ByteLength - 1), cancellationToken);
            if (media is null || media.TotalLength != attachment.ByteLength)
                return Result<CaseEvidenceContentMetadata>(UiCapabilityResultCode.Unavailable);
            var bytes = await ReadBoundedAsync(media.Content, attachment.ByteLength, cancellationToken);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), attachment.Sha256, StringComparison.OrdinalIgnoreCase))
                return Result<CaseEvidenceContentMetadata>(UiCapabilityResultCode.Unavailable);
            await destination.WriteAsync(bytes, cancellationToken);
            return Result(UiCapabilityResultCode.Success, Metadata(caseId, aggregate.AggregateVersion, row));
        }, cancellationToken);

    public async Task<UiCapabilityResult<CaseEvidenceUploadResult>> UploadAsync(CaseEvidenceUploadRequest request, Stream content,
        CancellationToken cancellationToken = default)
    {
        if (!request.IsValid || !content.CanRead) return Result<CaseEvidenceUploadResult>(UiCapabilityResultCode.InvalidRequest);
        try
        {
            // Authorization happens before reading or storing user-supplied bytes.
            var current = await ReadAsync(request.CaseId, (_, aggregate) => Task.FromResult(
                Result(UiCapabilityResultCode.Success, aggregate)), cancellationToken, CaseOperation.RecordObservation);
            if (!current.Succeeded || current.Value is null) return Result<CaseEvidenceUploadResult>(current.Code);
            var bytes = await ReadBoundedAsync(content, request.DeclaredContentLength, cancellationToken);
            var type = DetectContentType(request.FileName, bytes);
            if (type is null) return Result<CaseEvidenceUploadResult>(UiCapabilityResultCode.InvalidRequest);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var actor = ActorContext.FromValidatedPrincipal((await authentication.GetAuthenticationStateAsync()).User);
            var previous = current.Value.Events.OfType<CaseObservationRecorded>()
                .SingleOrDefault(row => row.CommandId == request.SubmissionId && row.ActorId == actor.ActorId);
            if (previous is not null)
                return previous.Attachment is { } saved && saved.FileName == request.GetCanonicalFileName()
                    && saved.ContentType == type && saved.ByteLength == bytes.Length && saved.Sha256 == hash
                    && previous.AggregateVersion == request.ExpectedVersion + 1
                    ? Result(UiCapabilityResultCode.Success, new CaseEvidenceUploadResult(request.CaseId, request.SubmissionId,
                        previous.AggregateVersion, Metadata(request.CaseId, previous.AggregateVersion, previous)))
                    : Result<CaseEvidenceUploadResult>(UiCapabilityResultCode.Conflict);
            if (current.Value.AggregateVersion != request.ExpectedVersion)
                return Result<CaseEvidenceUploadResult>(UiCapabilityResultCode.Conflict);

            await using var scope = scopes.CreateAsyncScope();
            await using var stream = new MemoryStream(bytes, writable: false);
            var stored = await scope.ServiceProvider.GetRequiredService<IObjectStorage>().StoreAsync(stream, type, cancellationToken);
            if (stored.Status != ObjectStorageStatus.Succeeded || stored.Reference is null)
                return Result<CaseEvidenceUploadResult>(UiCapabilityResultCode.Unavailable);
            var attachment = new RecordedAttachment(stored.Reference.Id, request.GetCanonicalFileName(), type, bytes.Length, hash);
            var command = new RecordObservationCommand(request.SubmissionId, request.ExpectedVersion, request.SubmissionId,
                "Original attachment added.", "File upload", "Preserve original case evidence", null, DateTimeOffset.UtcNow,
                Attachment: attachment);
            var result = await scope.ServiceProvider.GetRequiredService<ICaseCommandProcessor>()
                .ProcessAsync(actor, request.CaseId, 1, command, cancellationToken);
            if (result.Status is not (CaseCommandProcessingStatus.Accepted or CaseCommandProcessingStatus.Replayed))
                return Result<CaseEvidenceUploadResult>(result.Status switch
                {
                    CaseCommandProcessingStatus.Denied => UiCapabilityResultCode.Denied,
                    CaseCommandProcessingStatus.Conflict => UiCapabilityResultCode.Conflict,
                    CaseCommandProcessingStatus.Invalid => UiCapabilityResultCode.InvalidRequest,
                    _ => UiCapabilityResultCode.Unavailable
                });
            return await ReadAsync(request.CaseId, (_, aggregate) =>
            {
                var saved = Find(aggregate, request.SubmissionId.ToString("D"));
                return Task.FromResult(saved is null ? Result<CaseEvidenceUploadResult>(UiCapabilityResultCode.Unavailable)
                    : Result(UiCapabilityResultCode.Success, new CaseEvidenceUploadResult(request.CaseId, request.SubmissionId,
                        saved.AggregateVersion, Metadata(request.CaseId, saved.AggregateVersion, saved))));
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (InvalidDataException) { return Result<CaseEvidenceUploadResult>(UiCapabilityResultCode.InvalidRequest); }
        catch (UnauthorizedAccessException) { return Result<CaseEvidenceUploadResult>(UiCapabilityResultCode.Denied); }
        catch (Exception error)
        {
            logger.LogWarning(error, "Original evidence upload could not be confirmed.");
            return Result<CaseEvidenceUploadResult>(UiCapabilityResultCode.Unavailable);
        }
    }

    private async Task<UiCapabilityResult<T>> ReadAsync<T>(Guid caseId,
        Func<IServiceProvider, CaseAggregate, Task<UiCapabilityResult<T>>> read, CancellationToken ct,
        CaseOperation? additionalOperation = null)
    {
        if (caseId == Guid.Empty) return Result<T>(UiCapabilityResultCode.InvalidRequest);
        try
        {
            var actor = ActorContext.FromValidatedPrincipal((await authentication.GetAuthenticationStateAsync()).User);
            await using var scope = scopes.CreateAsyncScope();
            var services = scope.ServiceProvider;
            await using var lease = await services.GetRequiredService<ICaseCommandUnitOfWork>()
                .BeginCaseAsync(actor, caseId, ImmutableArray<string>.Empty, CaseLeaseMode.Read, ct);
            var policy = services.GetRequiredService<ICaseAccessPolicy>();
            var operations = new[] { CaseOperation.ReadCase, CaseOperation.ReadAttachment,
                additionalOperation ?? CaseOperation.ReadAttachment }.Distinct().ToArray();
            if (!(await policy.AuthorizeAllAsync(actor, caseId, operations, ct)).IsAllowed)
                return Result<T>(UiCapabilityResultCode.Denied);
            var aggregate = await services.GetRequiredService<ICaseCommandPersistence>().ReplayCaseAsync(lease, ct);
            var result = await read(services, aggregate);
            await lease.CommitAsync(ct);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is UnauthorizedAccessException or KeyNotFoundException)
        { return Result<T>(UiCapabilityResultCode.Denied); }
        catch (Exception error)
        {
            logger.LogWarning(error, "Authorized original evidence read failed.");
            return Result<T>(UiCapabilityResultCode.Unavailable);
        }
    }

    internal static async Task<byte[]> ReadBoundedAsync(Stream stream, long expectedLength, CancellationToken ct)
    {
        if (expectedLength is <= 0 or > CaseEvidenceUploadRequest.MaximumContentBytes) throw new InvalidDataException("Invalid file size.");
        var bytes = new byte[checked((int)expectedLength)];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var count = await stream.ReadAsync(bytes.AsMemory(offset), ct);
            if (count == 0) throw new InvalidDataException("Incomplete original file.");
            offset += count;
        }
        if (await stream.ReadAsync(new byte[1], ct) != 0) throw new InvalidDataException("File exceeds its declared size.");
        return bytes;
    }

    internal static string? DetectContentType(string fileName, byte[] bytes)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension is ".jpg" or ".jpeg" && bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xd8, 0xff })) return "image/jpeg";
        if (extension == ".png" && bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
        if (extension == ".pdf" && bytes.AsSpan().StartsWith("%PDF-"u8)) return "application/pdf";
        if (extension != ".txt") return null;
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes);
            return text.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t'))
                ? null : "text/plain; charset=utf-8";
        }
        catch (DecoderFallbackException) { return null; }
    }

    private static CaseObservationRecorded? Find(CaseAggregate aggregate, string key) =>
        Guid.TryParse(key, out var id) ? aggregate.Events.OfType<CaseObservationRecorded>()
            .SingleOrDefault(row => row.ObservationId == id && row.Attachment is not null) : null;

    private static CaseEvidenceReference ToReference(CaseObservationRecorded row) => new(row.ObservationId.ToString("D"),
        row.Attachment!.FileName, CaseEvidenceKind.File, row.RecordedAtUtc, CaseEvidenceLocalMediaState.NotAvailableOnDevice, CaseEvidenceUploadState.Uploaded);

    private static CaseEvidenceContentMetadata Metadata(Guid caseId, long version, CaseObservationRecorded row) =>
        new(caseId, row.ObservationId.ToString("D"), row.Attachment!.FileName, row.Attachment.ContentType,
            row.Attachment.ByteLength, row.Attachment.Sha256, version);

    private static UiCapabilityResult<T> Result<T>(UiCapabilityResultCode code, T? value = default) =>
        new(code, value, new("case-evidence", UiCapabilityResults.ContractVersion, UiCapabilityProvenance.LiveService,
            DateTimeOffset.UtcNow, code == UiCapabilityResultCode.Success));
}
