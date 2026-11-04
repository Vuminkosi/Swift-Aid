using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace SwiftAid.Client.LocalStore;

/// <summary>Encrypted local-only drafts, independent of the replaceable Datasync projection.</summary>
public sealed class SqliteDraftStore(
    IDbContextFactory<LocalDraftDbContext> contextFactory,
    ILocalDraftAccessContextProvider accessContextProvider,
    ILocalDraftKeyStore keyStore,
    TimeProvider? timeProvider = null) : IDraftStore
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task SaveAsync(ReportDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        draft.Validate();
        var access = await RequireAccessAsync(draft.WorkspaceId, cancellationToken).ConfigureAwait(false);

        // Draft DTOs are immutable and use immutable collections, so this validated snapshot is stable
        // through encryption and the database commit.
        var key = await GetKeyAsync(access.ActorId, cancellationToken).ConfigureAwait(false);
        byte[] plaintext = [];
        byte[] nonce = [];
        byte[] ciphertext = [];
        byte[] tag = [];
        try
        {
            plaintext = JsonSerializer.SerializeToUtf8Bytes(draft, JsonOptions);
            nonce = RandomNumberGenerator.GetBytes(NonceSize);
            ciphertext = new byte[plaintext.Length];
            tag = new byte[TagSize];
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, CreateAssociatedData(
                access.ActorId,
                draft.DraftId,
                EncryptedDraftRow.CurrentEncryptionVersion,
                access.AccessEpoch,
                draft.WorkspaceId));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (plaintext.Length > 0) CryptographicOperations.ZeroMemory(plaintext);
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var id = draft.DraftId.ToString("D");
        var existing = await db.Drafts.SingleOrDefaultAsync(row => row.DraftId == id, cancellationToken).ConfigureAwait(false);
        if (existing is not null &&
            (!string.Equals(existing.OwnerActorId, access.ActorId, StringComparison.Ordinal) ||
             !string.Equals(existing.WorkspaceId, draft.WorkspaceId, StringComparison.Ordinal) ||
             existing.AccessEpoch != access.AccessEpoch))
        {
            throw new LocalDraftAccessDeniedException();
        }

        // Version 1 rows were encrypted without binding the authorization epoch. Preserve them
        // for explicit recovery instead of silently replacing their ciphertext on a later save.
        if (existing is not null && existing.SchemaVersion != EncryptedDraftRow.CurrentEncryptionVersion)
        {
            throw new LocalDraftIntegrityException();
        }

        var row = existing ?? new EncryptedDraftRow { DraftId = id };
        row.OwnerActorId = access.ActorId;
        row.WorkspaceId = draft.WorkspaceId;
        row.SchemaVersion = EncryptedDraftRow.CurrentEncryptionVersion;
        row.AccessEpoch = access.AccessEpoch;
        row.Nonce = nonce;
        row.Ciphertext = ciphertext;
        row.Tag = tag;
        if (existing is null) db.Drafts.Add(row);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await RequireSameAccessAsync(access, draft.WorkspaceId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ReportDraft?> GetAsync(Guid draftId, CancellationToken cancellationToken)
    {
        if (draftId == Guid.Empty) throw new ArgumentException("A draft identifier is required.", nameof(draftId));
        var access = await RequireAccessAsync(workspaceId: null, cancellationToken).ConfigureAwait(false);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);

        var row = await db.Drafts.AsNoTracking()
            .SingleOrDefaultAsync(value => value.DraftId == draftId.ToString("D"), cancellationToken)
            .ConfigureAwait(false);
        if (row is null) return null;
        if (!string.Equals(row.OwnerActorId, access.ActorId, StringComparison.Ordinal) ||
            !access.PermittedWorkspaceIds.Contains(row.WorkspaceId, StringComparer.Ordinal) ||
            row.AccessEpoch != access.AccessEpoch)
        {
            return null;
        }

        if (row.SchemaVersion != EncryptedDraftRow.CurrentEncryptionVersion || row.Nonce is null || row.Tag is null ||
            row.Ciphertext is null || row.Nonce.Length != NonceSize || row.Tag.Length != TagSize)
            throw new LocalDraftIntegrityException();

        var key = await GetKeyAsync(access.ActorId, cancellationToken).ConfigureAwait(false);
        byte[] plaintext = [];
        try
        {
            plaintext = new byte[row.Ciphertext.Length];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(row.Nonce, row.Ciphertext, row.Tag, plaintext, CreateAssociatedData(
                row.OwnerActorId,
                draftId,
                row.SchemaVersion,
                row.AccessEpoch,
                row.WorkspaceId));
            var result = JsonSerializer.Deserialize<ReportDraft>(plaintext, JsonOptions);
            if (result is null || result.DraftId != draftId ||
                !string.Equals(result.WorkspaceId, row.WorkspaceId, StringComparison.Ordinal))
            {
                throw new LocalDraftIntegrityException();
            }
            result.Validate();

            await RequireSameAccessAsync(access, row.WorkspaceId, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (CryptographicException)
        {
            throw new LocalDraftIntegrityException();
        }
        catch (JsonException)
        {
            throw new LocalDraftIntegrityException();
        }
        catch (ArgumentException)
        {
            throw new LocalDraftIntegrityException();
        }
        catch (NotSupportedException)
        {
            throw new LocalDraftIntegrityException();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (plaintext.Length > 0) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private async ValueTask<LocalDraftAccessContext> RequireAccessAsync(string? workspaceId, CancellationToken cancellationToken)
    {
        var context = await accessContextProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (!IsUsable(context) || (workspaceId is not null &&
            !context!.PermittedWorkspaceIds.Contains(workspaceId, StringComparer.Ordinal)))
        {
            throw new LocalDraftAccessDeniedException();
        }
        return context! with { PermittedWorkspaceIds = context.PermittedWorkspaceIds.ToArray() };
    }

    private async ValueTask RequireSameAccessAsync(LocalDraftAccessContext prior, string workspaceId, CancellationToken cancellationToken)
    {
        var current = await accessContextProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (!IsUsable(current) || !string.Equals(prior.ActorId, current!.ActorId, StringComparison.Ordinal) ||
            prior.AccessEpoch != current.AccessEpoch ||
            !current.PermittedWorkspaceIds.Contains(workspaceId, StringComparer.Ordinal))
        {
            throw new LocalDraftAccessDeniedException();
        }
    }

    private bool IsUsable(LocalDraftAccessContext? context) =>
        context is not null &&
        context.IsProvisioned &&
        context.IsActive &&
        !string.IsNullOrWhiteSpace(context.ActorId) &&
        context.ActorId.Length <= 200 &&
        context.AccessEpoch >= 0 &&
        context.ExpiresAtUtc.Offset == TimeSpan.Zero &&
        context.ExpiresAtUtc > _timeProvider.GetUtcNow() &&
        context.PermittedWorkspaceIds is not null &&
        context.PermittedWorkspaceIds.All(workspace => !string.IsNullOrWhiteSpace(workspace) && workspace.Length <= 200);

    private async ValueTask<byte[]> GetKeyAsync(string actorId, CancellationToken cancellationToken)
    {
        var key = await keyStore.GetOrCreateKeyAsync(actorId, cancellationToken).ConfigureAwait(false);
        if (key is null || key.Length != 32)
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            throw new LocalDraftKeyUnavailableException();
        }
        return key;
    }

    private static byte[] CreateAssociatedData(
        string actorId,
        Guid draftId,
        int encryptionVersion,
        long accessEpoch,
        string workspaceId)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("SwiftAid.LocalDraft");
            writer.Write(encryptionVersion);
            writer.Write(actorId);
            writer.Write(draftId.ToByteArray());
            writer.Write(accessEpoch);
            writer.Write(workspaceId);
        }
        return stream.ToArray();
    }
}

public interface IDraftStore
{
    Task SaveAsync(ReportDraft draft, CancellationToken cancellationToken);
    Task<ReportDraft?> GetAsync(Guid draftId, CancellationToken cancellationToken);
}
