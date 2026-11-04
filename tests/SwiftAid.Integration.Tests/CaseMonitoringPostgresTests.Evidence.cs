using Microsoft.Extensions.Logging;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIddict.EntityFrameworkCore;
using SwiftAid.Application.Abstractions;
using SwiftAid.Application.Attachments;
using SwiftAid.Application.Authorization;
using SwiftAid.Application.Cases.Commands;
using SwiftAid.Application.Serialization;
using SwiftAid.Client.Presentation;
using SwiftAid.Gateway.Cases;
using SwiftAid.Infrastructure.Authorization;
using SwiftAid.Infrastructure.Persistence;
using SwiftAid.Infrastructure.Persistence.Processing;
using SwiftAid.Infrastructure.Persistence.Protection;
using SwiftAid.Infrastructure.Persistence.Transactions;

namespace SwiftAid.Integration.Tests;

public sealed partial class CaseMonitoringPostgresTests
{
    [IntakePostgresFact]
    public async Task Evidence_and_observations_persist_replay_and_reauthorize()
    {
        await using var db = await IntakeDecisionStoreTests.CreateMigratedIntakeContextAsync();
        var grantId = await SeedMonitoringScopeAsync(db, [CaseOperation.ReadCase, CaseOperation.ReadAttachment, CaseOperation.RecordObservation]);
        using var keys = SyntheticMonitoringKeyRing.Create();
        var protectionOptions = keys.CreateOptions();
        using var protection = ProtectedPayloadDataProtectionProviderFactory.Create(protectionOptions, new TestHostEnvironment(Environments.Production));
        var caseId = await SeedMonitoredCaseAsync(db, protectionOptions, protection);
        await using var cleanup = new CaseMonitoringCleanup(caseId, grantId);
        var objects = new EvidenceMemoryObjects();
        var services = new ServiceCollection();
        services.AddScoped(_ => new SwiftAidDbContext(new DbContextOptionsBuilder<SwiftAidDbContext>()
            .UseNpgsql(db.Database.GetConnectionString()).UseOpenIddict().Options));
        services.AddScoped<PostgresCaseCommandUnitOfWork>();
        services.AddScoped<ICaseCommandUnitOfWork>(sp => sp.GetRequiredService<PostgresCaseCommandUnitOfWork>());
        services.AddScoped<ICaseCommandLeaseAccessor>(sp => sp.GetRequiredService<PostgresCaseCommandUnitOfWork>());
        services.AddScoped<IProtectedCaseEventPayloadStore>(sp => new PostgresProtectedCaseEventPayloadStore(
            sp.GetRequiredService<SwiftAidDbContext>(), sp.GetRequiredService<ICaseCommandLeaseAccessor>(), protectionOptions, protection));
        services.AddScoped<ICaseCommandPersistence, PostgresCaseCommandPersistence>();
        services.AddSingleton<CaseEventSerializer>();
        services.AddScoped<ICurrentAccessGrantStore, PostgresCurrentAccessGrantStore>();
        services.AddScoped<ICaseAccessDescriptorStore, PostgresCaseAccessDescriptorStore>();
        services.AddScoped<ICaseAccessPolicy, CaseAccessPolicy>();
        services.AddScoped<ICaseCommandProcessor, CaseCommandProcessor>();
        services.AddSingleton<IObjectStorage>(objects);
        services.AddSingleton<IPrivateMediaObjectStore>(objects);
        await using var provider = services.BuildServiceProvider();
        var scopes = provider.GetRequiredService<IServiceScopeFactory>();
        var auth = new EvidenceAuthentication();
        var evidence = new CaseEvidenceGatewayClient(auth, scopes, NullLogger<CaseEvidenceGatewayClient>.Instance);
        var observations = new CaseObservationGatewayClient(auth, scopes, NullLogger<CaseObservationGatewayClient>.Instance);
        var request = new CaseEvidenceUploadRequest(caseId, Guid.NewGuid(), 2, "visit.txt", "text/plain", 4);
        var uploaded = await evidence.UploadAsync(request, new MemoryStream("note"u8.ToArray()));
        Assert.Equal(UiCapabilityResultCode.Success, uploaded.Code);
        Assert.Equal(3, uploaded.Value!.AggregateVersion);
        var replay = await evidence.UploadAsync(request, new MemoryStream("note"u8.ToArray()));
        Assert.Equal(uploaded.Value, replay.Value);
        Assert.Equal(1, objects.Stored);
        var conflict = await evidence.UploadAsync(request, new MemoryStream("edit"u8.ToArray()));
        Assert.Equal(UiCapabilityResultCode.Conflict, conflict.Code);
        await using var output = new MemoryStream();
        var downloaded = await evidence.DownloadToAsync(caseId, uploaded.Value.Evidence.EvidenceKey, output);
        Assert.True(downloaded.Succeeded);
        Assert.Equal("note"u8.ToArray(), output.ToArray());
        var list = await evidence.GetAsync(caseId, new(new()));
        Assert.Single(list.Value!.Items.Items);
        Assert.DoesNotContain("ObjectId", System.Text.Json.JsonSerializer.Serialize(list.Value), StringComparison.Ordinal);
        var note = new CaseObservationCommand(caseId, Guid.NewGuid(), 3, CaseObservationCategory.Environmental,
            "Water point damaged.", "Site visit", [], null);
        var recorded = await observations.RecordAsync(note);
        Assert.True(recorded.Succeeded);
        var notes = await observations.GetAsync(caseId, new());
        Assert.Contains(notes.Value!.Items.Items, row => row.Category == CaseObservationCategory.Environmental && row.Observation == note.Observation);
        using var logs = LoggerFactory.Create(builder => builder.AddConsole());
        var discussions = new CaseDiscussionGatewayClient(auth, scopes, logs.CreateLogger<CaseDiscussionGatewayClient>());
        var post = new CaseDiscussionPostCommand(caseId, Guid.NewGuid(), 4, "Supplies delivered.\r\nPlease confirm.", [], [uploaded.Value.Evidence.EvidenceKey]);
        var posted = await discussions.AppendAsync(post);
        Assert.Equal(UiCapabilityResultCode.Success, posted.Code);
        Assert.Equal(5, posted.Value!.AggregateVersion);
        Assert.Equal(posted.Value, (await discussions.AppendAsync(post)).Value);
        Assert.Equal(UiCapabilityResultCode.Conflict, (await discussions.AppendAsync(post with { Message = "Changed" })).Code);
        var thread = await discussions.GetAsync(caseId, new(new()));
        Assert.Equal(post.GetCanonicalMessage(), Assert.Single(thread.Value!.Messages.Items).Message);
        Assert.Equal(UiCapabilityResultCode.Conflict, (await discussions.AppendAsync(post with
        {
            SubmissionId = Guid.NewGuid(), ExpectedVersion = 5, EvidenceKeys = [Guid.NewGuid().ToString("D")]
        })).Code);
        await db.AccessGrantGroups.Where(row => row.GrantGroupId == grantId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, PersistentGrantStatus.Revoked));
        await using var deniedOutput = new MemoryStream();
        Assert.Equal(UiCapabilityResultCode.Denied, (await evidence.DownloadToAsync(caseId, request.SubmissionId.ToString("D"), deniedOutput)).Code);
        Assert.Equal(0, deniedOutput.Length);
        Assert.Equal(UiCapabilityResultCode.Denied, (await discussions.GetAsync(caseId, new(new()))).Code);
        Assert.Equal(UiCapabilityResultCode.Denied, (await discussions.AppendAsync(post)).Code);
    }

    private sealed class EvidenceAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "officer-1"), new Claim(ActorContext.AccessEpochClaimType, "7")], "fixture"))));
    }

    private sealed class EvidenceMemoryObjects : IObjectStorage, IPrivateMediaObjectStore
    {
        private readonly Dictionary<Guid, byte[]> _objects = [];
        public int Stored { get; private set; }
        public async ValueTask<ObjectStorageResult> StoreAsync(Stream content, string contentType, CancellationToken cancellationToken)
        {
            using var bytes = new MemoryStream();
            await content.CopyToAsync(bytes, cancellationToken);
            var reference = new ObjectReference(Guid.NewGuid());
            _objects.Add(reference.Id, bytes.ToArray());
            Stored++;
            return new(ObjectStorageStatus.Succeeded, reference);
        }
        public async ValueTask<ObjectStorageResult> CopyToAsync(ObjectReference reference, Stream destination, CancellationToken cancellationToken)
        {
            await destination.WriteAsync(_objects[reference.Id], cancellationToken);
            return new(ObjectStorageStatus.Succeeded, reference);
        }
        public ValueTask<AttachmentRangeContent?> OpenReadAsync(ObjectReference reference, AttachmentByteRange range, CancellationToken cancellationToken) =>
            ValueTask.FromResult<AttachmentRangeContent?>(new(new MemoryStream(_objects[reference.Id]), "text/plain; charset=utf-8", range, _objects[reference.Id].Length));
    }
}
